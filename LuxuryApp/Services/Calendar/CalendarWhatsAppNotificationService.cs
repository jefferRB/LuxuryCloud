using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Notifications;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Calendar
{
    public sealed class CalendarWhatsAppNotificationService : ICalendarWhatsAppNotificationService
    {
        private const int MaxAttempts = 3;
        private const int PendingBatchSize = 25;

        private static readonly string[] ActiveOutboundStatuses =
        [
            WhatsAppMessageStatuses.Pending,
            WhatsAppMessageStatuses.Processing,
            WhatsAppMessageStatuses.Sent,
            WhatsAppMessageStatuses.Delivered,
            WhatsAppMessageStatuses.Read
        ];

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly ApplicationDbContext _context;
        private readonly IMetaWhatsAppClient _metaClient;
        private readonly IOptionsMonitor<MetaWhatsAppOptions> _options;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITenantWhatsAppSettingsService _tenantSettingsService;
        private readonly ITenantProvider _tenantProvider;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;
        private readonly TenantExecutionService _tenantExecutionService;
        private readonly ILogger<CalendarWhatsAppNotificationService> _logger;

        public CalendarWhatsAppNotificationService(
            ApplicationDbContext context,
            IMetaWhatsAppClient metaClient,
            IOptionsMonitor<MetaWhatsAppOptions> options,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITenantWhatsAppSettingsService tenantSettingsService,
            ITenantProvider tenantProvider,
            ITenantDisplayNameService tenantDisplayNameService,
            TenantExecutionService tenantExecutionService,
            ILogger<CalendarWhatsAppNotificationService> logger)
        {
            _context = context;
            _metaClient = metaClient;
            _options = options;
            _businessDateTimeProvider = businessDateTimeProvider;
            _tenantSettingsService = tenantSettingsService;
            _tenantProvider = tenantProvider;
            _tenantDisplayNameService = tenantDisplayNameService;
            _tenantExecutionService = tenantExecutionService;
            _logger = logger;
        }

        public async Task SendAppointmentConfirmationAsync(int citaId, CancellationToken cancellationToken = default)
        {
            await QueueAppointmentConfirmationAsync(citaId, cancellationToken);
            await ProcessPendingNotificationsAsync(cancellationToken);
        }

        public async Task SendAppointmentReminderAsync(int citaId, CancellationToken cancellationToken = default)
        {
            await QueueAppointmentReminderAsync(citaId, cancellationToken);
            await ProcessPendingNotificationsAsync(cancellationToken);
        }

        public async Task<WhatsAppConfirmationSendResult> SendConfirmationNowAsync(
            int citaId,
            string source,
            CancellationToken cancellationToken = default)
        {
            // 1. Idempotencia dura: si ya hay una confirmación enviada/entregada/leída, no reenviar.
            var alreadySent = await _context.WhatsAppMessageLogs
                .AsNoTracking()
                .AnyAsync(message =>
                    message.CitaId == citaId &&
                    message.Direction == WhatsAppMessageDirections.Outbound &&
                    message.NotificationType == WhatsAppNotificationTypes.Confirmation &&
                    (message.Status == WhatsAppMessageStatuses.Sent ||
                     message.Status == WhatsAppMessageStatuses.Delivered ||
                     message.Status == WhatsAppMessageStatuses.Read),
                    cancellationToken);

            if (alreadySent)
            {
                _logger.LogInformation(
                    "Confirmacion WhatsApp inmediata omitida: ya estaba enviada. CitaId {CitaId}. Source {Source}.",
                    citaId, source);
                return new WhatsAppConfirmationSendResult(
                    WhatsAppConfirmationOutcome.AlreadySent,
                    "La confirmación de WhatsApp ya había sido enviada.");
            }

            // 2. Si la creación de la cita dejó una confirmación pendiente/programada (modo relativo),
            //    la adelantamos para envío inmediato en vez de crear una nueva (evita duplicados).
            await _context.WhatsAppMessageLogs
                .Where(message =>
                    message.CitaId == citaId &&
                    message.Direction == WhatsAppMessageDirections.Outbound &&
                    message.NotificationType == WhatsAppNotificationTypes.Confirmation &&
                    message.Status == WhatsAppMessageStatuses.Pending)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(message => message.NextAttemptAtUtc, (DateTime?)null),
                    cancellationToken);

            // 3. Encolar forzando envío inmediato. La verificación interna de duplicados evita crear
            //    otra fila si ya existía una pendiente (recién adelantada) o cualquier estado activo.
            await QueueAppointmentAsync(
                citaId,
                WhatsAppNotificationTypes.Confirmation,
                isBatchGenerated: false,
                cancellationToken,
                forceImmediate: true);

            // 4. Procesar la cola para intentar el envío ahora mismo.
            await ProcessPendingNotificationsAsync(cancellationToken);

            // 5. Leer el estado final del log de confirmación para informar honestamente.
            var log = await _context.WhatsAppMessageLogs
                .AsNoTracking()
                .Where(message =>
                    message.CitaId == citaId &&
                    message.Direction == WhatsAppMessageDirections.Outbound &&
                    message.NotificationType == WhatsAppNotificationTypes.Confirmation)
                .OrderByDescending(message => message.Id)
                .Select(message => new { message.Status, message.ErrorCode })
                .FirstOrDefaultAsync(cancellationToken);

            _logger.LogInformation(
                "Confirmacion WhatsApp inmediata procesada. CitaId {CitaId}. Source {Source}. Estado {Status}.",
                citaId, source, log?.Status ?? "SinLog");

            return MapConfirmationResult(log?.Status, log?.ErrorCode);
        }

        private static WhatsAppConfirmationSendResult MapConfirmationResult(string? status, string? errorCode)
        {
            if (status is null)
            {
                return new WhatsAppConfirmationSendResult(
                    WhatsAppConfirmationOutcome.Skipped,
                    "No se envió la confirmación de WhatsApp (no aplicable para esta cita).",
                    Reason: WhatsAppNotificationReason.NotApplicable);
            }

            var reason = WhatsAppNotificationReasons.FromErrorCode(errorCode);

            return status switch
            {
                WhatsAppMessageStatuses.Sent or
                WhatsAppMessageStatuses.Delivered or
                WhatsAppMessageStatuses.Read =>
                    new WhatsAppConfirmationSendResult(
                        WhatsAppConfirmationOutcome.Sent,
                        "Confirmación de WhatsApp enviada.",
                        Reason: WhatsAppNotificationReason.Eligible),

                WhatsAppMessageStatuses.Pending or
                WhatsAppMessageStatuses.Processing =>
                    new WhatsAppConfirmationSendResult(
                        WhatsAppConfirmationOutcome.Pending,
                        "La confirmación de WhatsApp quedó en cola y se enviará en breve.",
                        Reason: WhatsAppNotificationReason.Eligible),

                WhatsAppMessageStatuses.Failed =>
                    new WhatsAppConfirmationSendResult(
                        WhatsAppConfirmationOutcome.Failed,
                        "No se pudo enviar la confirmación de WhatsApp. Podés reintentarla.",
                        errorCode,
                        WhatsAppNotificationReason.ProviderFailed),

                WhatsAppMessageStatuses.SkippedInvalidPhone =>
                    new WhatsAppConfirmationSendResult(
                        WhatsAppConfirmationOutcome.Skipped,
                        "No se envió la confirmación: el teléfono del cliente no es válido para WhatsApp.",
                        errorCode,
                        WhatsAppNotificationReason.CustomerPhoneMissing),

                WhatsAppMessageStatuses.SkippedConsentMissing =>
                    new WhatsAppConfirmationSendResult(
                        WhatsAppConfirmationOutcome.Skipped,
                        "No se envió la confirmación: el cliente no autorizó mensajes de WhatsApp.",
                        errorCode,
                        WhatsAppNotificationReason.ConsentMissing),

                WhatsAppMessageStatuses.SkippedTenantDisabled or
                WhatsAppMessageStatuses.SkippedUserDisabled =>
                    new WhatsAppConfirmationSendResult(
                        WhatsAppConfirmationOutcome.Skipped,
                        "No se envió la confirmación: WhatsApp está desactivado.",
                        errorCode,
                        reason),

                WhatsAppMessageStatuses.SkippedSubscriptionRequired =>
                    new WhatsAppConfirmationSendResult(
                        WhatsAppConfirmationOutcome.Skipped,
                        "No se envió la confirmación: se requiere el complemento de WhatsApp activo.",
                        errorCode,
                        reason),

                WhatsAppMessageStatuses.SkippedDailyLimitExceeded or
                WhatsAppMessageStatuses.SkippedMonthlyLimitExceeded =>
                    new WhatsAppConfirmationSendResult(
                        WhatsAppConfirmationOutcome.Skipped,
                        "No se envió la confirmación: se alcanzó el límite de mensajes de WhatsApp.",
                        errorCode,
                        WhatsAppNotificationReason.LimitReached),

                _ =>
                    new WhatsAppConfirmationSendResult(
                        WhatsAppConfirmationOutcome.Skipped,
                        "No se envió la confirmación de WhatsApp por la configuración actual.",
                        errorCode,
                        reason)
            };
        }

        public Task QueueAppointmentConfirmationAsync(int citaId, CancellationToken cancellationToken = default) =>
            QueueAppointmentAsync(citaId, WhatsAppNotificationTypes.Confirmation, isBatchGenerated: false, cancellationToken);

        public Task QueueAppointmentReminderAsync(int citaId, CancellationToken cancellationToken = default) =>
            QueueAppointmentAsync(citaId, WhatsAppNotificationTypes.Reminder3Hours, isBatchGenerated: false, cancellationToken);

        public async Task QueueImmediateReminderOnCreateAsync(int citaId, CancellationToken cancellationToken = default)
        {
            if (!_tenantProvider.HasTenant())
            {
                return;
            }

            var settings = await _tenantSettingsService.GetSettingsForTenantAsync(
                _tenantProvider.GetTenantId(),
                cancellationToken);

            // Solo modo relativo con envío inmediato activo; el lote diario lo maneja por su cuenta.
            if (!settings.IsEnabled ||
                !settings.SendReminderThreeHoursBefore ||
                !settings.SendReminderImmediatelyIfInsideWindow ||
                !string.Equals(settings.ReminderScheduleMode, WhatsAppReminderScheduleModes.RelativeBeforeAppointment, StringComparison.Ordinal))
            {
                return;
            }

            // QueueAppointmentReminderAsync valida que la cita esté dentro de la ventana del recordatorio.
            await QueueAppointmentReminderAsync(citaId, cancellationToken);
        }

        public async Task ScheduleDueRemindersAsync(CancellationToken cancellationToken = default)
        {
            var options = _options.CurrentValue;
            if (!options.Enabled ||
                !options.SendReminderBeforeAppointment ||
                !_tenantProvider.HasTenant())
            {
                return;
            }

            var tenantId = _tenantProvider.GetTenantId();
            var settings = await _tenantSettingsService.GetSettingsForTenantAsync(tenantId, cancellationToken);
            if (!settings.IsEnabled || !settings.SendReminderThreeHoursBefore)
            {
                return;
            }

            // En modo lote diario, los recordatorios los genera GenerateDailyBatchAsync (no la ventana relativa).
            if (!string.Equals(settings.ReminderScheduleMode, WhatsAppReminderScheduleModes.RelativeBeforeAppointment, StringComparison.Ordinal))
            {
                return;
            }

            var now = _businessDateTimeProvider.Now();
            var upperLimit = now.AddMinutes(ResolveReminderLeadMinutes(settings));

            var candidates = await _context.Citas
                .AsNoTracking()
                .Where(c =>
                    c.Tipo == "CITA" &&
                    c.FechaHoraCita > now &&
                    c.FechaHoraCita <= upperLimit &&
                    !c.Recordatorio3hEnviado &&
                    c.RecordatorioWhatsAppTresHorasEnviadoUtc == null &&
                    c.EstadoConfirmacionWhatsApp != WhatsAppConfirmationStates.Cancelada)
                .Select(c => new
                {
                    c.Id,
                    c.TelefonoCliente
                })
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                await QueueAppointmentReminderAsync(candidate.Id, cancellationToken);
            }
        }

        public async Task GenerateDailyBatchAsync(CancellationToken cancellationToken = default)
        {
            var options = _options.CurrentValue;
            if (!options.Enabled || !_tenantProvider.HasTenant())
            {
                return;
            }

            var tenantId = _tenantProvider.GetTenantId();
            var settings = await _tenantSettingsService.GetSettingsForTenantAsync(tenantId, cancellationToken);
            if (!settings.IsEnabled)
            {
                return;
            }

            var now = _businessDateTimeProvider.Now();
            var today = DateOnly.FromDateTime(now);
            var nowTime = TimeOnly.FromDateTime(now);

            await GenerateConfirmationBatchAsync(tenantId, settings, now, today, nowTime, cancellationToken);
            await GenerateReminderBatchAsync(tenantId, settings, now, today, nowTime, cancellationToken);
        }

        private async Task GenerateConfirmationBatchAsync(
            Guid tenantId,
            TenantWhatsAppSettingsSnapshot settings,
            DateTime now,
            DateOnly today,
            TimeOnly nowTime,
            CancellationToken cancellationToken)
        {
            var isBatchMode = string.Equals(settings.ConfirmationScheduleMode, WhatsAppConfirmationScheduleModes.DailyBatchPreviousDay, StringComparison.Ordinal) ||
                              string.Equals(settings.ConfirmationScheduleMode, WhatsAppConfirmationScheduleModes.DailyBatchSameDay, StringComparison.Ordinal);

            if (!settings.SendConfirmationOnCreate ||
                !isBatchMode ||
                settings.ConfirmationBatchTime is null)
            {
                return;
            }

            // Una sola corrida por día local; si la hora aún no llega, esperar.
            if (settings.LastConfirmationBatchRunDateLocal == today || nowTime < settings.ConfirmationBatchTime.Value)
            {
                return;
            }

            var (startInclusive, endExclusive) = ResolveConfirmationBatchWindow(settings, now);
            var candidateIds = await _context.Citas
                .AsNoTracking()
                .Where(c =>
                    c.Tipo == "CITA" &&
                    c.FechaHoraCita >= startInclusive &&
                    c.FechaHoraCita < endExclusive &&
                    c.FechaHoraCita > now &&
                    c.EstadoConfirmacionWhatsApp != WhatsAppConfirmationStates.Cancelada)
                .OrderBy(c => c.FechaHoraCita)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            foreach (var citaId in candidateIds)
            {
                await QueueAppointmentAsync(citaId, WhatsAppNotificationTypes.Confirmation, isBatchGenerated: true, cancellationToken);
            }

            await StampBatchRunAsync(tenantId, isConfirmation: true, today, cancellationToken);

            _logger.LogInformation(
                "Lote de confirmaciones WhatsApp generado. TenantId {TenantId}. Citas objetivo {Count}. Target {Target}.",
                tenantId, candidateIds.Count, settings.ConfirmationBatchTarget);
        }

        private async Task GenerateReminderBatchAsync(
            Guid tenantId,
            TenantWhatsAppSettingsSnapshot settings,
            DateTime now,
            DateOnly today,
            TimeOnly nowTime,
            CancellationToken cancellationToken)
        {
            if (!settings.SendReminderThreeHoursBefore ||
                !string.Equals(settings.ReminderScheduleMode, WhatsAppReminderScheduleModes.DailyBatchSameDay, StringComparison.Ordinal) ||
                settings.ReminderBatchTime is null)
            {
                return;
            }

            if (settings.LastReminderBatchRunDateLocal == today || nowTime < settings.ReminderBatchTime.Value)
            {
                return;
            }

            var (startInclusive, endExclusive) = ResolveReminderBatchWindow(settings, now, today);
            var candidateIds = await _context.Citas
                .AsNoTracking()
                .Where(c =>
                    c.Tipo == "CITA" &&
                    c.FechaHoraCita >= startInclusive &&
                    c.FechaHoraCita < endExclusive &&
                    c.FechaHoraCita > now &&
                    !c.Recordatorio3hEnviado &&
                    c.RecordatorioWhatsAppTresHorasEnviadoUtc == null &&
                    c.EstadoConfirmacionWhatsApp != WhatsAppConfirmationStates.Cancelada)
                .OrderBy(c => c.FechaHoraCita)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            foreach (var citaId in candidateIds)
            {
                await QueueAppointmentAsync(citaId, WhatsAppNotificationTypes.Reminder3Hours, isBatchGenerated: true, cancellationToken);
            }

            await StampBatchRunAsync(tenantId, isConfirmation: false, today, cancellationToken);

            _logger.LogInformation(
                "Lote de recordatorios WhatsApp generado. TenantId {TenantId}. Citas objetivo {Count}. Target {Target}.",
                tenantId, candidateIds.Count, settings.ReminderBatchTarget);
        }

        private (DateTime StartInclusive, DateTime EndExclusive) ResolveConfirmationBatchWindow(
            TenantWhatsAppSettingsSnapshot settings,
            DateTime now)
        {
            var today = now.Date;
            var tomorrow = today.AddDays(1);

            switch (settings.ConfirmationBatchTarget)
            {
                case WhatsAppConfirmationBatchTargets.TomorrowMorning:
                    var morningStart = settings.ConfirmationMorningStart ?? TenantWhatsAppSettings.DefaultMorningStart;
                    var morningEnd = settings.ConfirmationMorningEnd ?? TenantWhatsAppSettings.DefaultMorningEnd;
                    return (tomorrow.Add(morningStart.ToTimeSpan()), tomorrow.Add(morningEnd.ToTimeSpan()));

                case WhatsAppConfirmationBatchTargets.SameDayRemaining:
                    return (now, tomorrow);

                case WhatsAppConfirmationBatchTargets.Next24Hours:
                    return (now, now.AddHours(24));

                case WhatsAppConfirmationBatchTargets.TomorrowAllDay:
                default:
                    return (tomorrow, tomorrow.AddDays(1));
            }
        }

        private (DateTime StartInclusive, DateTime EndExclusive) ResolveReminderBatchWindow(
            TenantWhatsAppSettingsSnapshot settings,
            DateTime now,
            DateOnly today)
        {
            var startOfToday = now.Date;
            var endOfToday = startOfToday.AddDays(1);

            return settings.ReminderBatchTarget switch
            {
                WhatsAppReminderBatchTargets.NextXHours => (now, now.AddHours(
                    settings.ReminderLookAheadHours > 0 ? settings.ReminderLookAheadHours : TenantWhatsAppSettings.DefaultReminderHoursBefore)),
                // SameDayRemaining / NextAppointmentsToday: lo que resta de hoy.
                _ => (now, endOfToday)
            };
        }

        private async Task StampBatchRunAsync(
            Guid tenantId,
            bool isConfirmation,
            DateOnly today,
            CancellationToken cancellationToken)
        {
            if (isConfirmation)
            {
                await _context.TenantWhatsAppSettings
                    .Where(s => s.TenantId == tenantId)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(s => s.LastConfirmationBatchRunDateLocal, today),
                        cancellationToken);
            }
            else
            {
                await _context.TenantWhatsAppSettings
                    .Where(s => s.TenantId == tenantId)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(s => s.LastReminderBatchRunDateLocal, today),
                        cancellationToken);
            }
        }

        public async Task ProcessPendingNotificationsAsync(CancellationToken cancellationToken = default)
        {
            var nowUtc = _businessDateTimeProvider.NowOffset().UtcDateTime;
            var staleProcessingCutoffUtc = nowUtc.AddMinutes(-10);

            // Horas de silencio: si estamos dentro del rango configurado por el tenant, no se envía
            // nada ahora; se reprograman los pendientes vencidos al fin del silencio (sin gastar intentos).
            if (_tenantProvider.HasTenant())
            {
                var quietSettings = await _tenantSettingsService.GetSettingsForTenantAsync(
                    _tenantProvider.GetTenantId(),
                    cancellationToken);

                if (quietSettings.QuietHoursEnabled &&
                    quietSettings.QuietHoursStart.HasValue &&
                    quietSettings.QuietHoursEnd.HasValue &&
                    TryGetQuietHoursResumeUtc(quietSettings, out var resumeUtc))
                {
                    await _context.WhatsAppMessageLogs
                        .Where(message =>
                            message.Direction == WhatsAppMessageDirections.Outbound &&
                            message.Status == WhatsAppMessageStatuses.Pending &&
                            message.AttemptCount < MaxAttempts &&
                            (message.NextAttemptAtUtc == null || message.NextAttemptAtUtc <= nowUtc) &&
                            (message.NotificationType == WhatsAppNotificationTypes.Confirmation ||
                             message.NotificationType == WhatsAppNotificationTypes.Reminder3Hours))
                        .ExecuteUpdateAsync(updates => updates
                            .SetProperty(message => message.NextAttemptAtUtc, resumeUtc),
                            cancellationToken);

                    _logger.LogInformation(
                        "Envíos WhatsApp pospuestos por horas de silencio. TenantId {TenantId}. Reanuda {ResumeUtc:yyyy-MM-dd HH:mm} UTC.",
                        quietSettings.TenantId, resumeUtc);
                    return;
                }
            }

            await _context.WhatsAppMessageLogs
                .Where(message =>
                    message.Direction == WhatsAppMessageDirections.Outbound &&
                    message.Status == WhatsAppMessageStatuses.Processing &&
                    message.AttemptCount < MaxAttempts &&
                    message.ProcessingStartedAtUtc != null &&
                    message.ProcessingStartedAtUtc < staleProcessingCutoffUtc)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(message => message.Status, WhatsAppMessageStatuses.Pending)
                    .SetProperty(message => message.ProcessingStartedAtUtc, (DateTime?)null)
                    .SetProperty(message => message.NextAttemptAtUtc, nowUtc),
                    cancellationToken);

            var pendingIds = await _context.WhatsAppMessageLogs
                .AsNoTracking()
                .Where(message =>
                    message.Direction == WhatsAppMessageDirections.Outbound &&
                    message.Status == WhatsAppMessageStatuses.Pending &&
                    message.AttemptCount < MaxAttempts &&
                    (message.NextAttemptAtUtc == null || message.NextAttemptAtUtc <= nowUtc) &&
                    (message.NotificationType == WhatsAppNotificationTypes.Confirmation ||
                     message.NotificationType == WhatsAppNotificationTypes.Reminder3Hours))
                .OrderBy(message => message.CreatedAtUtc)
                .Select(message => message.Id)
                .Take(PendingBatchSize)
                .ToListAsync(cancellationToken);

            foreach (var messageId in pendingIds)
            {
                var pendingMessage = await _context.WhatsAppMessageLogs
                    .AsNoTracking()
                    .Where(message => message.Id == messageId)
                    .Select(message => new
                    {
                        message.Id,
                        message.CitaId,
                        message.NotificationType
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                if (pendingMessage is null)
                {
                    continue;
                }

                if (pendingMessage.CitaId.HasValue)
                {
                    var citaForConsent = await _context.Citas
                        .AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == pendingMessage.CitaId.Value, cancellationToken);

                    if (citaForConsent is not null)
                    {
                        var consentDecision = await EvaluateConsentAsync(citaForConsent, cancellationToken);
                        if (!consentDecision.CanSend)
                        {
                            var skipped = await SkipPendingMessageForConsentAsync(
                                pendingMessage.Id,
                                pendingMessage.NotificationType,
                                citaForConsent,
                                consentDecision,
                                cancellationToken);

                            if (skipped)
                            {
                                continue;
                            }
                        }
                    }
                }

                var claimed = await _context.WhatsAppMessageLogs
                    .Where(message =>
                        message.Id == messageId &&
                        message.Status == WhatsAppMessageStatuses.Pending &&
                        message.AttemptCount < MaxAttempts)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(message => message.Status, WhatsAppMessageStatuses.Processing)
                        .SetProperty(message => message.ProcessingStartedAtUtc, nowUtc)
                        .SetProperty(message => message.LastAttemptAtUtc, nowUtc)
                        .SetProperty(message => message.AttemptCount, message => message.AttemptCount + 1),
                        cancellationToken);

                if (claimed == 0)
                {
                    continue;
                }

                var trackedMessageEntry = _context.ChangeTracker.Entries<WhatsAppMessageLog>()
                    .FirstOrDefault(entry => entry.Entity.Id == messageId);
                if (trackedMessageEntry is not null)
                {
                    trackedMessageEntry.State = EntityState.Detached;
                }

                var message = await _context.WhatsAppMessageLogs
                    .FirstOrDefaultAsync(current => current.Id == messageId, cancellationToken);

                if (message is null)
                {
                    continue;
                }

                await SendPendingLogAsync(message, cancellationToken);
            }
        }

        public async Task ProcessInboundReplyAsync(JsonElement payload, CancellationToken cancellationToken = default)
        {
            var inboundMessages = MetaWhatsAppWebhookPayloadParser.ExtractInboundMessages(payload);
            foreach (var inboundMessage in inboundMessages)
            {
                var action = WhatsAppAppointmentReplyResolver.Resolve(inboundMessage);
                var candidates = await ResolveTargetCandidatesAsync(inboundMessage, cancellationToken);

                if (candidates.Count == 0)
                {
                    _logger.LogWarning(
                        "Respuesta WhatsApp sin cita asociada. MetaMessageId {MetaMessageId}. ContextMessageId {ContextMessageId}.",
                        inboundMessage.MessageId,
                        inboundMessage.ContextMessageId);
                    continue;
                }

                if (candidates.Count > 1)
                {
                    await RegisterAmbiguousInboundAsync(inboundMessage, candidates, cancellationToken);
                    continue;
                }

                var candidate = candidates[0];
                await _tenantExecutionService.RunForTenantAsync(
                    candidate.TenantId,
                    async (serviceProvider, _, ct) =>
                    {
                        var db = serviceProvider.GetRequiredService<ApplicationDbContext>();
                        var notificationService = serviceProvider.GetRequiredService<INotificationService>();
                        await ProcessResolvedInboundAsync(db, notificationService, inboundMessage, candidate, action, ct);
                    },
                    cancellationToken);
            }
        }

        public async Task ProcessStatusUpdateAsync(JsonElement payload, CancellationToken cancellationToken = default)
        {
            var statusUpdates = MetaWhatsAppWebhookPayloadParser.ExtractStatusUpdates(payload);
            foreach (var statusUpdate in statusUpdates)
            {
                var processed = false;

                await _tenantExecutionService.RunForEachActiveTenantAsync(
                    async (serviceProvider, _, ct) =>
                    {
                        if (processed)
                        {
                            return;
                        }

                        var db = serviceProvider.GetRequiredService<ApplicationDbContext>();
                        var message = await db.WhatsAppMessageLogs
                            .FirstOrDefaultAsync(
                                current => current.MetaMessageId == statusUpdate.MessageId &&
                                           current.Direction == WhatsAppMessageDirections.Outbound,
                                ct);

                        if (message is null)
                        {
                            return;
                        }

                        ApplyProviderStatus(message, statusUpdate);

                        var status = MapProviderStatus(statusUpdate.Status);
                        var alreadyLogged = await db.WhatsAppMessageLogs
                            .AnyAsync(
                                current => current.Direction == WhatsAppMessageDirections.Status &&
                                           current.ContextMessageId == statusUpdate.MessageId &&
                                           current.Status == status,
                                ct);

                        if (!alreadyLogged)
                        {
                            db.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
                            {
                                CitaId = message.CitaId,
                                Direction = WhatsAppMessageDirections.Status,
                                NotificationType = WhatsAppNotificationTypes.Status,
                                Provider = WhatsAppProviders.Meta,
                                ContextMessageId = statusUpdate.MessageId,
                                RecipientPhoneE164 = _metaClient.NormalizePhoneNumber(statusUpdate.RecipientPhone),
                                Status = status,
                                ErrorCode = statusUpdate.ErrorCode,
                                ErrorMessage = Trim(statusUpdate.ErrorMessage, 1000),
                                PayloadJson = BuildStatusPayloadJson(statusUpdate),
                                CreatedAtUtc = DateTime.UtcNow,
                                ProcessedAtUtc = DateTime.UtcNow
                            });
                        }

                        await db.SaveChangesAsync(ct);
                        processed = true;
                    },
                    cancellationToken);

                if (!processed)
                {
                    _logger.LogWarning(
                        "Status WhatsApp sin mensaje saliente asociado. MetaMessageId {MetaMessageId}. Status {Status}.",
                        statusUpdate.MessageId,
                        statusUpdate.Status);
                }
            }
        }

        private async Task QueueAppointmentAsync(
            int citaId,
            string notificationType,
            bool isBatchGenerated,
            CancellationToken cancellationToken,
            bool forceImmediate = false)
        {
            var options = _options.CurrentValue;
            var cita = await _context.Citas
                .FirstOrDefaultAsync(c => c.Id == citaId, cancellationToken);

            if (cita is null)
            {
                return;
            }

            if (!IsNotifiableAppointment(cita, out var ignoredReason))
            {
                await RegisterSkippedOutboundAsync(
                    cita,
                    notificationType,
                    WhatsAppMessageStatuses.SkippedNotEligible,
                    WhatsAppErrorCodes.AppointmentNotEligible,
                    ignoredReason,
                    todayUsage: null,
                    dailyMessageLimit: null,
                    cancellationToken);
                return;
            }

            var consentDecision = await EvaluateConsentAsync(cita, cancellationToken);
            if (!consentDecision.CanSend)
            {
                await RegisterSkippedOutboundAsync(
                    cita,
                    notificationType,
                    WhatsAppMessageStatuses.SkippedConsentMissing,
                    WhatsAppErrorCodes.ConsentMissing,
                    consentDecision.Message,
                    todayUsage: null,
                    dailyMessageLimit: null,
                    cancellationToken);
                return;
            }

            var phoneE164 = _metaClient.NormalizePhoneNumber(cita.TelefonoCliente);
            if (phoneE164 is null)
            {
                await RegisterSkippedOutboundAsync(
                    cita,
                    notificationType,
                    WhatsAppMessageStatuses.SkippedInvalidPhone,
                    WhatsAppErrorCodes.InvalidPhone,
                    "Telefono invalido.",
                    todayUsage: null,
                    dailyMessageLimit: null,
                    cancellationToken);
                return;
            }

            // Configuración de programación del tenant (horas antes / envío inmediato / lote).
            var scheduleSettings = await _tenantSettingsService.GetSettingsForTenantAsync(cita.TenantId, cancellationToken);
            var confirmationIsBatchMode = !string.Equals(
                scheduleSettings.ConfirmationScheduleMode,
                WhatsAppConfirmationScheduleModes.RelativeBeforeAppointment,
                StringComparison.Ordinal);

            // En modo lote, las confirmaciones NO se encolan al crear la cita; las genera
            // GenerateDailyBatchAsync a la hora configurada. Solo el job de lote (isBatchGenerated) encola.
            // Excepción: forceImmediate (p. ej. reserva online aprobada) fuerza el envío ahora mismo.
            if (notificationType == WhatsAppNotificationTypes.Confirmation && confirmationIsBatchMode && !isBatchGenerated && !forceImmediate)
            {
                return;
            }

            // Si la cita ya entró en la ventana del recordatorio (modo relativo) y los recordatorios
            // están activos, NO se envía confirmación: basta el recordatorio. Se omite en silencio
            // (sin registrar log) para que el listado NO muestre "error/omitida" de confirmación.
            // Excepción: forceImmediate exige la confirmación (reserva aprobada = cita confirmada);
            // el recordatorio sigue su curso normal por separado.
            if (notificationType == WhatsAppNotificationTypes.Confirmation &&
                !forceImmediate &&
                scheduleSettings.SendReminderThreeHoursBefore &&
                string.Equals(scheduleSettings.ReminderScheduleMode, WhatsAppReminderScheduleModes.RelativeBeforeAppointment, StringComparison.Ordinal) &&
                IsReminderInsideWindow(cita, ResolveReminderLeadMinutes(scheduleSettings)))
            {
                _logger.LogInformation(
                    "Confirmacion WhatsApp omitida en silencio: la cita ya está dentro de la ventana de recordatorio; se enviará solo el recordatorio. TenantId {TenantId}. CitaId {CitaId}.",
                    cita.TenantId, cita.Id);
                return;
            }

            // El recordatorio relativo exige estar dentro de la ventana; el lote la omite.
            if (notificationType == WhatsAppNotificationTypes.Reminder3Hours &&
                !isBatchGenerated &&
                !IsReminderInsideWindow(cita, ResolveReminderLeadMinutes(scheduleSettings)))
            {
                return;
            }

            // Calcular cuándo enviar la confirmación según la configuración del tenant.
            DateTime? scheduledSendAtUtc = null;
            if (notificationType == WhatsAppNotificationTypes.Confirmation)
            {
                if (forceImmediate)
                {
                    // Reserva online aprobada: enviar ya. Solo se omite si la cita ya venció.
                    if (IsAppointmentPast(cita.FechaHoraCita))
                    {
                        await RegisterSkippedOutboundAsync(
                            cita,
                            notificationType,
                            WhatsAppMessageStatuses.SkippedNotEligible,
                            WhatsAppErrorCodes.AppointmentExpired,
                            "La cita ya venció; no se envía la confirmacion.",
                            todayUsage: null,
                            dailyMessageLimit: null,
                            cancellationToken);
                        return;
                    }

                    scheduledSendAtUtc = null;
                }
                else if (isBatchGenerated || confirmationIsBatchMode)
                {
                    // El lote ya se ejecuta a la hora configurada: enviar en el próximo ciclo,
                    // salvo que la cita ya haya vencido.
                    if (IsAppointmentPast(cita.FechaHoraCita))
                    {
                        _logger.LogInformation(
                            "Confirmacion WhatsApp (lote) omitida: la cita ya venció. TenantId {TenantId}. CitaId {CitaId}.",
                            cita.TenantId, cita.Id);
                        return;
                    }

                    scheduledSendAtUtc = null;
                }
                else
                {
                var (outcome, sendAtUtc) = CalculateConfirmationSchedule(
                    cita.FechaHoraCita,
                    scheduleSettings.ConfirmationHoursBefore,
                    scheduleSettings.SendConfirmationImmediatelyIfInsideWindow);

                if (outcome == ConfirmationScheduleOutcome.Past)
                {
                    _logger.LogInformation(
                        "Confirmacion WhatsApp omitida: la cita ya venció. TenantId {TenantId}. CitaId {CitaId}. FechaHoraCita {FechaHoraCita:yyyy-MM-dd HH:mm}.",
                        cita.TenantId, cita.Id, cita.FechaHoraCita);
                    return;
                }

                if (outcome == ConfirmationScheduleOutcome.SkipInsideWindow)
                {
                    _logger.LogInformation(
                        "Confirmacion WhatsApp omitida: la cita ya está dentro de la ventana ({HoursBefore}h) y el envío inmediato está desactivado. TenantId {TenantId}. CitaId {CitaId}.",
                        scheduleSettings.ConfirmationHoursBefore, cita.TenantId, cita.Id);
                    return;
                }

                // Immediate => null (se envía en el próximo ciclo); Scheduled => sendAtUtc.
                scheduledSendAtUtc = sendAtUtc;
                }
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var exists = await _context.WhatsAppMessageLogs
                .AnyAsync(message =>
                    message.CitaId == cita.Id &&
                    message.Direction == WhatsAppMessageDirections.Outbound &&
                    message.NotificationType == notificationType &&
                    ActiveOutboundStatuses.Contains(message.Status),
                    cancellationToken);

            if (exists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }

            var decision = await _tenantSettingsService.CanSendNotificationAsync(
                cita.TenantId,
                notificationType,
                cancellationToken: cancellationToken);

            if (!decision.CanSend)
            {
                await RegisterSkippedOutboundAsync(
                    cita,
                    notificationType,
                    ResolveSkippedStatus(decision.ErrorCode),
                    decision.ErrorCode ?? WhatsAppErrorCodes.ConfigurationDisabled,
                    decision.ErrorMessage ?? "El mensaje WhatsApp fue omitido por configuracion.",
                    decision.TodayUsage,
                    decision.DailyMessageLimit,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            _context.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
            {
                CitaId = cita.Id,
                Direction = WhatsAppMessageDirections.Outbound,
                NotificationType = notificationType,
                Provider = WhatsAppProviders.Meta,
                RecipientPhoneE164 = phoneE164,
                TemplateName = ResolveTemplateName(options, notificationType),
                Status = WhatsAppMessageStatuses.Pending,
                PayloadJson = BuildQueuedPayloadJson(cita, notificationType),
                CreatedAtUtc = _businessDateTimeProvider.NowOffset().UtcDateTime,
                NextAttemptAtUtc = scheduledSendAtUtc
            });

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (notificationType == WhatsAppNotificationTypes.Confirmation && scheduledSendAtUtc.HasValue)
            {
                _logger.LogInformation(
                    "Confirmacion WhatsApp programada para {ScheduledSendAtUtc:yyyy-MM-dd HH:mm} UTC (24h antes de la cita). TenantId {TenantId}. CitaId {CitaId}. UsoDiario {TodayUsage}. LimiteDiario {DailyMessageLimit}.",
                    scheduledSendAtUtc.Value,
                    cita.TenantId,
                    cita.Id,
                    decision.TodayUsage + 1,
                    decision.DailyMessageLimit);
            }
            else
            {
                _logger.LogInformation(
                    "Notificacion WhatsApp encolada para envio inmediato. TenantId {TenantId}. CitaId {CitaId}. NotificationType {NotificationType}. UsoDiario {TodayUsage}. LimiteDiario {DailyMessageLimit}.",
                    cita.TenantId,
                    cita.Id,
                    notificationType,
                    decision.TodayUsage + 1,
                    decision.DailyMessageLimit);
            }
        }

        private async Task RegisterSkippedOutboundAsync(
            Cita cita,
            string notificationType,
            string status,
            string errorCode,
            string reason,
            int? todayUsage,
            int? dailyMessageLimit,
            CancellationToken cancellationToken)
        {
            var nowUtc = _businessDateTimeProvider.NowOffset().UtcDateTime;
            if (notificationType == WhatsAppNotificationTypes.Confirmation)
            {
                cita.EstadoConfirmacionWhatsApp = WhatsAppConfirmationStates.NoEnviada;
            }

            var alreadyIgnored = await _context.WhatsAppMessageLogs
                .AnyAsync(message =>
                    message.CitaId == cita.Id &&
                    message.Direction == WhatsAppMessageDirections.Outbound &&
                    message.NotificationType == notificationType &&
                    message.Status == status &&
                    message.ErrorCode == errorCode,
                    cancellationToken);

            if (!alreadyIgnored)
            {
                _context.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
                {
                    CitaId = cita.Id,
                    Direction = WhatsAppMessageDirections.Outbound,
                    NotificationType = notificationType,
                    Provider = WhatsAppProviders.Meta,
                    RecipientPhoneE164 = _metaClient.NormalizePhoneNumber(cita.TelefonoCliente),
                    TemplateName = ResolveTemplateName(_options.CurrentValue, notificationType),
                    Status = status,
                    ErrorCode = errorCode,
                    ErrorMessage = Trim(reason, 1000),
                    PayloadJson = BuildSkippedPayloadJson(cita, notificationType, errorCode),
                    CreatedAtUtc = nowUtc,
                    ProcessedAtUtc = nowUtc
                });
            }

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Notificacion WhatsApp omitida. TenantId {TenantId}. CitaId {CitaId}. NotificationType {NotificationType}. Motivo {SkipReason}. UsoDiario {TodayUsage}. LimiteDiario {DailyMessageLimit}.",
                cita.TenantId,
                cita.Id,
                notificationType,
                errorCode,
                todayUsage,
                dailyMessageLimit);
        }

        private async Task<WhatsAppConsentDecision> EvaluateConsentAsync(
            Cita cita,
            CancellationToken cancellationToken)
        {
            bool? clienteAcepta = null;
            if (cita.ClienteId.HasValue)
            {
                clienteAcepta = await _context.Clientes
                    .AsNoTracking()
                    .Where(current => current.Id == cita.ClienteId.Value)
                    .Select(current => (bool?)current.AceptaMensajesWhatsApp)
                    .SingleOrDefaultAsync(cancellationToken);
            }

            return WhatsAppConsentPolicy.Evaluate(cita, clienteAcepta);
        }

        private async Task<bool> SkipPendingMessageForConsentAsync(
            long messageId,
            string notificationType,
            Cita cita,
            WhatsAppConsentDecision consentDecision,
            CancellationToken cancellationToken)
        {
            var nowUtc = _businessDateTimeProvider.NowOffset().UtcDateTime;
            var payloadJson = BuildSkippedPayloadJson(
                cita,
                notificationType,
                WhatsAppErrorCodes.ConsentMissing,
                consentDecision.Source,
                consentDecision.HasClienteId);

            var updated = await _context.WhatsAppMessageLogs
                .Where(message =>
                    message.Id == messageId &&
                    message.Status == WhatsAppMessageStatuses.Pending &&
                    message.AttemptCount < MaxAttempts)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(message => message.Status, WhatsAppMessageStatuses.SkippedConsentMissing)
                    .SetProperty(message => message.ErrorCode, WhatsAppErrorCodes.ConsentMissing)
                    .SetProperty(message => message.ErrorMessage, Trim(consentDecision.Message, 1000))
                    .SetProperty(message => message.ProcessedAtUtc, nowUtc)
                    .SetProperty(message => message.ProcessingStartedAtUtc, (DateTime?)null)
                    .SetProperty(message => message.NextAttemptAtUtc, (DateTime?)null)
                    .SetProperty(message => message.PayloadJson, payloadJson),
                    cancellationToken);

            if (updated == 0)
            {
                return false;
            }

            if (notificationType == WhatsAppNotificationTypes.Confirmation)
            {
                await _context.Citas
                    .Where(current => current.Id == cita.Id)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(current => current.EstadoConfirmacionWhatsApp, WhatsAppConfirmationStates.NoEnviada),
                        cancellationToken);
            }

            _logger.LogInformation(
                "Notificacion WhatsApp pendiente omitida por falta de consentimiento. TenantId {TenantId}. CitaId {CitaId}. NotificationType {NotificationType}.",
                cita.TenantId,
                cita.Id,
                notificationType);

            return true;
        }

        private async Task SendPendingLogAsync(
            WhatsAppMessageLog message,
            CancellationToken cancellationToken)
        {
            var nowUtc = _businessDateTimeProvider.NowOffset().UtcDateTime;
            var cita = await _context.Citas
                .Include(c => c.Funcionario)
                .Include(c => c.Servicio)
                .FirstOrDefaultAsync(c => c.Id == message.CitaId, cancellationToken);

            if (cita is null)
            {
                MarkSkipped(
                    message,
                    cita: null,
                    WhatsAppMessageStatuses.SkippedNotEligible,
                    WhatsAppErrorCodes.AppointmentNotEligible,
                    "Cita no encontrada.",
                    nowUtc);
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            var consentDecision = await EvaluateConsentAsync(cita, cancellationToken);
            if (!consentDecision.CanSend)
            {
                MarkSkipped(
                    message,
                    cita,
                    WhatsAppMessageStatuses.SkippedConsentMissing,
                    WhatsAppErrorCodes.ConsentMissing,
                    consentDecision.Message,
                    nowUtc);

                if (message.NotificationType == WhatsAppNotificationTypes.Confirmation)
                {
                    cita.EstadoConfirmacionWhatsApp = WhatsAppConfirmationStates.NoEnviada;
                }

                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            var decision = await _tenantSettingsService.CanSendNotificationAsync(
                cita.TenantId,
                message.NotificationType,
                message.Id,
                cancellationToken);

            if (!decision.CanSend)
            {
                MarkSkipped(
                    message,
                    cita,
                    ResolveSkippedStatus(decision.ErrorCode),
                    decision.ErrorCode ?? WhatsAppErrorCodes.ConfigurationDisabled,
                    decision.ErrorMessage ?? "El mensaje WhatsApp fue omitido por configuracion.",
                    nowUtc);
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Notificacion WhatsApp pendiente omitida. TenantId {TenantId}. CitaId {CitaId}. NotificationType {NotificationType}. Motivo {SkipReason}. UsoDiario {TodayUsage}. LimiteDiario {DailyMessageLimit}.",
                    cita.TenantId,
                    cita.Id,
                    message.NotificationType,
                    decision.ErrorCode,
                    decision.TodayUsage,
                    decision.DailyMessageLimit);
                return;
            }

            if (!IsNotifiableAppointment(cita, out var ignoredReason))
            {
                MarkSkipped(
                    message,
                    cita,
                    WhatsAppMessageStatuses.SkippedNotEligible,
                    WhatsAppErrorCodes.AppointmentNotEligible,
                    ignoredReason,
                    nowUtc);
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            var phoneE164 = _metaClient.NormalizePhoneNumber(cita.TelefonoCliente);
            if (phoneE164 is null)
            {
                MarkSkipped(
                    message,
                    cita,
                    WhatsAppMessageStatuses.SkippedInvalidPhone,
                    WhatsAppErrorCodes.InvalidPhone,
                    "Telefono invalido.",
                    nowUtc);
                cita.EstadoConfirmacionWhatsApp = WhatsAppConfirmationStates.NoEnviada;
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            var sendSettings = await _tenantSettingsService.GetSettingsForTenantAsync(cita.TenantId, cancellationToken);
            if (message.NotificationType == WhatsAppNotificationTypes.Reminder3Hours &&
                !IsReminderInsideWindow(cita, ResolveReminderLeadMinutes(sendSettings)))
            {
                MarkSkipped(
                    message,
                    cita,
                    WhatsAppMessageStatuses.SkippedNotEligible,
                    WhatsAppErrorCodes.AppointmentNotEligible,
                    "Cita fuera de la ventana de recordatorio.",
                    nowUtc);
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            // Omitir confirmaciones si la cita ya venció al momento de enviarla.
            if (message.NotificationType == WhatsAppNotificationTypes.Confirmation)
            {
                if (IsAppointmentPast(cita.FechaHoraCita))
                {
                    MarkSkipped(
                        message,
                        cita,
                        WhatsAppMessageStatuses.SkippedNotEligible,
                        WhatsAppErrorCodes.AppointmentExpired,
                        "La cita ya venció al momento de enviar la confirmacion.",
                        nowUtc);
                    _logger.LogInformation(
                        "Confirmacion WhatsApp omitida al procesar: cita ya vencida. TenantId {TenantId}. CitaId {CitaId}. FechaHoraCita {FechaHoraCita:yyyy-MM-dd HH:mm}.",
                        cita.TenantId, cita.Id, cita.FechaHoraCita);
                    await _context.SaveChangesAsync(cancellationToken);
                    return;
                }
            }

            var tenantName = await _tenantDisplayNameService.GetTenantDisplayNameAsync(
                cita.TenantId,
                cancellationToken);

            var sendResult = message.NotificationType switch
            {
                WhatsAppNotificationTypes.Confirmation => await _metaClient.SendConfirmationTemplateAsync(
                    phoneE164,
                    cita.NombreCliente ?? "Cliente",
                    tenantName,
                    cita.FechaHoraCita.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("es-CR")),
                    cita.FechaHoraCita.ToString("hh:mm tt", CultureInfo.GetCultureInfo("es-CR")),
                    cita.Funcionario?.Nombre ?? "-",
                    cancellationToken),

                WhatsAppNotificationTypes.Reminder3Hours => await _metaClient.SendReminderTemplateAsync(
                    phoneE164,
                    cita.NombreCliente ?? "Cliente",
                    tenantName,
                    cita.FechaHoraCita.ToString("hh:mm tt", CultureInfo.GetCultureInfo("es-CR")),
                    cita.Funcionario?.Nombre ?? "-",
                    cancellationToken),

                _ => MetaWhatsAppSendResult.Failed("UNSUPPORTED_TYPE", "Tipo de notificacion no soportado.")
            };

            if (sendResult.Success && !string.IsNullOrWhiteSpace(sendResult.MetaMessageId))
            {
                MarkSent(message, sendResult, phoneE164, nowUtc);
                ApplySentState(cita, message.NotificationType, sendResult.MetaMessageId, nowUtc);
            }
            else
            {
                MarkFailedOrRetry(message, sendResult, nowUtc);
                if (message.NotificationType == WhatsAppNotificationTypes.Confirmation &&
                    message.Status == WhatsAppMessageStatuses.Failed)
                {
                    cita.EstadoConfirmacionWhatsApp = WhatsAppConfirmationStates.ErrorEnvio;
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task RescheduleConfirmationIfPendingAsync(
            int citaId,
            DateTime newFechaHoraCita,
            CancellationToken cancellationToken = default)
        {
            var hoursBefore = TenantWhatsAppSettings.DefaultConfirmationHoursBefore;
            var sendImmediatelyIfInside = true;
            if (_tenantProvider.HasTenant())
            {
                var settings = await _tenantSettingsService.GetSettingsForTenantAsync(
                    _tenantProvider.GetTenantId(),
                    cancellationToken);

                // En modo lote, el job diario gestiona la confirmación. Si la cita ya pasó la
                // cancelamos; en cualquier otro caso no reprogramamos por ventana relativa.
                if (!string.Equals(settings.ConfirmationScheduleMode, WhatsAppConfirmationScheduleModes.RelativeBeforeAppointment, StringComparison.Ordinal))
                {
                    if (IsAppointmentPast(newFechaHoraCita))
                    {
                        await CancelPendingNotificationsAsync(citaId, cancellationToken);
                    }
                    return;
                }

                hoursBefore = settings.ConfirmationHoursBefore;
                sendImmediatelyIfInside = settings.SendConfirmationImmediatelyIfInsideWindow;
            }

            var (outcome, sendAtUtc) = CalculateConfirmationSchedule(
                newFechaHoraCita,
                hoursBefore,
                sendImmediatelyIfInside);

            // Si la cita ya pasó, o entró en la ventana con envío inmediato desactivado,
            // cancelar la confirmación pendiente (ya no debe enviarse).
            if (outcome is ConfirmationScheduleOutcome.Past or ConfirmationScheduleOutcome.SkipInsideWindow)
            {
                await CancelPendingNotificationsAsync(citaId, cancellationToken);
                return;
            }

            var updated = await _context.WhatsAppMessageLogs
                .Where(message =>
                    message.CitaId == citaId &&
                    message.Direction == WhatsAppMessageDirections.Outbound &&
                    message.NotificationType == WhatsAppNotificationTypes.Confirmation &&
                    message.Status == WhatsAppMessageStatuses.Pending)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(message => message.NextAttemptAtUtc, sendAtUtc),
                    cancellationToken);

            if (updated > 0)
            {
                if (sendAtUtc.HasValue)
                {
                    _logger.LogInformation(
                        "Confirmacion WhatsApp reprogramada a {ScheduledSendAtUtc:yyyy-MM-dd HH:mm} UTC. CitaId {CitaId}.",
                        sendAtUtc.Value, citaId);
                }
                else
                {
                    _logger.LogInformation(
                        "Confirmacion WhatsApp reprogramada para envio inmediato (cita dentro de la ventana configurada). CitaId {CitaId}.",
                        citaId);
                }
            }
        }

        public async Task CancelPendingNotificationsAsync(
            int citaId,
            CancellationToken cancellationToken = default)
        {
            var nowUtc = _businessDateTimeProvider.NowOffset().UtcDateTime;

            var cancelled = await _context.WhatsAppMessageLogs
                .Where(message =>
                    message.CitaId == citaId &&
                    message.Direction == WhatsAppMessageDirections.Outbound &&
                    (message.Status == WhatsAppMessageStatuses.Pending ||
                     message.Status == WhatsAppMessageStatuses.Processing))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(message => message.Status, WhatsAppMessageStatuses.Cancelled)
                    .SetProperty(message => message.ErrorCode, WhatsAppErrorCodes.CitaCancellada)
                    .SetProperty(message => message.ErrorMessage, "La cita fue eliminada o cancelada.")
                    .SetProperty(message => message.ProcessedAtUtc, nowUtc)
                    .SetProperty(message => message.NextAttemptAtUtc, (DateTime?)null),
                    cancellationToken);

            if (cancelled > 0)
            {
                _logger.LogInformation(
                    "Notificaciones WhatsApp pendientes canceladas por eliminacion de cita. CitaId {CitaId}. Mensajes cancelados: {Count}.",
                    citaId, cancelled);
            }
        }

        private enum ConfirmationScheduleOutcome
        {
            Past,
            SkipInsideWindow,
            Immediate,
            Scheduled
        }

        /// <summary>
        /// Calcula cuándo enviar la confirmación según la configuración del tenant
        /// (modo relativo: <paramref name="hoursBefore"/> antes de la cita).
        /// Si la cita ya está dentro del rango, envía inmediatamente o se omite según
        /// <paramref name="sendImmediatelyIfInside"/>.
        /// </summary>
        private (ConfirmationScheduleOutcome Outcome, DateTime? ScheduledSendAtUtc) CalculateConfirmationSchedule(
            DateTime fechaHoraCita,
            int hoursBefore,
            bool sendImmediatelyIfInside)
        {
            var nowOffset = _businessDateTimeProvider.NowOffset();
            var nowUtc = nowOffset.UtcDateTime;
            var crOffset = nowOffset.Offset;
            var citaUtc = new DateTimeOffset(fechaHoraCita, crOffset).UtcDateTime;

            if (citaUtc <= nowUtc)
            {
                return (ConfirmationScheduleOutcome.Past, null);
            }

            var effectiveHours = hoursBefore is < 1 or > 168
                ? TenantWhatsAppSettings.DefaultConfirmationHoursBefore
                : hoursBefore;

            var sendAtUtc = citaUtc.AddHours(-effectiveHours);
            if (sendAtUtc > nowUtc)
            {
                return (ConfirmationScheduleOutcome.Scheduled, sendAtUtc);
            }

            // La cita ya entró en la ventana de anticipación configurada.
            return sendImmediatelyIfInside
                ? (ConfirmationScheduleOutcome.Immediate, null)
                : (ConfirmationScheduleOutcome.SkipInsideWindow, null);
        }

        private bool IsAppointmentPast(DateTime fechaHoraCita)
        {
            var nowOffset = _businessDateTimeProvider.NowOffset();
            var citaUtc = new DateTimeOffset(fechaHoraCita, nowOffset.Offset).UtcDateTime;
            return citaUtc <= nowOffset.UtcDateTime;
        }

        private static bool IsNotifiableAppointment(Cita cita, out string reason)
        {
            if (!string.Equals(cita.Tipo, "CITA", StringComparison.OrdinalIgnoreCase))
            {
                reason = "La entrada de agenda no es una cita.";
                return false;
            }

            if (string.Equals(cita.EstadoConfirmacionWhatsApp, WhatsAppConfirmationStates.Cancelada, StringComparison.OrdinalIgnoreCase))
            {
                reason = "La cita esta cancelada por WhatsApp.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private bool IsReminderInsideWindow(Cita cita, int leadMinutes)
        {
            var now = _businessDateTimeProvider.Now();
            var effectiveLead = leadMinutes <= 0 ? GetReminderLeadTimeMinutes(_options.CurrentValue) : leadMinutes;
            return cita.FechaHoraCita > now &&
                   cita.FechaHoraCita <= now.AddMinutes(effectiveLead);
        }

        private static int ResolveReminderLeadMinutes(TenantWhatsAppSettingsSnapshot settings)
        {
            var hours = settings.ReminderHoursBefore > 0
                ? settings.ReminderHoursBefore
                : TenantWhatsAppSettings.DefaultReminderHoursBefore;
            return hours * 60;
        }

        /// <summary>
        /// Si la hora local actual cae dentro de las horas de silencio del tenant, devuelve true y
        /// la hora UTC en que se pueden reanudar los envíos (próxima ocurrencia de la hora de fin).
        /// Soporta rangos que cruzan medianoche (ej. 21:00–07:00).
        /// </summary>
        private bool TryGetQuietHoursResumeUtc(TenantWhatsAppSettingsSnapshot settings, out DateTime resumeUtc)
        {
            resumeUtc = default;

            if (!settings.QuietHoursStart.HasValue || !settings.QuietHoursEnd.HasValue)
            {
                return false;
            }

            var start = settings.QuietHoursStart.Value;
            var end = settings.QuietHoursEnd.Value;
            if (start == end)
            {
                return false;
            }

            var nowLocal = _businessDateTimeProvider.Now();
            var offset = _businessDateTimeProvider.NowOffset().Offset;
            var nowTime = TimeOnly.FromDateTime(nowLocal);

            var inQuiet = start < end
                ? nowTime >= start && nowTime < end
                : nowTime >= start || nowTime < end;

            if (!inQuiet)
            {
                return false;
            }

            var localEnd = nowLocal.Date.Add(end.ToTimeSpan());
            if (localEnd <= nowLocal)
            {
                localEnd = localEnd.AddDays(1);
            }

            resumeUtc = new DateTimeOffset(localEnd, offset).UtcDateTime;
            return true;
        }

        private static void MarkSent(
            WhatsAppMessageLog message,
            MetaWhatsAppSendResult sendResult,
            string phoneE164,
            DateTime nowUtc)
        {
            message.Status = WhatsAppMessageStatuses.Sent;
            message.MetaMessageId = sendResult.MetaMessageId;
            message.RecipientPhoneE164 = phoneE164;
            message.SentAtUtc = nowUtc;
            message.ProcessedAtUtc = nowUtc;
            message.ProcessingStartedAtUtc = null;
            message.NextAttemptAtUtc = null;
            message.ErrorCode = null;
            message.ErrorMessage = null;
            message.PayloadJson = BuildSendResultPayloadJson(message, sendResult);
        }

        private static void MarkFailedOrRetry(
            WhatsAppMessageLog message,
            MetaWhatsAppSendResult sendResult,
            DateTime nowUtc)
        {
            message.PayloadJson = BuildSendResultPayloadJson(message, sendResult);
            message.ProcessingStartedAtUtc = null;
            message.MetaMessageId = null;

            var shouldRetry = sendResult.ShouldRetry && message.AttemptCount < MaxAttempts;
            if (!shouldRetry)
            {
                message.Status = WhatsAppMessageStatuses.Failed;
                message.ErrorCode = Trim(sendResult.ErrorCode ?? "SEND_ERROR", 80);
                message.ErrorMessage = Trim(sendResult.ErrorMessage ?? "No fue posible enviar el mensaje.", 1000);
                message.FailedAtUtc = nowUtc;
                message.ProcessedAtUtc = nowUtc;
                message.NextAttemptAtUtc = null;
                return;
            }

            message.Status = WhatsAppMessageStatuses.Pending;
            message.ErrorCode = null;
            message.ErrorMessage = null;
            message.FailedAtUtc = null;
            message.ProcessedAtUtc = null;
            message.NextAttemptAtUtc = nowUtc.AddMinutes(Math.Min(message.AttemptCount * 2, 10));
        }

        private static void MarkSkipped(
            WhatsAppMessageLog message,
            Cita? cita,
            string status,
            string errorCode,
            string reason,
            DateTime nowUtc)
        {
            message.Status = status;
            message.ErrorCode = errorCode;
            message.ErrorMessage = Trim(reason, 1000);
            message.ProcessedAtUtc = nowUtc;
            message.ProcessingStartedAtUtc = null;
            message.NextAttemptAtUtc = null;
            if (cita is not null)
            {
                message.PayloadJson = BuildSkippedPayloadJson(cita, message.NotificationType, errorCode);
            }
        }

        private static void ApplySentState(
            Cita cita,
            string notificationType,
            string metaMessageId,
            DateTime nowUtc)
        {
            cita.UltimoMetaMessageId = metaMessageId;

            if (notificationType == WhatsAppNotificationTypes.Confirmation)
            {
                cita.ConfirmacionEnviada = true;
                cita.ConfirmacionWhatsAppEnviadaUtc = nowUtc;
                if (!string.Equals(cita.EstadoConfirmacionWhatsApp, WhatsAppConfirmationStates.Confirmada, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(cita.EstadoConfirmacionWhatsApp, WhatsAppConfirmationStates.Cancelada, StringComparison.OrdinalIgnoreCase))
                {
                    cita.EstadoConfirmacionWhatsApp = WhatsAppConfirmationStates.Pendiente;
                }
                return;
            }

            if (notificationType == WhatsAppNotificationTypes.Reminder3Hours)
            {
                cita.Recordatorio3hEnviado = true;
                cita.RecordatorioWhatsAppTresHorasEnviadoUtc = nowUtc;
            }
        }

        private async Task<List<WhatsAppTargetCandidate>> ResolveTargetCandidatesAsync(
            InboundWhatsAppMessage inboundMessage,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(inboundMessage.ContextMessageId))
            {
                var contextCandidates = await ResolveCandidatesByContextAsync(inboundMessage.ContextMessageId!, cancellationToken);
                if (contextCandidates.Count > 0)
                {
                    return contextCandidates;
                }
            }

            return await ResolveCandidatesByPhoneAsync(inboundMessage, cancellationToken);
        }

        private async Task<List<WhatsAppTargetCandidate>> ResolveCandidatesByContextAsync(
            string contextMessageId,
            CancellationToken cancellationToken)
        {
            var candidates = new List<WhatsAppTargetCandidate>();

            await _tenantExecutionService.RunForEachActiveTenantAsync(
                async (serviceProvider, tenantId, ct) =>
                {
                    var db = serviceProvider.GetRequiredService<ApplicationDbContext>();
                    var match = await db.WhatsAppMessageLogs
                        .AsNoTracking()
                        .Where(message =>
                            message.MetaMessageId == contextMessageId &&
                            message.Direction == WhatsAppMessageDirections.Outbound &&
                            message.CitaId != null)
                        .Select(message => new
                        {
                            message.CitaId,
                            message.RecipientPhoneE164,
                            message.MetaMessageId
                        })
                        .FirstOrDefaultAsync(ct);

                    if (match?.CitaId is null)
                    {
                        return;
                    }

                    candidates.Add(new WhatsAppTargetCandidate(
                        tenantId,
                        match.CitaId.Value,
                        match.MetaMessageId,
                        match.RecipientPhoneE164));
                },
                cancellationToken);

            return candidates;
        }

        private async Task<List<WhatsAppTargetCandidate>> ResolveCandidatesByPhoneAsync(
            InboundWhatsAppMessage inboundMessage,
            CancellationToken cancellationToken)
        {
            var senderPhone = _metaClient.NormalizePhoneNumber(inboundMessage.From);
            if (senderPhone is null)
            {
                return [];
            }

            var now = _businessDateTimeProvider.Now();
            var upperLimit = now.AddHours(48);
            var candidates = new List<WhatsAppTargetCandidate>();

            await _tenantExecutionService.RunForEachActiveTenantAsync(
                async (serviceProvider, tenantId, ct) =>
                {
                    var db = serviceProvider.GetRequiredService<ApplicationDbContext>();
                    var citas = await db.Citas
                        .AsNoTracking()
                        .Where(c =>
                            c.Tipo == "CITA" &&
                            c.FechaHoraCita >= now &&
                            c.FechaHoraCita <= upperLimit &&
                            c.EstadoConfirmacionWhatsApp != WhatsAppConfirmationStates.Confirmada &&
                            c.EstadoConfirmacionWhatsApp != WhatsAppConfirmationStates.Cancelada &&
                            c.TelefonoCliente != null)
                        .Select(c => new
                        {
                            c.Id,
                            c.TelefonoCliente,
                            c.FechaHoraCita
                        })
                        .ToListAsync(ct);

                    foreach (var cita in citas.OrderBy(c => c.FechaHoraCita))
                    {
                        if (string.Equals(_metaClient.NormalizePhoneNumber(cita.TelefonoCliente), senderPhone, StringComparison.Ordinal))
                        {
                            candidates.Add(new WhatsAppTargetCandidate(tenantId, cita.Id, null, senderPhone));
                        }
                    }
                },
                cancellationToken);

            return candidates;
        }

        private async Task ProcessResolvedInboundAsync(
            ApplicationDbContext db,
            INotificationService notificationService,
            InboundWhatsAppMessage inboundMessage,
            WhatsAppTargetCandidate candidate,
            WhatsAppReplyAction action,
            CancellationToken cancellationToken)
        {
            var inboundExists = await db.WhatsAppMessageLogs
                .AnyAsync(message =>
                    message.MetaMessageId == inboundMessage.MessageId &&
                    message.Direction == WhatsAppMessageDirections.Inbound,
                    cancellationToken);

            if (inboundExists)
            {
                return;
            }

            var cita = await db.Citas
                .FirstOrDefaultAsync(current => current.Id == candidate.CitaId, cancellationToken);

            if (cita is null)
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var status = WhatsAppMessageStatuses.Received;
            string? errorCode = null;
            string? errorMessage = null;

            switch (action)
            {
                case WhatsAppReplyAction.Confirm:
                    cita.EstadoConfirmacionWhatsApp = WhatsAppConfirmationStates.Confirmada;
                    cita.ConfirmadaPorWhatsAppUtc = nowUtc;
                    cita.UltimaRespuestaWhatsAppUtc = nowUtc;
                    break;

                case WhatsAppReplyAction.Cancel:
                    cita.EstadoConfirmacionWhatsApp = WhatsAppConfirmationStates.Cancelada;
                    cita.CanceladaPorWhatsAppUtc = nowUtc;
                    cita.UltimaRespuestaWhatsAppUtc = nowUtc;
                    break;

                default:
                    status = WhatsAppMessageStatuses.Ignored;
                    errorCode = "UNRECOGNIZED_REPLY";
                    errorMessage = "Respuesta no reconocida.";
                    cita.UltimaRespuestaWhatsAppUtc = nowUtc;
                    break;
            }

            db.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
            {
                CitaId = cita.Id,
                Direction = WhatsAppMessageDirections.Inbound,
                NotificationType = WhatsAppNotificationTypes.Reply,
                Provider = WhatsAppProviders.Meta,
                MetaMessageId = inboundMessage.MessageId,
                ContextMessageId = inboundMessage.ContextMessageId ?? candidate.ContextMessageId,
                SenderPhoneE164 = _metaClient.NormalizePhoneNumber(inboundMessage.From),
                RecipientPhoneE164 = candidate.RecipientPhoneE164,
                WaId = inboundMessage.WaId,
                Status = status,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                PayloadJson = BuildInboundPayloadJson(inboundMessage, action),
                CreatedAtUtc = nowUtc,
                ProcessedAtUtc = nowUtc
            });

            await db.SaveChangesAsync(cancellationToken);

            // Centro de Notificaciones: avisa al negocio cuando el cliente cancela por WhatsApp,
            // para que pueda llenar ese espacio o contactarlo. Idempotente y tenant-scoped: el
            // guard inicial de inboundExists evita reprocesar el mismo mensaje en reintentos.
            if (action == WhatsAppReplyAction.Cancel)
            {
                try
                {
                    await notificationService.CreateAppointmentCancelledViaWhatsAppAsync(cita, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "No se pudo generar la notificación de cancelación para CitaId {CitaId}.",
                        cita.Id);
                }
            }

            _logger.LogInformation(
                "Respuesta WhatsApp procesada para CitaId {CitaId}. Accion {Action}.",
                cita.Id,
                action);
        }

        private async Task RegisterAmbiguousInboundAsync(
            InboundWhatsAppMessage inboundMessage,
            IReadOnlyList<WhatsAppTargetCandidate> candidates,
            CancellationToken cancellationToken)
        {
            var tenantIds = candidates
                .Select(candidate => candidate.TenantId)
                .Distinct()
                .ToList();

            if (tenantIds.Count != 1)
            {
                _logger.LogWarning(
                    "Respuesta WhatsApp ambigua entre multiples tenants. MetaMessageId {MetaMessageId}. CandidateCount {CandidateCount}.",
                    inboundMessage.MessageId,
                    candidates.Count);
                return;
            }

            await _tenantExecutionService.RunForTenantAsync(
                tenantIds[0],
                async (serviceProvider, _, ct) =>
                {
                    var db = serviceProvider.GetRequiredService<ApplicationDbContext>();
                    var exists = await db.WhatsAppMessageLogs
                        .AnyAsync(message =>
                            message.MetaMessageId == inboundMessage.MessageId &&
                            message.Direction == WhatsAppMessageDirections.Inbound,
                            ct);

                    if (exists)
                    {
                        return;
                    }

                    db.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
                    {
                        Direction = WhatsAppMessageDirections.Inbound,
                        NotificationType = WhatsAppNotificationTypes.Reply,
                        Provider = WhatsAppProviders.Meta,
                        MetaMessageId = inboundMessage.MessageId,
                        ContextMessageId = inboundMessage.ContextMessageId,
                        SenderPhoneE164 = _metaClient.NormalizePhoneNumber(inboundMessage.From),
                        WaId = inboundMessage.WaId,
                        Status = WhatsAppMessageStatuses.Ignored,
                        ErrorCode = "AMBIGUOUS_REPLY",
                        ErrorMessage = "Hay mas de una cita posible para este telefono.",
                        PayloadJson = BuildInboundPayloadJson(inboundMessage, WhatsAppReplyAction.Unknown),
                        CreatedAtUtc = DateTime.UtcNow,
                        ProcessedAtUtc = DateTime.UtcNow
                    });

                    await db.SaveChangesAsync(ct);
                },
                cancellationToken);
        }

        private static void ApplyProviderStatus(
            WhatsAppMessageLog message,
            MetaWhatsAppStatusUpdate statusUpdate)
        {
            var mappedStatus = MapProviderStatus(statusUpdate.Status);
            var eventTime = statusUpdate.TimestampUtc ?? DateTime.UtcNow;

            if (mappedStatus == WhatsAppMessageStatuses.Failed)
            {
                message.Status = WhatsAppMessageStatuses.Failed;
                message.FailedAtUtc = eventTime;
                message.ErrorCode = Trim(statusUpdate.ErrorCode, 80);
                message.ErrorMessage = Trim(statusUpdate.ErrorMessage, 1000);
                message.ProcessedAtUtc = DateTime.UtcNow;
                return;
            }

            if (mappedStatus == WhatsAppMessageStatuses.Sent &&
                (message.Status == WhatsAppMessageStatuses.Pending ||
                 message.Status == WhatsAppMessageStatuses.Processing))
            {
                message.Status = WhatsAppMessageStatuses.Sent;
            }

            if (mappedStatus == WhatsAppMessageStatuses.Sent)
            {
                message.SentAtUtc ??= eventTime;
            }
            else if (mappedStatus == WhatsAppMessageStatuses.Delivered)
            {
                message.DeliveredAtUtc ??= eventTime;
            }
            else if (mappedStatus == WhatsAppMessageStatuses.Read)
            {
                message.ReadAtUtc ??= eventTime;
            }

            message.ProcessedAtUtc = DateTime.UtcNow;
        }

        private static string MapProviderStatus(string status) =>
            status.Trim().ToLowerInvariant() switch
            {
                "sent" => WhatsAppMessageStatuses.Sent,
                "delivered" => WhatsAppMessageStatuses.Delivered,
                "read" => WhatsAppMessageStatuses.Read,
                "failed" => WhatsAppMessageStatuses.Failed,
                _ => WhatsAppMessageStatuses.Ignored
            };

        private static string ResolveTemplateName(MetaWhatsAppOptions options, string notificationType) =>
            notificationType switch
            {
                WhatsAppNotificationTypes.Reminder3Hours => options.ReminderTemplateName,
                WhatsAppNotificationTypes.Cancellation => options.CancellationTemplateName,
                _ => options.ConfirmationTemplateName
            };

        private static int GetReminderLeadTimeMinutes(MetaWhatsAppOptions options) =>
            options.ReminderLeadTimeMinutes <= 0 ? 180 : options.ReminderLeadTimeMinutes;

        private static string ResolveSkippedStatus(string? errorCode) =>
            errorCode switch
            {
                WhatsAppErrorCodes.ConsentMissing => WhatsAppMessageStatuses.SkippedConsentMissing,
                WhatsAppErrorCodes.TenantDisabled => WhatsAppMessageStatuses.SkippedTenantDisabled,
                WhatsAppErrorCodes.DailyLimitExceeded => WhatsAppMessageStatuses.SkippedDailyLimitExceeded,
                WhatsAppErrorCodes.NoActiveWhatsAppAddon => WhatsAppMessageStatuses.SkippedSubscriptionRequired,
                WhatsAppErrorCodes.NoActiveBaseSubscription => WhatsAppMessageStatuses.SkippedSubscriptionRequired,
                WhatsAppErrorCodes.NotConfigured => WhatsAppMessageStatuses.SkippedConfiguration,
                WhatsAppErrorCodes.SubscriptionRequired => WhatsAppMessageStatuses.SkippedSubscriptionRequired,
                WhatsAppErrorCodes.MonthlyLimitExceeded => WhatsAppMessageStatuses.SkippedMonthlyLimitExceeded,
                WhatsAppErrorCodes.InsufficientBalance => WhatsAppMessageStatuses.SkippedMonthlyLimitExceeded,
                WhatsAppErrorCodes.UserDisabled => WhatsAppMessageStatuses.SkippedUserDisabled,
                WhatsAppErrorCodes.NotificationTypeDisabled => WhatsAppMessageStatuses.SkippedUserDisabled,
                WhatsAppErrorCodes.InvalidPhone => WhatsAppMessageStatuses.SkippedInvalidPhone,
                _ => WhatsAppMessageStatuses.SkippedConfiguration
            };

        private static string BuildQueuedPayloadJson(Cita cita, string notificationType) =>
            JsonSerializer.Serialize(new
            {
                phase = "queued",
                notificationType,
                citaId = cita.Id,
                citaFechaHora = cita.FechaHoraCita,
                source = WhatsAppConsentPolicy.ResolveSource(cita),
                hasClienteId = cita.ClienteId.HasValue
            }, JsonOptions);

        private static string BuildSkippedPayloadJson(
            Cita cita,
            string notificationType,
            string reason,
            string? source = null,
            bool? hasClienteId = null) =>
            JsonSerializer.Serialize(new
            {
                phase = "skipped",
                reason,
                notificationType,
                citaId = cita.Id,
                source = source ?? WhatsAppConsentPolicy.ResolveSource(cita),
                hasClienteId = hasClienteId ?? cita.ClienteId.HasValue
            }, JsonOptions);

        private static string BuildSendResultPayloadJson(
            WhatsAppMessageLog message,
            MetaWhatsAppSendResult sendResult) =>
            JsonSerializer.Serialize(new
            {
                phase = sendResult.Success ? "sent" : "send_failed",
                message.NotificationType,
                message.CitaId,
                sendResult.MetaMessageId,
                statusCode = sendResult.StatusCode.HasValue ? (int?)sendResult.StatusCode.Value : null,
                sendResult.ErrorCode,
                sendResult.ErrorType,
                sendResult.ErrorSubcode,
                sendResult.ErrorMessage,
                sendResult.FbTraceId,
                sendResult.ShouldRetry,
                sendResult.Endpoint,
                responseBody = sendResult.ResponseBody
            }, JsonOptions);

        private static string BuildInboundPayloadJson(
            InboundWhatsAppMessage inboundMessage,
            WhatsAppReplyAction action) =>
            JsonSerializer.Serialize(new
            {
                phase = "inbound_reply",
                inboundMessage.MessageId,
                inboundMessage.ContextMessageId,
                inboundMessage.From,
                inboundMessage.WaId,
                inboundMessage.Text,
                inboundMessage.ButtonText,
                inboundMessage.ButtonPayload,
                inboundMessage.InteractiveButtonId,
                inboundMessage.InteractiveButtonTitle,
                action = action.ToString()
            }, JsonOptions);

        private static string BuildStatusPayloadJson(MetaWhatsAppStatusUpdate statusUpdate) =>
            JsonSerializer.Serialize(new
            {
                phase = "status",
                statusUpdate.MessageId,
                statusUpdate.Status,
                statusUpdate.TimestampUtc,
                statusUpdate.RecipientPhone,
                statusUpdate.ErrorCode,
                statusUpdate.ErrorMessage
            }, JsonOptions);

        private static string? Trim(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private sealed record WhatsAppTargetCandidate(
            Guid TenantId,
            int CitaId,
            string? ContextMessageId,
            string? RecipientPhoneE164);

    }
}
