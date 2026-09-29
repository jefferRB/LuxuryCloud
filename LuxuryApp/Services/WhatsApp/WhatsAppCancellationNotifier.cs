using System.Globalization;
using System.Text;
using System.Text.Json;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Tenant;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.WhatsApp
{
    /// <summary>De dónde nace el aviso de cancelación. Solo para trazas y payload del log.</summary>
    public enum WhatsAppCancellationSource
    {
        /// <summary>Una cita ya agendada que el negocio cancela.</summary>
        Cita,

        /// <summary>Una solicitud de reserva online que el negocio rechaza antes de agendarla.</summary>
        BookingRequest
    }

    /// <summary>
    /// Datos de negocio necesarios para armar <c>luxurycloud_cancelacion_cita</c>, sin atarse a la
    /// entidad de origen: una cita y una solicitud de reserva rechazada dicen exactamente lo mismo
    /// al cliente, solo cambia de dónde salen los datos.
    /// </summary>
    /// <param name="FechaHoraLocal">Hora local del negocio, igual que <c>Cita.FechaHoraCita</c>.</param>
    /// <param name="DefaultReason">Texto de {{6}} cuando no llega un motivo utilizable.</param>
    public sealed record WhatsAppCancellationSubject(
        Guid TenantId,
        string? CustomerName,
        string? ServiceName,
        DateTime FechaHoraLocal,
        string? Reason,
        string DefaultReason);

    /// <summary>Envío concreto: la fila del log ya reservada más el destinatario y los parámetros.</summary>
    /// <param name="RelatedId">Id secundario para trazas (p. ej. la reserva que originó la cita).</param>
    public sealed record WhatsAppCancellationDispatch(
        long MessageLogId,
        Guid TenantId,
        string RecipientPhoneE164,
        WhatsAppCancellationTemplateParameters Parameters,
        WhatsAppCancellationSource Source,
        int SourceId,
        int? RelatedId = null);

    /// <summary>
    /// Arma y envía <c>luxurycloud_cancelacion_cita</c>. Es el único lugar que conoce el template:
    /// quien cancela una cita y quien rechaza una solicitud comparten construcción de parámetros,
    /// saneamiento, teléfono público del negocio y cierre del <c>WhatsAppMessageLog</c>.
    ///
    /// <para>
    /// NO decide elegibilidad: complemento, consentimiento, teléfono del cliente y cuotas los
    /// resuelve cada flujo con las políticas que ya existen, porque el origen del consentimiento
    /// difiere entre una cita y una solicitud.
    /// </para>
    /// </summary>
    public interface IWhatsAppCancellationNotifier
    {
        Task<WhatsAppCancellationTemplateParameters> BuildParametersAsync(
            WhatsAppCancellationSubject subject,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Envía y cierra la fila del log (Sent/Failed). No lanza nunca: la operación principal
        /// (cancelar la cita o rechazar la solicitud) ya está confirmada y no se revierte.
        /// </summary>
        Task<MetaWhatsAppSendResult> SendAsync(
            WhatsAppCancellationDispatch dispatch,
            CancellationToken cancellationToken = default);
    }

    /// <inheritdoc cref="IWhatsAppCancellationNotifier"/>
    public sealed class WhatsAppCancellationNotifier : IWhatsAppCancellationNotifier
    {
        /// <summary>
        /// Texto de {{8}} cuando el negocio todavía no cargó su teléfono público. No se bloquea el
        /// aviso por esto: el cliente con consentimiento debe enterarse igual.
        /// </summary>
        public const string MissingBusinessPhoneFallback = "mismo número de siempre";

        /// <summary>Tope del motivo. Meta admite más, pero un párrafo largo no aporta y sí rompe la lectura.</summary>
        public const int MaxReasonLength = 300;

        private static readonly CultureInfo CostaRica = CultureInfo.GetCultureInfo("es-CR");

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly ApplicationDbContext _context;
        private readonly IMetaWhatsAppClient _metaClient;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;
        private readonly ILogger<WhatsAppCancellationNotifier> _logger;

        public WhatsAppCancellationNotifier(
            ApplicationDbContext context,
            IMetaWhatsAppClient metaClient,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITenantDisplayNameService tenantDisplayNameService,
            ILogger<WhatsAppCancellationNotifier> logger)
        {
            _context = context;
            _metaClient = metaClient;
            _businessDateTimeProvider = businessDateTimeProvider;
            _tenantDisplayNameService = tenantDisplayNameService;
            _logger = logger;
        }

        public async Task<WhatsAppCancellationTemplateParameters> BuildParametersAsync(
            WhatsAppCancellationSubject subject,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(subject);

            var businessName = WhatsAppTemplateText.Sanitize(
                await _tenantDisplayNameService.GetTenantDisplayNameAsync(subject.TenantId, cancellationToken))
                ?? "el negocio";

            var businessPhone = await ResolveBusinessPublicPhoneAsync(cancellationToken);
            if (businessPhone is null)
            {
                _logger.LogWarning(
                    "El negocio no tiene telefono publico configurado; el aviso de cancelacion sale con texto de relleno en la variable 8. TenantId {TenantId}.",
                    subject.TenantId);
                businessPhone = MissingBusinessPhoneFallback;
            }

            return new WhatsAppCancellationTemplateParameters
            {
                CustomerName = WhatsAppTemplateText.Sanitize(subject.CustomerName) ?? "Cliente",
                BusinessName = businessName,
                ServiceName = WhatsAppTemplateText.Sanitize(subject.ServiceName) ?? "el servicio reservado",
                AppointmentDate = subject.FechaHoraLocal.ToString("dd/MM/yyyy", CostaRica),
                AppointmentTime = subject.FechaHoraLocal.ToString("hh:mm tt", CostaRica),
                CancellationReason = WhatsAppTemplateText.Sanitize(subject.Reason, MaxReasonLength)
                    ?? subject.DefaultReason,
                // Mismo nombre a propósito: Meta no deja repetir {{2}} en dos posiciones.
                BusinessNameRepeated = businessName,
                BusinessPhone = businessPhone
            };
        }

        public async Task<MetaWhatsAppSendResult> SendAsync(
            WhatsAppCancellationDispatch dispatch,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dispatch);

            var nowUtc = _businessDateTimeProvider.NowOffset().UtcDateTime;

            try
            {
                var sendResult = await _metaClient.SendCancellationTemplateAsync(
                    dispatch.RecipientPhoneE164,
                    dispatch.Parameters,
                    cancellationToken);

                if (sendResult.Success && !string.IsNullOrWhiteSpace(sendResult.MetaMessageId))
                {
                    await _context.WhatsAppMessageLogs
                        .Where(message => message.Id == dispatch.MessageLogId)
                        .ExecuteUpdateAsync(updates => updates
                            .SetProperty(message => message.Status, WhatsAppMessageStatuses.Sent)
                            .SetProperty(message => message.MetaMessageId, sendResult.MetaMessageId)
                            .SetProperty(message => message.SentAtUtc, nowUtc)
                            .SetProperty(message => message.LastAttemptAtUtc, nowUtc)
                            .SetProperty(message => message.ProcessedAtUtc, nowUtc)
                            .SetProperty(message => message.AttemptCount, 1)
                            .SetProperty(message => message.PayloadJson, BuildSendResultPayloadJson(dispatch, sendResult)),
                            cancellationToken);

                    _logger.LogInformation(
                        "Cancelacion WhatsApp enviada. TenantId {TenantId}. Source {Source}. SourceId {SourceId}. RelatedId {RelatedId}. MetaMessageId {MetaMessageId}.",
                        dispatch.TenantId,
                        dispatch.Source,
                        dispatch.SourceId,
                        dispatch.RelatedId,
                        sendResult.MetaMessageId);

                    return sendResult;
                }

                await MarkFailedAsync(dispatch, sendResult, nowUtc, cancellationToken);

                _logger.LogWarning(
                    "Fallo el envio de la cancelacion WhatsApp. TenantId {TenantId}. Source {Source}. SourceId {SourceId}. ErrorCode {ErrorCode}.",
                    dispatch.TenantId,
                    dispatch.Source,
                    dispatch.SourceId,
                    sendResult.ErrorCode);

                return sendResult;
            }
            catch (Exception ex)
            {
                // La operación principal ya está confirmada en base de datos: WhatsApp jamás la
                // revierte. Solo se deja constancia del fallo.
                _logger.LogError(
                    ex,
                    "Error inesperado enviando la cancelacion WhatsApp. TenantId {TenantId}. Source {Source}. SourceId {SourceId}.",
                    dispatch.TenantId,
                    dispatch.Source,
                    dispatch.SourceId);

                var failure = MetaWhatsAppSendResult.Failed("UNEXPECTED_ERROR", ex.Message);

                try
                {
                    await MarkFailedAsync(dispatch, failure, nowUtc, cancellationToken);
                }
                catch (Exception logEx)
                {
                    _logger.LogWarning(
                        logEx,
                        "No fue posible registrar el fallo de la cancelacion WhatsApp. MessageLogId {MessageLogId}.",
                        dispatch.MessageLogId);
                }

                return failure;
            }
        }

        /// <summary>
        /// Teléfono público/habitual del negocio: el de su página pública. Nunca el número central
        /// de automatizaciones de LuxuryCloud. Queda acotado al tenant por el filtro global.
        /// </summary>
        private async Task<string?> ResolveBusinessPublicPhoneAsync(CancellationToken cancellationToken)
        {
            var page = await _context.TenantPublicPages
                .AsNoTracking()
                .Select(current => new { current.WhatsAppPhone, current.Phone })
                .FirstOrDefaultAsync(cancellationToken);

            if (page is null)
            {
                return null;
            }

            return WhatsAppTemplateText.Sanitize(page.WhatsAppPhone) ?? WhatsAppTemplateText.Sanitize(page.Phone);
        }

        private Task MarkFailedAsync(
            WhatsAppCancellationDispatch dispatch,
            MetaWhatsAppSendResult sendResult,
            DateTime nowUtc,
            CancellationToken cancellationToken) =>
            _context.WhatsAppMessageLogs
                .Where(message => message.Id == dispatch.MessageLogId)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(message => message.Status, WhatsAppMessageStatuses.Failed)
                    .SetProperty(message => message.ErrorCode, WhatsAppTemplateText.Truncate(sendResult.ErrorCode, 80))
                    .SetProperty(message => message.ErrorMessage, WhatsAppTemplateText.Truncate(sendResult.ErrorMessage, 1000))
                    .SetProperty(message => message.FailedAtUtc, nowUtc)
                    .SetProperty(message => message.LastAttemptAtUtc, nowUtc)
                    .SetProperty(message => message.ProcessedAtUtc, nowUtc)
                    .SetProperty(message => message.AttemptCount, 1)
                    .SetProperty(message => message.PayloadJson, BuildSendResultPayloadJson(dispatch, sendResult)),
                    cancellationToken);

        private static string BuildSendResultPayloadJson(
            WhatsAppCancellationDispatch dispatch,
            MetaWhatsAppSendResult sendResult) =>
            JsonSerializer.Serialize(new
            {
                phase = sendResult.Success ? "sent" : "send_failed",
                notificationType = WhatsAppNotificationTypes.Cancellation,
                source = dispatch.Source.ToString(),
                sourceId = dispatch.SourceId,
                relatedId = dispatch.RelatedId,
                sendResult.MetaMessageId,
                statusCode = sendResult.StatusCode.HasValue ? (int?)sendResult.StatusCode.Value : null,
                sendResult.ErrorCode,
                sendResult.ErrorType,
                sendResult.ErrorSubcode,
                sendResult.ErrorMessage,
                sendResult.FbTraceId,
                sendResult.ShouldRetry,
                sendResult.Endpoint
            }, JsonOptions);
    }

    /// <summary>
    /// Saneamiento de texto para parámetros de plantilla. Meta rechaza saltos de línea, tabuladores
    /// y espacios repetidos, así que todo valor que viaje a una variable pasa por acá.
    /// </summary>
    public static class WhatsAppTemplateText
    {
        public const int MaxParameterLength = 300;

        /// <summary>Colapsa espacios/controles, recorta y limita. Devuelve null si no queda nada útil.</summary>
        public static string? Sanitize(string? value, int maxLength = MaxParameterLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var builder = new StringBuilder(value.Length);
            var previousWasSpace = false;

            foreach (var character in value)
            {
                if (char.IsWhiteSpace(character) || char.IsControl(character))
                {
                    if (builder.Length > 0 && !previousWasSpace)
                    {
                        builder.Append(' ');
                        previousWasSpace = true;
                    }

                    continue;
                }

                builder.Append(character);
                previousWasSpace = false;
            }

            var normalized = builder.ToString().Trim();
            if (normalized.Length == 0)
            {
                return null;
            }

            return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd();
        }

        public static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
