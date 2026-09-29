using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Billing
{
    /// <summary>Contexto mínimo para resolver y persistir el id_suscriptor de un cobro recurrente.</summary>
    public sealed record SubscriberResolutionContext
    {
        public required Guid TenantId { get; init; }
        public required int TilopayRecurringPlanId { get; init; }
        public string? Email { get; init; }
        public Guid? PaymentId { get; init; }
        public bool IsAddon { get; init; }

        /// <summary>Origen para la auditoría: "webhook_registration", "webhook_payment", "reconciliation".</summary>
        public string Source { get; init; } = "webhook";
    }

    public enum SubscriberPersistenceOutcome
    {
        /// <summary>Integración deshabilitada o ya había subscriber: no se hizo nada.</summary>
        Skipped,
        Resolved,
        Pending,
        Ambiguous,
        Failed
    }

    public interface ISubscriberResolutionService
    {
        bool IsEnabled { get; }

        /// <summary>
        /// Resuelve el id_suscriptor por (plan, email) vía API admin y lo persiste en el pago y en
        /// la suscripción/add-on en una transacción CORTA y separada. La llamada HTTP ocurre ANTES
        /// de abrir la transacción de BD (nunca HTTP dentro de una transacción SQL abierta). Nunca
        /// lanza: cualquier fallo se audita y devuelve un outcome, para no romper la activación.
        /// </summary>
        Task<SubscriberPersistenceOutcome> TryResolveAndPersistAsync(
            SubscriberResolutionContext context,
            CancellationToken cancellationToken = default);
    }

    public sealed class SubscriberResolutionService : ISubscriberResolutionService
    {
        private static readonly TimeSpan AlertCooldown = TimeSpan.FromHours(6);

        private readonly ApplicationDbContext _db;
        private readonly ITilopayRepeatAdminService _adminService;
        private readonly ITenantExecutionContextAccessor _tenantExecutionContextAccessor;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly ILogger<SubscriberResolutionService> _logger;

        public SubscriberResolutionService(
            ApplicationDbContext db,
            ITilopayRepeatAdminService adminService,
            ITenantExecutionContextAccessor tenantExecutionContextAccessor,
            IBusinessDateTimeProvider clock,
            ILogger<SubscriberResolutionService> logger)
        {
            _db = db;
            _adminService = adminService;
            _tenantExecutionContextAccessor = tenantExecutionContextAccessor;
            _clock = clock;
            _logger = logger;
        }

        public bool IsEnabled => _adminService.IsEnabled;

        public async Task<SubscriberPersistenceOutcome> TryResolveAndPersistAsync(
            SubscriberResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled)
            {
                return SubscriberPersistenceOutcome.Skipped;
            }

            try
            {
                // 1) Llamada HTTP externa PRIMERO, sin ninguna transacción de BD abierta.
                var resolution = await _adminService.ResolveSubscriberAsync(
                    context.TilopayRecurringPlanId,
                    context.Email,
                    cancellationToken);

                // 2) Persistencia en transacción corta y separada.
                return resolution.Status switch
                {
                    SubscriberResolutionStatus.Found => await PersistResolvedAsync(context, resolution.Subscriber!, cancellationToken),
                    SubscriberResolutionStatus.NotFound => await AuditNonResolvedAsync(
                        context,
                        PlatformAuditActions.ProviderSubscriberResolutionPending,
                        SubscriberPersistenceOutcome.Pending,
                        resolution.Detail ?? "Sin suscriptor en TiloPay para el email/plan al momento de la consulta.",
                        cancellationToken),
                    SubscriberResolutionStatus.Ambiguous => await AuditNonResolvedAsync(
                        context,
                        PlatformAuditActions.ProviderSubscriberResolutionAmbiguous,
                        SubscriberPersistenceOutcome.Ambiguous,
                        resolution.Detail ?? $"{resolution.MatchCount} suscriptores coinciden por email; requiere revisión manual.",
                        cancellationToken),
                    _ => await AuditNonResolvedAsync(
                        context,
                        PlatformAuditActions.ProviderSubscriberResolutionFailed,
                        SubscriberPersistenceOutcome.Failed,
                        resolution.Detail ?? "La consulta de suscriptor a TiloPay falló.",
                        cancellationToken)
                };
            }
            catch (Exception ex)
            {
                // Nunca romper la activación por un fallo de resolución.
                _logger.LogError(
                    ex,
                    "Resolución de suscriptor falló inesperadamente. TenantId {TenantId}. PlanId {PlanId}. Source {Source}.",
                    context.TenantId,
                    context.TilopayRecurringPlanId,
                    context.Source);

                try
                {
                    return await AuditNonResolvedAsync(
                        context,
                        PlatformAuditActions.ProviderSubscriberResolutionFailed,
                        SubscriberPersistenceOutcome.Failed,
                        "Excepción inesperada al resolver el suscriptor.",
                        cancellationToken);
                }
                catch
                {
                    return SubscriberPersistenceOutcome.Failed;
                }
            }
        }

        /// <summary>
        /// True cuando el add-on ya se movió al plan recurrente que se acaba de pagar pero sigue
        /// guardando el suscriptor del paquete ANTERIOR (TiloPay no manda id_suscriptor en el
        /// webhook). Solo entonces se adopta el resuelto: sin la coincidencia de plan no hay
        /// evidencia de que el id guardado sea el viejo, y pisarlo sería peor que dejarlo.
        /// </summary>
        private static bool IsStaleAddonSubscriber(
            TenantSubscriptionAddon addon,
            SubscriberResolutionContext context,
            TilopaySubscriber subscriber) =>
            addon.TilopayRecurringPlanId == context.TilopayRecurringPlanId &&
            addon.PendingCancellationTilopayRecurringPlanId.HasValue &&
            addon.PendingCancellationTilopayRecurringPlanId.Value != context.TilopayRecurringPlanId &&
            string.IsNullOrWhiteSpace(addon.PendingCancellationProviderSubscriptionId) &&
            !string.IsNullOrWhiteSpace(subscriber.SubscriberId) &&
            !string.Equals(
                addon.ProviderSubscriptionId?.Trim(),
                subscriber.SubscriberId.Trim(),
                StringComparison.OrdinalIgnoreCase);

        private async Task<SubscriberPersistenceOutcome> PersistResolvedAsync(
            SubscriberResolutionContext context,
            TilopaySubscriber subscriber,
            CancellationToken cancellationToken)
        {
            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(context.TenantId);
            using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var nowUtc = GetUtcNow();
                var changed = false;

                if (context.PaymentId.HasValue)
                {
                    var payment = await _db.PagosSuscripcion
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(p => p.Id == context.PaymentId.Value, cancellationToken);

                    if (payment is not null && string.IsNullOrWhiteSpace(payment.ProviderSubscriberId))
                    {
                        payment.ProviderSubscriberId = subscriber.SubscriberId;
                        payment.FechaActualizacionUtc = nowUtc;
                        changed = true;
                    }
                }

                if (context.IsAddon)
                {
                    var addon = await _db.TenantSubscriptionAddons
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(a => a.TenantId == context.TenantId, cancellationToken);

                    if (addon is not null && string.IsNullOrWhiteSpace(addon.ProviderSubscriptionId))
                    {
                        addon.ProviderSubscriptionId = subscriber.SubscriberId;
                        addon.ProviderCancellation = ProviderCancellationState.NotRequired;
                        addon.ProviderCancellationSubscriptionId = null;
                        addon.ProviderCancelledAtUtc = null;
                        addon.UpdatedAtUtc = nowUtc;
                        changed = true;
                    }
                    else if (addon is not null && IsStaleAddonSubscriber(addon, context, subscriber))
                    {
                        // SUSCRIPTOR RESUELTO TARDE en un CAMBIO de paquete: TiloPay no manda
                        // id_suscriptor en el webhook, así que la activación conservó el id ANTERIOR
                        // mientras el plan recurrente ya era el nuevo. La fila quedaba apuntando al
                        // suscriptor viejo: cancelarla habría dado de baja el paquete recién pagado y
                        // el viejo habría seguido cobrando. Se adopta el resuelto y el anterior queda
                        // pendiente de baja verificada (Strategy B), nunca al revés.
                        var staleSubscriberId = addon.ProviderSubscriptionId;

                        addon.ProviderSubscriptionId = subscriber.SubscriberId;
                        addon.PendingCancellationProviderSubscriptionId = staleSubscriberId;
                        // El plan recurrente del suscriptor viejo quedó APARCADO en la activación
                        // (ActivarAddonWhatsAppRecurrenteAsync) justamente para poder verificar la
                        // baja ahora: sin él, getSuscriptorRepeat no tiene contra qué verificar.
                        addon.ProviderCancellation = ProviderCancellationState.PendingManualCancellation;
                        addon.ProviderCancellationSubscriptionId = null;
                        addon.ProviderCancelledAtUtc = null;
                        addon.ProviderCancellationAttemptCount = 0;
                        addon.ProviderCancellationLastAttemptUtc = null;
                        addon.ProviderCancellationNextRetryUtc = null;
                        addon.UpdatedAtUtc = nowUtc;
                        changed = true;

                        _db.PlatformAuditLogs.Add(new PlatformAuditLog
                        {
                            Id = Guid.NewGuid(),
                            ActorUserId = "system",
                            ActorEmail = "system",
                            Action = PlatformAuditActions.AddonSubscriberAdoptedAfterLateResolution,
                            EntityType = PlatformAuditEntityTypes.WhatsAppAddon,
                            EntityId = addon.Id.ToString(),
                            TenantId = context.TenantId,
                            Reason = $"Add-on: el suscriptor del plan {context.TilopayRecurringPlanId} se resolvió tarde. Se adoptó {SensitiveDataMasker.MaskReference(subscriber.SubscriberId)} y el anterior {SensitiveDataMasker.MaskReference(staleSubscriberId)} queda pendiente de baja verificada.",
                            CreatedAtUtc = nowUtc
                        });
                    }
                }
                else
                {
                    var subscription = await _db.Suscripciones
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(s => s.TenantId == context.TenantId, cancellationToken);

                    if (subscription is not null && string.IsNullOrWhiteSpace(subscription.ProviderSubscriptionId))
                    {
                        subscription.ProviderSubscriptionId = subscriber.SubscriberId;
                        subscription.FechaUltimaActualizacionUtc = nowUtc;
                        changed = true;
                    }
                }

                if (changed)
                {
                    _db.PlatformAuditLogs.Add(new PlatformAuditLog
                    {
                        Id = Guid.NewGuid(),
                        ActorUserId = "system",
                        ActorEmail = "system",
                        Action = PlatformAuditActions.ProviderSubscriberResolved,
                        EntityType = PlatformAuditEntityTypes.Subscription,
                        EntityId = context.PaymentId?.ToString(),
                        TenantId = context.TenantId,
                        Reason = $"id_suscriptor resuelto por (plan {context.TilopayRecurringPlanId}, email) desde {context.Source}. SubscriberIdSuffix {SensitiveDataMasker.MaskReference(subscriber.SubscriberId)}.",
                        CreatedAtUtc = nowUtc
                    });

                    await _db.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Suscriptor TiloPay resuelto y persistido. TenantId {TenantId}. PlanId {PlanId}. IsAddon {IsAddon}. SubscriberIdSuffix {Suffix}. Source {Source}. Changed {Changed}.",
                    context.TenantId,
                    context.TilopayRecurringPlanId,
                    context.IsAddon,
                    SensitiveDataMasker.MaskReference(subscriber.SubscriberId),
                    context.Source,
                    changed);

                return SubscriberPersistenceOutcome.Resolved;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        private async Task<SubscriberPersistenceOutcome> AuditNonResolvedAsync(
            SubscriberResolutionContext context,
            string action,
            SubscriberPersistenceOutcome outcome,
            string reason,
            CancellationToken cancellationToken)
        {
            var entityId = context.PaymentId?.ToString();
            var cooldownCutoff = GetUtcNow().Subtract(AlertCooldown);

            var recentlyAudited = await _db.PlatformAuditLogs.AnyAsync(
                log => log.Action == action &&
                       log.TenantId == context.TenantId &&
                       log.EntityId == entityId &&
                       log.CreatedAtUtc >= cooldownCutoff,
                cancellationToken);

            if (!recentlyAudited)
            {
                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = action,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    EntityId = entityId,
                    TenantId = context.TenantId,
                    Reason = Trim($"Plan {context.TilopayRecurringPlanId} desde {context.Source}: {reason}", 500),
                    CreatedAtUtc = GetUtcNow()
                });

                await _db.SaveChangesAsync(cancellationToken);
            }

            _logger.LogWarning(
                "Resolución de suscriptor no concluyó. TenantId {TenantId}. PlanId {PlanId}. Outcome {Outcome}. Source {Source}.",
                context.TenantId,
                context.TilopayRecurringPlanId,
                outcome,
                context.Source);

            return outcome;
        }

        private DateTime GetUtcNow() => _clock.NowOffset().UtcDateTime;

        private static string Trim(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];
    }
}
