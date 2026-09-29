using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Billing
{
    public enum ProviderExpirySyncOutcome
    {
        /// <summary>Integración apagada, sin suscriptor, o suscriptor no confiable: no se tocó nada.</summary>
        Skipped,

        /// <summary>Se guardó el expire del proveedor; la diferencia con lo local es despreciable.</summary>
        InSync,

        /// <summary>El proveedor cobra más TARDE: se extendió la vigencia local.</summary>
        Extended,

        /// <summary>El proveedor cobra más TEMPRANO: se alertó, sin acortar acceso.</summary>
        EarlierAlerted
    }

    public interface IProviderExpirySyncService
    {
        bool IsEnabled { get; }

        /// <summary>
        /// Sincroniza la fecha real de cobro del proveedor para TODAS las suscripciones Tilopay
        /// activas/morosas con suscriptor conocido. Best-effort, aislado por tenant. Devuelve
        /// cuántas se extendieron / alertaron para el reporte del pase.
        /// </summary>
        Task SyncActiveSubscriptionsAsync(
            BillingReconciliationReport report,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Concilia la vigencia local con la fecha real de cobro de TiloPay (getSuscriptorRepeat → expire).
    ///
    /// Motivo: TiloPay puede reactivar un suscriptor previamente eliminado y EXTENDER su expire, o
    /// conservar una fecha de cobro distinta de la que calcula LuxuryCloud. Si LuxuryCloud cree que
    /// vence antes, marca moroso/suspende/alerta injustamente por un cobro que el proveedor todavía
    /// no va a hacer (p. ej. tras un cambio de plan: local vence un mes antes que el proveedor).
    ///
    /// Principio: LuxuryCloud manda sobre plan/cupo/acceso; TiloPay es la fuente de verdad de la
    /// próxima fecha de cobro cuando el suscriptor está Active. La fecha del proveedor solo puede
    /// EXTENDER la vigencia, nunca acortarla (acortar quitaría servicio ya pagado). Un expire
    /// anterior al local solo se ALERTA para revisión manual.
    ///
    /// No toca cancelación, late repair, retry ni target Delete: solo lee expire y sincroniza fechas.
    /// </summary>
    public sealed class ProviderExpirySyncService : IProviderExpirySyncService
    {
        private readonly ApplicationDbContext _db;
        private readonly ITilopayRepeatAdminService _adminService;
        private readonly ITenantExecutionContextAccessor _tenantExecutionContextAccessor;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly BillingReconciliationOptions _options;
        private readonly ILogger<ProviderExpirySyncService> _logger;

        public ProviderExpirySyncService(
            ApplicationDbContext db,
            ITilopayRepeatAdminService adminService,
            ITenantExecutionContextAccessor tenantExecutionContextAccessor,
            IBusinessDateTimeProvider clock,
            IOptions<BillingReconciliationOptions> options,
            ILogger<ProviderExpirySyncService> logger)
        {
            _db = db;
            _adminService = adminService;
            _tenantExecutionContextAccessor = tenantExecutionContextAccessor;
            _clock = clock;
            _options = options.Value;
            _logger = logger;
        }

        public bool IsEnabled => _adminService.IsEnabled;

        public async Task SyncActiveSubscriptionsAsync(
            BillingReconciliationReport report,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled)
            {
                return;
            }

            // Escaneo cross-tenant SOLO lectura. Solo Activa/Morosa con suscriptor y plan conocidos:
            // sin suscriptor no hay a quién buscar el expire, y un plan nulo no se puede consultar.
            var subscriptions = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s =>
                    s.Proveedor == PaymentProviderType.Tilopay &&
                    s.TilopayRecurringPlanId != null &&
                    s.ProviderSubscriptionId != null &&
                    (s.Estado == EstadoSuscripcion.Activa || s.Estado == EstadoSuscripcion.Morosa))
                .Select(s => new
                {
                    s.Id,
                    s.TenantId,
                    s.TilopayRecurringPlanId,
                    s.ProviderSubscriptionId,
                    s.CodigoPlan
                })
                .ToListAsync(cancellationToken);

            if (subscriptions.Count == 0)
            {
                return;
            }

            // Una llamada a getSuscriptorRepeat por PLAN (devuelve todos sus suscriptores), no una
            // por suscripción: evita el N+1 contra el API del proveedor.
            foreach (var planGroup in subscriptions.GroupBy(s => s.TilopayRecurringPlanId!.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<TilopaySubscriber> providerSubscribers;
                try
                {
                    providerSubscribers = await _adminService.GetSuscriptorRepeatAsync(planGroup.Key, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "No se pudo consultar getSuscriptorRepeat para sincronizar expire. PlanId {PlanId}.",
                        planGroup.Key);
                    continue;
                }

                foreach (var subscription in planGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (subscription.TenantId == Guid.Empty)
                    {
                        continue;
                    }

                    var match = providerSubscribers.FirstOrDefault(p =>
                        string.Equals(p.SubscriberId, subscription.ProviderSubscriptionId, StringComparison.OrdinalIgnoreCase));

                    // Solo confiamos en el expire de un suscriptor ACTIVE. Ausente, inactivo o con
                    // status desconocido ⇒ no se toca la fecha (no adivinamos vencimientos).
                    if (match is null ||
                        !ProviderSubscriberStatusRules.IsProviderSubscriberActive(match.Status) ||
                        match.ExpiresAtUtc is null)
                    {
                        continue;
                    }

                    try
                    {
                        _db.ChangeTracker.Clear();
                        await SyncOneAsync(subscription.Id, subscription.TenantId, match, report, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _db.ChangeTracker.Clear();
                        _logger.LogError(
                            ex,
                            "Sincronización de expire falló para la suscripción {SubscriptionId} del tenant {TenantId}; se continúa.",
                            subscription.Id,
                            subscription.TenantId);
                    }
                }
            }
        }

        private async Task SyncOneAsync(
            Guid subscriptionId,
            Guid tenantId,
            TilopaySubscriber subscriber,
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(tenantId);

            var subscription = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Id == subscriptionId && s.TenantId == tenantId, cancellationToken);

            if (subscription is null)
            {
                return;
            }

            var providerExpiresAtUtc = subscriber.ExpiresAtUtc!.Value;
            var nowUtc = GetUtcNow();
            var tolerance = TimeSpan.FromHours(Math.Max(1, _options.ProviderExpiryReconcileMinDifferenceHours));

            // Siempre se guarda el dato crudo del proveedor: es el rastro de auditoría, aunque no
            // haya que cambiar ninguna fecha.
            subscription.ProviderExpiresAtUtc = providerExpiresAtUtc;
            subscription.ProviderExpiryRaw = subscriber.ExpiresRaw;
            subscription.ProviderExpiryLastSyncedUtc = nowUtc;

            var localEndUtc = subscription.FechaFin;
            var outcome = ProviderExpirySyncOutcome.InSync;

            if (SubscriptionEffectiveDates.ProviderIsAhead(localEndUtc, providerExpiresAtUtc, tolerance))
            {
                // El proveedor cobra más tarde: extender la vigencia local para no marcar moroso
                // antes de tiempo. Se mueven FechaFin y el próximo cobro; el ciclo de facturación
                // no se recalcula (no es un cambio de plan, solo el vencimiento real).
                var previousEndUtc = subscription.FechaFin;
                subscription.FechaFin = providerExpiresAtUtc;
                subscription.FechaProximoCobroUtc = providerExpiresAtUtc;
                subscription.FechaUltimaActualizacionUtc = nowUtc;
                // El período de gracia viejo se calculó sobre la fecha anterior: ya no aplica.
                if (subscription.FechaFinGraciaUtc is { } grace && grace < providerExpiresAtUtc)
                {
                    subscription.FechaFinGraciaUtc = null;
                }

                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = PlatformAuditActions.BillingProviderExpiryReconciled,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    EntityId = subscription.Id.ToString(),
                    TenantId = tenantId,
                    Reason = Trim(
                        $"Vigencia extendida a la fecha real de TiloPay. Local anterior {previousEndUtc:yyyy-MM-dd HH:mm} UTC → proveedor {providerExpiresAtUtc:yyyy-MM-dd HH:mm} UTC (expire {subscriber.ExpiresRaw}). " +
                        $"SuscriptorSuffix {SensitiveDataMasker.MaskReference(subscriber.SubscriberId)}. Plan {subscription.CodigoPlan}.",
                        500),
                    CreatedAtUtc = nowUtc
                });

                outcome = ProviderExpirySyncOutcome.Extended;
                report.ProviderExpiriesReconciled++;

                _logger.LogWarning(
                    "Expire del proveedor POSTERIOR al local: vigencia extendida. TenantId {TenantId}. SubscriptionId {SubscriptionId}. Local {Local} → Proveedor {Provider}.",
                    tenantId,
                    subscription.Id,
                    previousEndUtc,
                    providerExpiresAtUtc);
            }
            else if (SubscriptionEffectiveDates.ProviderIsEarlier(localEndUtc, providerExpiresAtUtc, tolerance))
            {
                // El proveedor vence ANTES que lo local. NO se acorta: podría quitar servicio ya
                // pagado. Solo se alerta para revisión manual (idempotente por cooldown).
                if (await TryAlertEarlierAsync(subscription, tenantId, providerExpiresAtUtc, subscriber, nowUtc, cancellationToken))
                {
                    outcome = ProviderExpirySyncOutcome.EarlierAlerted;
                    report.ProviderExpiryEarlierAlerts++;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            _db.ChangeTracker.Clear();

            report.ProviderExpiriesSynced++;

            if (outcome == ProviderExpirySyncOutcome.InSync)
            {
                _logger.LogDebug(
                    "Expire del proveedor sincronizado (sin cambio de fecha). TenantId {TenantId}. SubscriptionId {SubscriptionId}.",
                    tenantId,
                    subscription.Id);
            }
        }

        private async Task<bool> TryAlertEarlierAsync(
            Suscripcion subscription,
            Guid tenantId,
            DateTime providerExpiresAtUtc,
            TilopaySubscriber subscriber,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var cooldownCutoffUtc = nowUtc.AddHours(-Math.Max(1, _options.AlertCooldownHours));
            var entityId = subscription.Id.ToString();

            var alreadyAlerted = await _db.PlatformAuditLogs.AnyAsync(
                log =>
                    log.Action == PlatformAuditActions.BillingProviderExpiryEarlierThanLocal &&
                    log.EntityId == entityId &&
                    log.CreatedAtUtc >= cooldownCutoffUtc,
                cancellationToken);

            if (alreadyAlerted)
            {
                return false;
            }

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.BillingProviderExpiryEarlierThanLocal,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = entityId,
                TenantId = tenantId,
                Reason = Trim(
                    $"El expire de TiloPay ({providerExpiresAtUtc:yyyy-MM-dd HH:mm} UTC, expire {subscriber.ExpiresRaw}) es ANTERIOR a la vigencia local ({subscription.FechaFin:yyyy-MM-dd HH:mm} UTC). " +
                    $"NO se acortó el acceso (evita quitar servicio pagado). Requiere revisión manual. " +
                    $"SuscriptorSuffix {SensitiveDataMasker.MaskReference(subscriber.SubscriberId)}. Plan {subscription.CodigoPlan}.",
                    500),
                CreatedAtUtc = nowUtc
            });

            _logger.LogWarning(
                "Expire del proveedor ANTERIOR al local (posible corte injusto): se alerta sin acortar. TenantId {TenantId}. SubscriptionId {SubscriptionId}.",
                tenantId,
                subscription.Id);

            return true;
        }

        private DateTime GetUtcNow() => _clock.NowOffset().UtcDateTime;

        private static string Trim(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];
    }
}
