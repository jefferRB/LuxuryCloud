using System.Text.Json;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Reservas
{
    /// <summary>
    /// Aviso al cliente cuando el negocio RECHAZA su solicitud de reserva online.
    ///
    /// <para>
    /// Reutiliza el mismo template aprobado que la cancelación de una cita
    /// (<c>luxurycloud_cancelacion_cita</c>) a través de <see cref="IWhatsAppCancellationNotifier"/>:
    /// para el cliente es el mismo hecho — la hora que pidió no va a suceder. No se crea ninguna
    /// cita para poder avisarle.
    /// </para>
    ///
    /// <para>
    /// La elegibilidad se resuelve acá y no en el notifier porque el consentimiento de una solicitud
    /// vive en la propia solicitud (<c>BookingRequest.AceptaWhatsApp</c>, la casilla del formulario
    /// público), mientras que el de una cita depende del cliente registrado.
    /// </para>
    /// </summary>
    public interface IBookingRejectionWhatsAppService
    {
        /// <summary>
        /// Avisa el rechazo. Se llama DESPUÉS de que la solicitud quedó Rejected en base de datos:
        /// un fallo de Meta no revierte el rechazo. Nunca lanza.
        /// </summary>
        /// <returns>Motivo semántico del resultado, para trazas y para la UI.</returns>
        Task<WhatsAppNotificationReason> NotifyRejectionAsync(
            int bookingRequestId,
            CancellationToken cancellationToken = default);
    }

    /// <inheritdoc cref="IBookingRejectionWhatsAppService"/>
    public sealed class BookingRejectionWhatsAppService : IBookingRejectionWhatsAppService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly ApplicationDbContext _context;
        private readonly IMetaWhatsAppClient _metaClient;
        private readonly IOptionsMonitor<MetaWhatsAppOptions> _options;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITenantWhatsAppSettingsService _tenantSettingsService;
        private readonly IWhatsAppCancellationNotifier _cancellationNotifier;
        private readonly ILogger<BookingRejectionWhatsAppService> _logger;

        public BookingRejectionWhatsAppService(
            ApplicationDbContext context,
            IMetaWhatsAppClient metaClient,
            IOptionsMonitor<MetaWhatsAppOptions> options,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITenantWhatsAppSettingsService tenantSettingsService,
            IWhatsAppCancellationNotifier cancellationNotifier,
            ILogger<BookingRejectionWhatsAppService> logger)
        {
            _context = context;
            _metaClient = metaClient;
            _options = options;
            _businessDateTimeProvider = businessDateTimeProvider;
            _tenantSettingsService = tenantSettingsService;
            _cancellationNotifier = cancellationNotifier;
            _logger = logger;
        }

        public async Task<WhatsAppNotificationReason> NotifyRejectionAsync(
            int bookingRequestId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // La solicitud se lee con el filtro de tenant activo: el TenantId y el teléfono del
                // negocio salen de la entidad, nunca de quien llama.
                var solicitud = await _context.BookingRequests
                    .AsNoTracking()
                    .Where(request => request.Id == bookingRequestId)
                    .Select(request => new RejectedBookingSnapshot(
                        request.TenantId,
                        request.Estado,
                        request.NombreCliente,
                        request.TelefonoCliente,
                        request.Servicio != null ? request.Servicio.Nombre : null,
                        request.FechaHoraInicioSolicitada,
                        request.RejectedReason,
                        request.AceptaWhatsApp))
                    .FirstOrDefaultAsync(cancellationToken);

                if (solicitud is null || solicitud.Estado != BookingRequestStates.Rejected)
                {
                    return WhatsAppNotificationReason.NotApplicable;
                }

                return await NotifyAsync(bookingRequestId, solicitud, cancellationToken);
            }
            catch (Exception ex)
            {
                // El rechazo ya está confirmado: WhatsApp jamás lo revierte.
                _logger.LogError(
                    ex,
                    "Error inesperado avisando el rechazo por WhatsApp. BookingRequestId {BookingRequestId}.",
                    bookingRequestId);
                return WhatsAppNotificationReason.Other;
            }
        }

        private async Task<WhatsAppNotificationReason> NotifyAsync(
            int bookingRequestId,
            RejectedBookingSnapshot solicitud,
            CancellationToken cancellationToken)
        {
            // Sin complemento contratado no hay flujo de WhatsApp: se sale en silencio, sin fila de
            // log y sin avisos. No es un error ni una falta de consentimiento del cliente.
            if (!await _tenantSettingsService.HasActiveWhatsAppAddonAsync(solicitud.TenantId, cancellationToken))
            {
                _logger.LogDebug(
                    "Aviso de rechazo omitido en silencio: el negocio no tiene el complemento activo. TenantId {TenantId}. BookingRequestId {BookingRequestId}.",
                    solicitud.TenantId,
                    bookingRequestId);
                return WhatsAppNotificationReason.AddonInactive;
            }

            // Consentimiento de ESTA reserva: la casilla que el cliente marcó en el formulario
            // público. Si no autorizó, no se le escribe.
            if (!solicitud.AceptaWhatsApp)
            {
                await RegisterSkippedAsync(
                    bookingRequestId,
                    solicitud,
                    WhatsAppMessageStatuses.SkippedConsentMissing,
                    WhatsAppErrorCodes.ConsentMissing,
                    "El cliente no autorizó notificaciones por WhatsApp.",
                    cancellationToken);
                return WhatsAppNotificationReason.ConsentMissing;
            }

            var phoneE164 = _metaClient.NormalizePhoneNumber(solicitud.TelefonoCliente);
            if (phoneE164 is null)
            {
                await RegisterSkippedAsync(
                    bookingRequestId,
                    solicitud,
                    WhatsAppMessageStatuses.SkippedInvalidPhone,
                    WhatsAppErrorCodes.InvalidPhone,
                    "Telefono invalido.",
                    cancellationToken);
                return WhatsAppNotificationReason.CustomerPhoneMissing;
            }

            // Misma política comercial y de cuotas que confirmaciones, recordatorios y cancelaciones.
            var decision = await _tenantSettingsService.CanSendNotificationAsync(
                solicitud.TenantId,
                WhatsAppNotificationTypes.Cancellation,
                cancellationToken: cancellationToken);

            if (!decision.CanSend)
            {
                await RegisterSkippedAsync(
                    bookingRequestId,
                    solicitud,
                    WhatsAppNotificationReasons.ToSkippedStatus(decision.ErrorCode),
                    decision.ErrorCode ?? WhatsAppErrorCodes.ConfigurationDisabled,
                    decision.ErrorMessage ?? "El mensaje WhatsApp fue omitido por configuracion.",
                    cancellationToken);
                return WhatsAppNotificationReasons.FromErrorCode(decision.ErrorCode);
            }

            var parameters = await _cancellationNotifier.BuildParametersAsync(
                new WhatsAppCancellationSubject(
                    solicitud.TenantId,
                    solicitud.NombreCliente,
                    solicitud.ServicioNombre,
                    solicitud.FechaHoraInicioSolicitada,
                    solicitud.RejectedReason,
                    BookingRejectionDefaults.MotivoPorDefecto),
                cancellationToken);

            var nowUtc = _businessDateTimeProvider.NowOffset().UtcDateTime;
            var log = new WhatsAppMessageLog
            {
                // Una solicitud rechazada nunca llegó a ser cita: la bitácora queda sin CitaId y la
                // solicitud viaja en el payload.
                CitaId = null,
                Direction = WhatsAppMessageDirections.Outbound,
                NotificationType = WhatsAppNotificationTypes.Cancellation,
                Provider = WhatsAppProviders.Meta,
                RecipientPhoneE164 = phoneE164,
                TemplateName = _options.CurrentValue.CancellationTemplateName,
                Status = WhatsAppMessageStatuses.Pending,
                PayloadJson = BuildPayloadJson("queued", bookingRequestId, solicitud, null),
                CreatedAtUtc = nowUtc
            };

            _context.WhatsAppMessageLogs.Add(log);
            await _context.SaveChangesAsync(cancellationToken);

            var sendResult = await _cancellationNotifier.SendAsync(
                new WhatsAppCancellationDispatch(
                    log.Id,
                    solicitud.TenantId,
                    phoneE164,
                    parameters,
                    WhatsAppCancellationSource.BookingRequest,
                    bookingRequestId),
                cancellationToken);

            if (sendResult.Success)
            {
                _logger.LogInformation(
                    "Aviso de rechazo enviado por WhatsApp. TenantId {TenantId}. BookingRequestId {BookingRequestId}.",
                    solicitud.TenantId,
                    bookingRequestId);
                return WhatsAppNotificationReason.Eligible;
            }

            return WhatsAppNotificationReason.ProviderFailed;
        }

        private async Task RegisterSkippedAsync(
            int bookingRequestId,
            RejectedBookingSnapshot solicitud,
            string status,
            string errorCode,
            string reason,
            CancellationToken cancellationToken)
        {
            var nowUtc = _businessDateTimeProvider.NowOffset().UtcDateTime;

            _context.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
            {
                CitaId = null,
                Direction = WhatsAppMessageDirections.Outbound,
                NotificationType = WhatsAppNotificationTypes.Cancellation,
                Provider = WhatsAppProviders.Meta,
                RecipientPhoneE164 = _metaClient.NormalizePhoneNumber(solicitud.TelefonoCliente),
                TemplateName = _options.CurrentValue.CancellationTemplateName,
                Status = status,
                ErrorCode = errorCode,
                ErrorMessage = WhatsAppTemplateText.Sanitize(reason, 1000),
                PayloadJson = BuildPayloadJson("skipped", bookingRequestId, solicitud, errorCode),
                CreatedAtUtc = nowUtc,
                ProcessedAtUtc = nowUtc
            });

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Aviso de rechazo omitido por regla de WhatsApp. TenantId {TenantId}. BookingRequestId {BookingRequestId}. Motivo {SkipReason}.",
                solicitud.TenantId,
                bookingRequestId,
                errorCode);
        }

        private static string BuildPayloadJson(
            string phase,
            int bookingRequestId,
            RejectedBookingSnapshot solicitud,
            string? reason) =>
            JsonSerializer.Serialize(new
            {
                phase,
                reason,
                notificationType = WhatsAppNotificationTypes.Cancellation,
                source = WhatsAppCancellationSource.BookingRequest.ToString(),
                sourceId = bookingRequestId,
                fechaSolicitada = solicitud.FechaHoraInicioSolicitada,
                aceptaWhatsApp = solicitud.AceptaWhatsApp
            }, JsonOptions);

        /// <summary>Proyección mínima de la solicitud rechazada. Evita traer la entidad completa.</summary>
        private sealed record RejectedBookingSnapshot(
            Guid TenantId,
            string Estado,
            string? NombreCliente,
            string? TelefonoCliente,
            string? ServicioNombre,
            DateTime FechaHoraInicioSolicitada,
            string? RejectedReason,
            bool AceptaWhatsApp);
    }
}
