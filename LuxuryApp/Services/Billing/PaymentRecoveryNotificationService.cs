using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Common;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Billing
{
    public interface IPaymentRecoveryNotificationService
    {
        /// <summary>
        /// Envía las notificaciones pendientes de los incidentes abiertos/vencidos: correo inicial de
        /// gracia, recordatorio antes de vencer y aviso de suspensión (solo si el acceso quedó
        /// suspendido). Respeta estrictamente <c>SendEmailNotifications</c>: si es false, no envía y
        /// solo deja rastro dry-run. Idempotente (máx. 1 correo por etapa/incidente). El HTTP del
        /// correo ocurre SIEMPRE fuera de la transacción. Devuelve cuántos correos (o dry-runs) procesó.
        /// </summary>
        Task<int> RunPendingNotificationsAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Notificaciones de recuperación de pago. Corre desde el worker (sin contexto de tenant): escanea
    /// incidentes cross-tenant en solo lectura, envía el correo FUERA de transacción y luego persiste
    /// los marcadores de idempotencia (NotificationCount / LastNotificationAtUtc / LastReminderAtUtc)
    /// bajo <c>BeginScope(tenantId)</c> para que el UPDATE del incidente (ITenantEntity) pase el RLS.
    /// NUNCA envía WhatsApp ni almacena datos de tarjeta o recurrentUrl.
    /// </summary>
    public sealed class PaymentRecoveryNotificationService : IPaymentRecoveryNotificationService
    {
        private readonly ApplicationDbContext _db;
        private readonly ITenantExecutionContextAccessor _tenantExecutionContextAccessor;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly BillingPaymentRecoveryOptions _options;
        private readonly IPaymentRecoveryEmailSender _emailSender;
        private readonly PublicSiteOptions _publicSiteOptions;
        private readonly ILogger<PaymentRecoveryNotificationService> _logger;
        private readonly ITenantOwnerResolver _ownerResolver;

        public PaymentRecoveryNotificationService(
            ApplicationDbContext db,
            ITenantExecutionContextAccessor tenantExecutionContextAccessor,
            IBusinessDateTimeProvider clock,
            IOptions<BillingPaymentRecoveryOptions> options,
            IPaymentRecoveryEmailSender emailSender,
            IOptions<PublicSiteOptions> publicSiteOptions,
            ILogger<PaymentRecoveryNotificationService> logger,
            ITenantOwnerResolver ownerResolver)
        {
            _db = db;
            _tenantExecutionContextAccessor = tenantExecutionContextAccessor;
            _clock = clock;
            _options = options.Value;
            _emailSender = emailSender;
            _publicSiteOptions = publicSiteOptions.Value;
            _logger = logger;
            _ownerResolver = ownerResolver;
        }

        public async Task<int> RunPendingNotificationsAsync(CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                return 0;
            }

            var nowUtc = GetUtcNow();
            var reminderHours = Math.Clamp(_options.ReminderBeforeGraceEndsHours, 1, 240);

            // Candidatos cross-tenant (solo lectura): incidentes que aún pueden necesitar correo.
            var candidates = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(i =>
                    i.Status == PaymentIncidentStatus.Open ||
                    i.Status == PaymentIncidentStatus.GraceExpired)
                .OrderBy(i => i.CreatedAtUtc)
                .Select(i => new
                {
                    i.Id,
                    i.TenantId,
                    i.Status,
                    i.GraceEndsAtUtc,
                    i.LastNotificationAtUtc,
                    i.LastReminderAtUtc,
                    i.ClienteEmail
                })
                .Take(500)
                .ToListAsync(cancellationToken);

            var processed = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.TenantId == Guid.Empty)
                {
                    continue;
                }

                // Decidir UNA etapa por incidente por pase (evita mandar dos correos a la vez).
                var kind = ResolveKind(candidate.Status, candidate.GraceEndsAtUtc, candidate.LastNotificationAtUtc, candidate.LastReminderAtUtc, nowUtc, reminderHours);
                if (kind is null)
                {
                    continue;
                }

                // La suspensión solo se avisa si el acceso quedó realmente suspendido y aún no se avisó.
                if (kind == PaymentRecoveryEmailKind.Suspended &&
                    !await ShouldNotifySuspensionAsync(candidate.Id, candidate.TenantId, cancellationToken))
                {
                    continue;
                }

                try
                {
                    if (await ProcessOneAsync(candidate.Id, candidate.TenantId, candidate.ClienteEmail, kind.Value, candidate.GraceEndsAtUtc, nowUtc, cancellationToken))
                    {
                        processed++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _db.ChangeTracker.Clear();
                    _logger.LogError(ex, "No se pudo procesar la notificación del incidente {IncidentId}; se continúa.", candidate.Id);
                }
            }

            return processed;
        }

        private static PaymentRecoveryEmailKind? ResolveKind(
            PaymentIncidentStatus status,
            DateTime? graceEndsAtUtc,
            DateTime? lastNotificationAtUtc,
            DateTime? lastReminderAtUtc,
            DateTime nowUtc,
            int reminderHours)
        {
            if (status == PaymentIncidentStatus.Open)
            {
                if (lastNotificationAtUtc is null)
                {
                    return PaymentRecoveryEmailKind.PaymentFailed; // inicial
                }

                if (lastReminderAtUtc is null &&
                    graceEndsAtUtc is { } graceEnds &&
                    nowUtc >= graceEnds.AddHours(-reminderHours) &&
                    nowUtc < graceEnds)
                {
                    return PaymentRecoveryEmailKind.GraceReminder;
                }

                return null;
            }

            // GraceExpired: solo aplica el aviso de suspensión (se valida el estado real aparte).
            return PaymentRecoveryEmailKind.Suspended;
        }

        private async Task<bool> ShouldNotifySuspensionAsync(Guid incidentId, Guid tenantId, CancellationToken cancellationToken)
        {
            var suspended = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(s => s.TenantId == tenantId && s.PaymentRecoveryStatus == "Suspended", cancellationToken);

            if (!suspended)
            {
                return false;
            }

            // Idempotencia de la suspensión vía marcador de auditoría (no hay campo dedicado): si ya
            // hay un Sent o DryRun de la etapa Suspended para este incidente, no se reenvía.
            return !await SuspensionAlreadyNotifiedAsync(incidentId, cancellationToken);
        }

        // Marcador estable SIN caracteres comodín de LIKE ('%', '_', '[', ']'): en SQL Server,
        // string.Contains se traduce a LIKE y unos corchetes se interpretarían como clase de
        // caracteres, así que el token va en texto plano "kind=Suspended".
        private const string SuspensionMarker = "kind=" + nameof(PaymentRecoveryEmailKind.Suspended);

        private Task<bool> SuspensionAlreadyNotifiedAsync(Guid incidentId, CancellationToken cancellationToken) =>
            _db.PlatformAuditLogs.AnyAsync(l =>
                l.EntityId == incidentId.ToString() &&
                (l.Action == PlatformAuditActions.PaymentRecoveryNotificationSent ||
                 l.Action == PlatformAuditActions.PaymentRecoveryNotificationDryRun) &&
                l.Reason != null &&
                l.Reason.Contains(SuspensionMarker),
                cancellationToken);

        private async Task<bool> ProcessOneAsync(
            Guid incidentId,
            Guid tenantId,
            string? clienteEmail,
            PaymentRecoveryEmailKind kind,
            DateTime? graceEndsAtUtc,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var recipient = clienteEmail;
            if (string.IsNullOrWhiteSpace(recipient))
            {
                // Contacto por regla de owner (admin > funcionario). El orden alfabético anterior
                // podía mandar un aviso de cobro fallido a una cuenta de funcionario.
                recipient = await _ownerResolver.ResolveOwnerEmailAsync(tenantId, cancellationToken);
            }

            var tenantName = await _db.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => t.Nombre)
                .FirstOrDefaultAsync(cancellationToken);

            var sendEmails = _options.SendEmailNotifications;
            PaymentRecoveryEmailResult sendResult;

            if (sendEmails && !string.IsNullOrWhiteSpace(recipient))
            {
                // HTTP del correo SIEMPRE fuera de cualquier transacción abierta.
                var context = new PaymentRecoveryEmailContext
                {
                    ToEmail = recipient!,
                    DisplayName = tenantName,
                    GraceEndsDisplay = FormatCostaRica(graceEndsAtUtc),
                    UpdateUrl = _publicSiteOptions.ResolveAbsoluteUrl("/Billing/Suscripcion"),
                    TenantId = tenantId,
                    IncidentId = incidentId
                };
                sendResult = await _emailSender.SendAsync(kind, context, cancellationToken);
            }
            else if (!sendEmails)
            {
                sendResult = PaymentRecoveryEmailResult.Fail("dry-run"); // no se envía; se audita dry-run
            }
            else
            {
                sendResult = PaymentRecoveryEmailResult.Fail("sin correo de destino");
            }

            // Ahora sí: persistir marcadores + auditoría bajo scope del tenant (RLS).
            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            _db.ChangeTracker.Clear();

            var incident = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == incidentId && i.TenantId == tenantId, cancellationToken);
            if (incident is null)
            {
                return false;
            }

            // Re-verificación del guard bajo tracked: pudo resolverse/avanzar entre la lectura y ahora.
            if (!IsStillDue(incident, kind, graceEndsAtUtc, nowUtc))
            {
                return false;
            }

            if (!sendEmails)
            {
                // Dry-run: NO se envía, pero se avanza el marcador (una sola vez por etapa) y se audita.
                AdvanceMarkers(incident, kind, nowUtc);
                await MarkSubscriptionNotifiedAsync(tenantId, nowUtc, cancellationToken);
                _db.PlatformAuditLogs.Add(BuildAudit(
                    PlatformAuditActions.PaymentRecoveryNotificationDryRun, tenantId, incidentId,
                    $"kind={kind}. Notificación NO enviada (SendEmailNotifications=false)."));
                await _db.SaveChangesAsync(cancellationToken);
                return true;
            }

            if (!sendResult.Sent)
            {
                // Fallo real de envío: se audita y NO se avanza el marcador (se reintenta el próximo pase).
                _db.PlatformAuditLogs.Add(BuildAudit(
                    PlatformAuditActions.PaymentRecoveryNotificationFailed, tenantId, incidentId,
                    $"kind={kind}. Falló el envío del correo de recuperación. Detalle: {Trim(sendResult.Error, 200)}"));
                await _db.SaveChangesAsync(cancellationToken);
                return false;
            }

            AdvanceMarkers(incident, kind, nowUtc);
            await MarkSubscriptionNotifiedAsync(tenantId, nowUtc, cancellationToken);
            _db.PlatformAuditLogs.Add(BuildAudit(
                PlatformAuditActions.PaymentRecoveryNotificationSent, tenantId, incidentId,
                $"kind={kind}. Correo de recuperación enviado a {SensitiveDataMasker.MaskEmail(recipient)}."));
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        private static bool IsStillDue(SubscriptionPaymentIncident incident, PaymentRecoveryEmailKind kind, DateTime? graceEndsAtUtc, DateTime nowUtc) =>
            kind switch
            {
                PaymentRecoveryEmailKind.PaymentFailed =>
                    incident.Status == PaymentIncidentStatus.Open && incident.LastNotificationAtUtc is null,
                PaymentRecoveryEmailKind.GraceReminder =>
                    incident.Status == PaymentIncidentStatus.Open && incident.LastReminderAtUtc is null,
                _ => incident.Status == PaymentIncidentStatus.GraceExpired
            };

        private static void AdvanceMarkers(SubscriptionPaymentIncident incident, PaymentRecoveryEmailKind kind, DateTime nowUtc)
        {
            incident.NotificationCount += 1;
            incident.UpdatedAtUtc = nowUtc;
            switch (kind)
            {
                case PaymentRecoveryEmailKind.PaymentFailed:
                    incident.LastNotificationAtUtc = nowUtc;
                    break;
                case PaymentRecoveryEmailKind.GraceReminder:
                    incident.LastReminderAtUtc = nowUtc;
                    break;
                    // Suspended: la idempotencia vive en el marcador de auditoría [Suspended].
            }
        }

        private async Task MarkSubscriptionNotifiedAsync(Guid tenantId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var subscription = await _db.Suscripciones
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .FirstOrDefaultAsync(cancellationToken);
            if (subscription is not null)
            {
                subscription.LastPaymentRecoveryNotificationAtUtc = nowUtc;
            }
        }

        private string? FormatCostaRica(DateTime? utc)
        {
            if (utc is not { } value)
            {
                return null;
            }

            // Costa Rica no tiene horario de verano: el offset del reloj de negocio es constante.
            var local = DateTime.SpecifyKind(value, DateTimeKind.Utc) + _clock.NowOffset().Offset;
            return local.ToString("dd/MM/yyyy HH:mm");
        }

        private PlatformAuditLog BuildAudit(string action, Guid tenantId, Guid incidentId, string reason) =>
            new()
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = action,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = incidentId.ToString(),
                TenantId = tenantId,
                Reason = reason.Length <= 500 ? reason : reason[..500],
                CreatedAtUtc = GetUtcNow()
            };

        private DateTime GetUtcNow() => _clock.NowOffset().UtcDateTime;

        private static string? Trim(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);
    }
}
