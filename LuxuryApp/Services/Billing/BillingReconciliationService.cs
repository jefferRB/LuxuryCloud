using System.Text.Json;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Billing
{
    /// <summary>Desenlace de un reintento de cancelación del suscriptor viejo, por intent.</summary>
    public enum PlanChangeCancellationRetryStatus
    {
        /// <summary>Baja VERIFICADA en TiloPay: el viejo ya no puede cobrar.</summary>
        Cancelled,

        /// <summary>Se llamó a TiloPay pero el viejo sigue pendiente: se reintentará con backoff.</summary>
        AttemptedStillPending,

        /// <summary>No se llamó: la cancelación automática está apagada. No consume presupuesto.</summary>
        SkippedAutoCancelDisabled,

        /// <summary>No se llamó: el intent está en cooldown de backoff.</summary>
        SkippedBackoff,

        /// <summary>No se llamó: faltan datos para un intento verificable.</summary>
        SkippedNotEligible,

        /// <summary>Nada que hacer: el intent no existe o ya no tiene cancelación pendiente.</summary>
        NotPending
    }

    public sealed record PlanChangeCancellationRetryOutcome
    {
        public required PlanChangeCancellationRetryStatus Status { get; init; }
        public required string Message { get; init; }
        public int AttemptCount { get; init; }
        public DateTime? NextEligibleUtc { get; init; }
    }

    public interface IBillingReconciliationService
    {
        /// <summary>
        /// Pase completo de reconciliación: repara lo determinístico, alerta lo ambiguo,
        /// limpia lo abandonado. Nunca modifica datos ambiguos. Todo queda auditado.
        /// </summary>
        Task<BillingReconciliationReport> RunAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Ejecuta SOLO la fase de reintento de cancelación del suscriptor viejo (cambios de plan).
        /// Pensado para un worker de alta frecuencia: el riesgo de doble cobro no debe esperar 24h.
        /// Aislado por tenant y con backoff POR INTENT para no golpear a TiloPay en loop.
        /// </summary>
        Task<BillingReconciliationReport> RunOldSubscriberCancellationRetryAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Ejecuta SOLO el cierre local de cancelaciones vencidas (CancelAtPeriodEnd cuyo período ya
        /// terminó → Estado Cancelada). Local y barato (cero HTTP a TiloPay): pensado para un worker
        /// liviano de alta frecuencia + arranque, para no depender del pase diario de 24 h.
        /// </summary>
        Task<BillingReconciliationReport> RunLifecycleFinalizationAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Reintento forzado por soporte (SuperAdmin) de un intent concreto. Misma lógica que el
        /// worker e igual de idempotente; solo ignora el backoff y reinicia el presupuesto, nunca
        /// las guardas de elegibilidad. Sirve para destrabar un caso sin tocar la BD ni TiloPay a mano.
        /// </summary>
        Task<PlanChangeCancellationRetryOutcome> ForceOldSubscriberCancellationRetryAsync(
            Guid intentId,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Red de seguridad del módulo Billing. El flujo normal es 100% webhook-driven; este
    /// servicio cubre los huecos que dejan webhooks perdidos, reinicios y estados parciales:
    ///
    /// 1. Pago recurrente CONFIRMADO cuya suscripción no quedó activa => reparación automática
    ///    (el dinero ya está cobrado; activar es determinístico) o alerta si no es seguro.
    /// 2. Suscripción/add-on Activa con FechaProximoCobroUtc vencida => ALERTA únicamente
    ///    (no sabemos si TiloPay cobró; extender sin evidencia sería regalar servicio,
    ///    suspender sin evidencia sería castigar a un cliente que pagó).
    /// 3. Pendiente sin webhook por más de N días => Expirado (limpieza segura: la correlación
    ///    de webhooks solo mira 48h hacia atrás, ese intento ya no puede activarse solo).
    /// 4. ManualReview viejo => alerta; NUNCA se toca (puede haber dinero cobrado detrás).
    /// 5. EventoPago atascado en Recibido/Error => alerta (procesamiento murió a mitad).
    /// </summary>
    public sealed class BillingReconciliationService : IBillingReconciliationService
    {
        private readonly ApplicationDbContext _db;
        private readonly SuscripcionService _suscripcionService;
        private readonly ITenantExecutionContextAccessor _tenantExecutionContextAccessor;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly TilopayRepeatOptions _repeatOptions;
        private readonly BillingReconciliationOptions _options;
        private readonly ISubscriberResolutionService? _subscriberResolutionService;
        private readonly IProviderSubscriptionManager? _providerSubscriptionManager;
        private readonly IAddonSubscriptionManager? _addonSubscriptionManager;
        private readonly IAddonProviderAuditService? _addonProviderAudit;
        private readonly IPlanChangeLateApplicationService? _planChangeLateApplicationService;
        private readonly IProviderExpirySyncService? _providerExpirySyncService;
        // Opcionales (patrón del módulo): DI los inyecta en producción; en tests con el ctor mínimo
        // quedan null y las fases de ciclo de vida que dependen de HTTP/cache simplemente se saltan.
        private readonly ITilopayRepeatAdminService? _adminService;
        private readonly ITenantCommercialAccessCache? _accessCache;
        private readonly OpcionesTilopayRepeatAdmin _adminOptions;
        private readonly int _adminMaxAttempts;
        private readonly ILogger<BillingReconciliationService> _logger;

        public BillingReconciliationService(
            ApplicationDbContext db,
            SuscripcionService suscripcionService,
            ITenantExecutionContextAccessor tenantExecutionContextAccessor,
            IBusinessDateTimeProvider clock,
            IOptions<TilopayRepeatOptions> repeatOptions,
            IOptions<BillingReconciliationOptions> options,
            ILogger<BillingReconciliationService> logger,
            ISubscriberResolutionService? subscriberResolutionService = null,
            IOptions<OpcionesTilopayRepeatAdmin>? adminOptions = null,
            IProviderSubscriptionManager? providerSubscriptionManager = null,
            IPlanChangeLateApplicationService? planChangeLateApplicationService = null,
            IProviderExpirySyncService? providerExpirySyncService = null,
            ITilopayRepeatAdminService? adminService = null,
            ITenantCommercialAccessCache? accessCache = null,
            IAddonSubscriptionManager? addonSubscriptionManager = null,
            IAddonProviderAuditService? addonProviderAudit = null)
        {
            _db = db;
            _suscripcionService = suscripcionService;
            _tenantExecutionContextAccessor = tenantExecutionContextAccessor;
            _clock = clock;
            _repeatOptions = repeatOptions.Value;
            _options = options.Value;
            _subscriberResolutionService = subscriberResolutionService;
            _providerSubscriptionManager = providerSubscriptionManager;
            _addonSubscriptionManager = addonSubscriptionManager;
            _addonProviderAudit = addonProviderAudit;
            _planChangeLateApplicationService = planChangeLateApplicationService;
            _providerExpirySyncService = providerExpirySyncService;
            _adminService = adminService;
            _accessCache = accessCache;
            _adminOptions = adminOptions?.Value ?? new OpcionesTilopayRepeatAdmin();
            _adminMaxAttempts = _adminOptions.MaxReconciliationResolveAttempts;
            _logger = logger;
        }

        public async Task<BillingReconciliationReport> RunAsync(CancellationToken cancellationToken = default)
        {
            var nowUtc = GetUtcNow();
            var report = new BillingReconciliationReport { StartedUtc = nowUtc };

            // Cada fase se ejecuta AISLADA: un fallo en una (p.ej. expiración de stale de un tenant)
            // no impide que corran las demás (en particular, el backfill de id_suscriptor). Cada
            // fase empieza con el ChangeTracker limpio para no arrastrar entidades de otro tenant.
            await RunPhaseAsync("OrphanConfirmedPayments", () => ReconcileOrphanConfirmedPaymentsAsync(report, nowUtc, cancellationToken), cancellationToken);
            // ANTES de alertar renovaciones vencidas: sincronizar el expire real del proveedor, para
            // que la alerta use la fecha efectiva y no dispare un falso positivo cuando TiloPay cobra
            // más tarde de lo que calculamos localmente.
            await RunPhaseAsync("SyncProviderExpiry", () => SyncProviderExpiryAsync(report, cancellationToken), cancellationToken);
            // Ciclo de vida: cerrar localmente los períodos ya pagados de suscripciones con
            // CancelAtPeriodEnd (sin HTTP: el proveedor ya quedó dado de baja al pedir la cancelación),
            // y detectar drift entre el estado local y el del proveedor (con HTTP, fuera de tx).
            await RunPhaseAsync("FinalizeCancelAtPeriodEnd", () => FinalizeCancelAtPeriodEndAsync(report, nowUtc, cancellationToken), cancellationToken);
            await RunPhaseAsync("SyncProviderLifecycleStatus", () => SyncProviderLifecycleStatusAsync(report, nowUtc, cancellationToken), cancellationToken);
            // Sanar recuperaciones falsas: base en gracia/morosa cuyo proveedor ya está Active y
            // renovado (el webhook success quedó SinRelacion). Cierra incidente y reactiva.
            await RunPhaseAsync("HealRecoveredBaseSubscriptions", () => HealRecoveredBaseSubscriptionsAsync(report, nowUtc, cancellationToken), cancellationToken);
            // Trazabilidad financiera: un repeat_payment_success que quedó SinRelacion (url_renew sin
            // pending) se reconcilia contra la suscripción ya renovada por el proveedor. Corre DESPUÉS
            // de sanar para cerrar en el mismo pase el evento de una base recién reactivada, y también
            // atrapa las bases ya sanas de pases anteriores (el evento huérfano no depende de la gracia).
            await RunPhaseAsync("ReconcileOrphanedRenewalSuccessEvents", () => ReconcileOrphanedRenewalSuccessEventsAsync(report, nowUtc, cancellationToken), cancellationToken);
            await RunPhaseAsync("OverdueRenewals", () => AlertOverdueRenewalsAsync(report, nowUtc, cancellationToken), cancellationToken);
            await RunPhaseAsync("ExpireStalePendings", () => ExpireStalePendingAttemptsAsync(report, nowUtc, cancellationToken), cancellationToken);
            await RunPhaseAsync("StaleManualReviews", () => AlertStaleManualReviewsAsync(report, nowUtc, cancellationToken), cancellationToken);
            await RunPhaseAsync("StuckEvents", () => AlertStuckEventsAsync(report, nowUtc, cancellationToken), cancellationToken);
            await RunPhaseAsync("SubscriberBackfill", () => BackfillMissingSubscriberIdsAsync(report, nowUtc, cancellationToken), cancellationToken);
            // Antes de reparar: terminar de APLICAR los cambios cuyo pago ya está confirmado y cuyo
            // suscriptor nuevo se conoció tarde. Si no, el intent sigue Pending y las fases de abajo
            // (que solo miran Applied) no lo ven nunca.
            await RunPhaseAsync("ApplyLatePlanChanges", () => ApplyPendingPlanChangesWithConfirmedPaymentAsync(report, cancellationToken), cancellationToken);
            // Después de aplicar lo pagado: lo que sigue Pending y NO tiene dinero detrás es un
            // checkout abandonado. Este orden evita cualquier carrera entre aplicar y expirar.
            await RunPhaseAsync("ExpireAbandonedPlanChangeCheckouts", () => ExpireAbandonedPlanChangeCheckoutsAsync(report, nowUtc, cancellationToken), cancellationToken);
            // La reparación va ANTES del reintento: rellena NewProviderSubscriptionId y corrige la
            // suscripción, para que la cancelación del viejo trabaje sobre datos ya consistentes.
            await RunPhaseAsync("RepairInconsistentPlanChanges", () => RepairInconsistentPlanChangesAsync(report, cancellationToken), cancellationToken);
            await RunPhaseAsync("RetryOldSubscriberCancellations", () => RetryPendingPlanChangeCancellationsAsync(report, cancellationToken), cancellationToken);
            // Add-ons: reintentar la baja pendiente de suscriptores (Strategy B / cascada / manual) y
            // alertar add-ons activos sin plan base. Independiente del plan base; nunca lo toca.
            await RunPhaseAsync("RetryPendingAddonCancellations", () => RetryPendingAddonCancellationsAsync(report, cancellationToken), cancellationToken);
            await RunPhaseAsync("AlertAddonsWithoutActiveBase", () => AlertAddonsWithoutActiveBaseAsync(report, cancellationToken), cancellationToken);
            // Va AL FINAL: primero se intentan las bajas pendientes, y recién después se le pregunta
            // a TiloPay cuántos add-ons puede cobrar realmente. Es lo único que detecta el doble
            // suscriptor del proveedor cuando el estado local quedó impecable.
            await RunPhaseAsync("AuditAddonProviderState", () => AuditAddonProviderStateAsync(report, cancellationToken), cancellationToken);

            report.FinishedUtc = GetUtcNow();

            // El resumen del pase es un PlatformAuditLog (cross-tenant, NO ITenantEntity): se guarda
            // con el ChangeTracker ya limpio para que no arrastre entidades tenant-scoped colgadas.
            DetachAllTracked();

            // Cierre del pase: SIEMPRE se registra (sin cooldown) para que el health check
            // pueda mostrar "última reconciliación" y su resumen sin consultar logs de texto.
            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.BillingReconciliationCompleted,
                EntityType = PlatformAuditEntityTypes.Billing,
                Reason = report.HasFindings
                    ? "Pase de reconciliación con hallazgos. Ver AfterJson."
                    : "Pase de reconciliación sin hallazgos.",
                AfterJson = JsonSerializer.Serialize(report),
                CreatedAtUtc = report.FinishedUtc
            });

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Reconciliación Billing completada. DurationMs {DurationMs}. Reparados {Repaired}. AlertasHuérfanos {OrphanAlerts}. RenovacionesVencidas {Overdue}. AddonsVencidos {OverdueAddons}. PendientesExpirados {StaleExpired}. ManualReviewViejos {StaleManual}. EventosAtascados {StuckEvents}. AlertasSuprimidas {Suppressed}.",
                report.DurationMs,
                report.OrphanPaymentsRepaired,
                report.OrphanPaymentsAlerted,
                report.OverdueRenewalsAlerted,
                report.OverdueAddonsAlerted,
                report.StalePendingsExpired,
                report.StaleManualReviewsAlerted,
                report.StuckEventsAlerted,
                report.AlertsSuppressedByCooldown);

            return report;
        }

        public async Task<BillingReconciliationReport> RunOldSubscriberCancellationRetryAsync(
            CancellationToken cancellationToken = default)
        {
            var report = new BillingReconciliationReport { StartedUtc = GetUtcNow() };
            // Sincronizar el expire real del proveedor también en el pase rápido: si TiloPay
            // extendió el vencimiento, no hay que esperar al pase diario para dejar de considerar
            // la suscripción "por vencer" y evitar una morosidad falsa.
            await RunPhaseAsync(
                "SyncProviderExpiry",
                () => SyncProviderExpiryAsync(report, cancellationToken),
                cancellationToken);
            // Cierre local del período de las cancelaciones programadas: barato (sin HTTP) y no debe
            // esperar al pase diario para que el acceso termine cuando el período pagado se acaba.
            await RunPhaseAsync(
                "FinalizeCancelAtPeriodEnd",
                () => FinalizeCancelAtPeriodEndAsync(report, GetUtcNow(), cancellationToken),
                cancellationToken);
            // Aplicar los cambios pagados que quedaron sin aplicar por resolución tardía del
            // suscriptor. Va en el worker RÁPIDO a propósito: mientras el cambio no se aplica hay
            // dos suscriptores activos en TiloPay, y eso no puede esperar al pase diario.
            await RunPhaseAsync(
                "ApplyLatePlanChanges",
                () => ApplyPendingPlanChangesWithConfirmedPaymentAsync(report, cancellationToken),
                cancellationToken);
            // Limpieza local y barata (sin TiloPay): el cupo de "un cambio abierto por tenant" no
            // puede quedar tomado 7 días por un checkout que nadie pagó.
            await RunPhaseAsync(
                "ExpireAbandonedPlanChangeCheckouts",
                () => ExpireAbandonedPlanChangeCheckoutsAsync(report, GetUtcNow(), cancellationToken),
                cancellationToken);
            // Reparar (backfill de NewProviderSubscriptionId desde el pago) y luego reintentar:
            // el worker rápido no depende de que alguien rellene los IDs a mano.
            await RunPhaseAsync(
                "RepairInconsistentPlanChanges",
                () => RepairInconsistentPlanChangesAsync(report, cancellationToken),
                cancellationToken);
            await RunPhaseAsync(
                "RetryOldSubscriberCancellations",
                () => RetryPendingPlanChangeCancellationsAsync(report, cancellationToken),
                cancellationToken);
            // El doble cobro de un add-on tampoco puede esperar al pase diario: reintentar aquí.
            await RunPhaseAsync(
                "RetryPendingAddonCancellations",
                () => RetryPendingAddonCancellationsAsync(report, cancellationToken),
                cancellationToken);
            // Sanar recuperaciones falsas también en el pase rápido: un cliente en falso "gracia"
            // con el proveedor ya renovado no debe esperar 24h para verse Activa.
            await RunPhaseAsync(
                "HealRecoveredBaseSubscriptions",
                () => HealRecoveredBaseSubscriptionsAsync(report, GetUtcNow(), cancellationToken),
                cancellationToken);
            // Cerrar la traza financiera del success huérfano también en el pase rápido: si el cliente
            // regularizó por url_renew, el evento no debe quedar SinRelacion hasta el pase diario.
            await RunPhaseAsync(
                "ReconcileOrphanedRenewalSuccessEvents",
                () => ReconcileOrphanedRenewalSuccessEventsAsync(report, GetUtcNow(), cancellationToken),
                cancellationToken);
            report.FinishedUtc = GetUtcNow();
            return report;
        }

        public async Task<BillingReconciliationReport> RunLifecycleFinalizationAsync(CancellationToken cancellationToken = default)
        {
            // Solo el cierre local de cancelaciones vencidas. Sin HTTP: si el proveedor ya está
            // Delete, finalizar el Estado local no requiere volver a llamar a TiloPay. Idempotente
            // (re-correrlo no encuentra nada por finalizar).
            var report = new BillingReconciliationReport { StartedUtc = GetUtcNow() };
            await RunPhaseAsync(
                "FinalizeCancelAtPeriodEnd",
                () => FinalizeCancelAtPeriodEndAsync(report, GetUtcNow(), cancellationToken),
                cancellationToken);
            report.FinishedUtc = GetUtcNow();
            return report;
        }

        // ── 11. Sincronización del expire real del proveedor ─────────────────────────

        /// <summary>
        /// Lee el expire de cada suscriptor Active en TiloPay y concilia la vigencia local: extiende
        /// si el proveedor cobra más tarde (evita morosidad falsa), alerta si cobra más temprano
        /// (nunca acorta). Delega en <see cref="IProviderExpirySyncService"/>. No toca cancelación,
        /// late repair, retry ni target Delete: solo fechas.
        /// </summary>
        private async Task SyncProviderExpiryAsync(
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            if (_providerExpirySyncService is null || !_providerExpirySyncService.IsEnabled)
            {
                return;
            }

            await _providerExpirySyncService.SyncActiveSubscriptionsAsync(report, cancellationToken);
        }

        // ── 12. Ciclo de vida: cierre local del período de cancelaciones programadas ──

        /// <summary>
        /// Cierra localmente el acceso de las suscripciones con CancelAtPeriodEnd cuando su período
        /// EFECTIVO ya pagado terminó. NO llama a TiloPay: el suscriptor ya quedó dado de baja al
        /// solicitar la cancelación (ProviderCancelledAtUtc). Nunca corta antes de la fecha efectiva
        /// (máximo entre local, expire del proveedor y CancellationEffectiveAtUtc): no quita servicio
        /// ya pagado. Barato y sin HTTP, así que corre también en el worker rápido.
        /// </summary>
        private async Task FinalizeCancelAtPeriodEndAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            if (!_options.CancelAtPeriodEndFinalizationEnabled)
            {
                return;
            }

            var candidates = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s =>
                    s.CancelAtPeriodEnd &&
                    (s.Estado == EstadoSuscripcion.Activa || s.Estado == EstadoSuscripcion.Morosa))
                .Select(s => new
                {
                    s.Id,
                    s.TenantId,
                    s.FechaFin,
                    s.ProviderExpiresAtUtc,
                    s.CancellationEffectiveAtUtc
                })
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (candidate.TenantId == Guid.Empty)
                {
                    continue;
                }

                // Fin EFECTIVO: máximo entre local y el expire del proveedor, y nunca antes de la
                // fecha efectiva calculada al pedir la cancelación.
                var effectiveEndUtc = SubscriptionEffectiveDates.GetEffectiveEndUtc(
                    candidate.FechaFin,
                    candidate.ProviderExpiresAtUtc);
                var cutoffUtc = LatestOf(effectiveEndUtc, candidate.CancellationEffectiveAtUtc);

                if (cutoffUtc is null || cutoffUtc.Value > nowUtc)
                {
                    continue; // Aún dentro del período pagado: NO se corta el acceso.
                }

                try
                {
                    _db.ChangeTracker.Clear();
                    await FinalizeOneCancelAtPeriodEndAsync(candidate.Id, candidate.TenantId, cutoffUtc.Value, report, cancellationToken);
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
                        "No se pudo finalizar la cancelación de período para la suscripción {SubscriptionId} del tenant {TenantId}; se continúa.",
                        candidate.Id,
                        candidate.TenantId);
                }
            }
        }

        private async Task FinalizeOneCancelAtPeriodEndAsync(
            Guid subscriptionId,
            Guid tenantId,
            DateTime cutoffUtc,
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(tenantId);

            var subscription = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Id == subscriptionId && s.TenantId == tenantId, cancellationToken);

            // Re-verificación bajo el registro tracked: pudo reactivarse o cambiar entre la lectura y ahora.
            if (subscription is null ||
                !subscription.CancelAtPeriodEnd ||
                subscription.Estado is not (EstadoSuscripcion.Activa or EstadoSuscripcion.Morosa))
            {
                return;
            }

            var nowUtc = GetUtcNow();
            var estadoAnterior = subscription.Estado;

            subscription.Estado = EstadoSuscripcion.Cancelada;
            subscription.FechaCancelacionUtc ??= cutoffUtc;
            subscription.CancellationEffectiveAtUtc ??= cutoffUtc;
            subscription.FechaUltimaActualizacionUtc = nowUtc;
            subscription.MotivoEstado = "Cancelación de renovación: período pagado finalizado, acceso cerrado.";

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.SubscriptionCancellationAtPeriodEndFinalized,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = subscription.Id.ToString(),
                TenantId = tenantId,
                BeforeJson = JsonSerializer.Serialize(new { estado = estadoAnterior.ToString() }),
                AfterJson = JsonSerializer.Serialize(new { estado = EstadoSuscripcion.Cancelada.ToString() }),
                Reason = Trim(
                    $"Período pagado terminó ({cutoffUtc:yyyy-MM-dd HH:mm} UTC): suscripción con CancelAtPeriodEnd pasada a Cancelada localmente. Sin llamada a TiloPay (ya dado de baja).",
                    500),
                CreatedAtUtc = nowUtc
            });

            await _db.SaveChangesAsync(cancellationToken);
            _db.ChangeTracker.Clear();

            _accessCache?.Invalidate(tenantId);
            report.CancelAtPeriodEndFinalized++;

            _logger.LogInformation(
                "Cancelación de período finalizada localmente. TenantId {TenantId}. SubscriptionId {SubscriptionId}. Cutoff {Cutoff}.",
                tenantId,
                subscription.Id,
                cutoffUtc);
        }

        // ── 13. Ciclo de vida: drift entre estado local y estado del proveedor ────────

        /// <summary>
        /// Detecta desajustes entre lo local y TiloPay para suscripciones con acceso vigente
        /// (Activa/Morosa): (1) CRÍTICO — CancelAtPeriodEnd local pero suscriptor ACTIVO en el
        /// proveedor (podría seguir cobrando); (2) suscriptor INACTIVO en el proveedor sin
        /// cancelación pedida; (3) suscriptor PAUSADO en el proveedor que lo local no refleja.
        /// Solo alerta (idempotente por cooldown) y guarda el status observado; NUNCA suspende ni
        /// cancela por su cuenta. HTTP por plan (getSuscriptorRepeat), siempre FUERA de transacción.
        /// </summary>
        private async Task SyncProviderLifecycleStatusAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            if (_adminService is null ||
                !_adminService.IsEnabled ||
                !_options.LifecycleProviderStatusSyncEnabled)
            {
                return;
            }

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
                    s.CodigoPlan,
                    s.CancelAtPeriodEnd,
                    s.ProviderPausedAtUtc,
                    s.PaymentRecoveryStatus
                })
                .ToListAsync(cancellationToken);

            if (subscriptions.Count == 0)
            {
                return;
            }

            // Una llamada por PLAN (devuelve todos sus suscriptores): evita el N+1 contra el API.
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
                        "No se pudo consultar getSuscriptorRepeat para el drift de ciclo de vida. PlanId {PlanId}.",
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
                    var state = match is null
                        ? ProviderSubscriberState.Inactive
                        : ProviderSubscriberStatusRules.Classify(match.Status);

                    string? mismatch = null;
                    if (subscription.CancelAtPeriodEnd && state == ProviderSubscriberState.Active)
                    {
                        mismatch = "CRÍTICO: la suscripción pidió cancelar la renovación pero el suscriptor sigue ACTIVO en TiloPay: podría seguir cobrando. Cancelar en el proveedor.";
                    }
                    else if (!subscription.CancelAtPeriodEnd && state == ProviderSubscriberState.Inactive)
                    {
                        mismatch = "El suscriptor está INACTIVO/eliminado en TiloPay pero la suscripción local sigue activa sin cancelación pedida. Revisar (cancelación externa o baja no registrada).";
                    }
                    else if (state == ProviderSubscriberState.Paused && subscription.ProviderPausedAtUtc is null)
                    {
                        // En recuperación de pago la pausa ("Pause By Commerce") suele ser consecuencia de
                        // los reintentos rechazados: NO es una pausa de soporte ni cancela/cierra la gracia.
                        // Solo se informa; la gracia local y el pase de gracia siguen mandando.
                        mismatch = subscription.PaymentRecoveryStatus is null
                            ? "El suscriptor está PAUSADO en TiloPay pero la suscripción local no lo refleja. Revisar el estado."
                            : $"El suscriptor quedó PAUSADO en TiloPay durante la recuperación de pago ({subscription.PaymentRecoveryStatus}): TiloPay dejó de reintentar. La gracia local sigue vigente; el cliente debe regularizar el pago.";
                    }

                    if (mismatch is null)
                    {
                        continue;
                    }

                    try
                    {
                        _db.ChangeTracker.Clear();
                        if (await TryAlertProviderStatusMismatchAsync(
                                subscription.Id,
                                subscription.TenantId,
                                match?.Status,
                                subscription.CodigoPlan,
                                subscription.ProviderSubscriptionId,
                                mismatch,
                                nowUtc,
                                cancellationToken))
                        {
                            report.ProviderStatusMismatchesAlerted++;
                        }
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
                            "El drift de ciclo de vida falló para la suscripción {SubscriptionId} del tenant {TenantId}; se continúa.",
                            subscription.Id,
                            subscription.TenantId);
                    }
                }
            }
        }

        private async Task<bool> TryAlertProviderStatusMismatchAsync(
            Guid subscriptionId,
            Guid tenantId,
            string? providerStatusRaw,
            string? planCode,
            string? subscriberId,
            string reason,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(tenantId);

            var entityId = subscriptionId.ToString();
            var cooldownCutoffUtc = nowUtc.AddHours(-Math.Max(1, _options.AlertCooldownHours));

            var alreadyAlerted = await _db.PlatformAuditLogs.AnyAsync(
                log =>
                    log.Action == PlatformAuditActions.SubscriptionProviderStatusMismatch &&
                    log.EntityId == entityId &&
                    log.CreatedAtUtc >= cooldownCutoffUtc,
                cancellationToken);

            // El status observado se guarda SIEMPRE (telemetría), aunque la alerta esté en cooldown.
            var subscription = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Id == subscriptionId && s.TenantId == tenantId, cancellationToken);
            if (subscription is not null)
            {
                subscription.ProviderStatusRaw = Trim(ProviderSubscriberStatusRules.Sanitize(providerStatusRaw), 40);
                subscription.ProviderStatusLastSyncedUtc = nowUtc;
            }

            if (!alreadyAlerted)
            {
                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = PlatformAuditActions.SubscriptionProviderStatusMismatch,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    EntityId = entityId,
                    TenantId = tenantId,
                    Reason = Trim(
                        $"{reason} SuscriptorSuffix {SensitiveDataMasker.MaskReference(subscriberId)}. Plan {planCode}. Estado provider {ProviderSubscriberStatusRules.Sanitize(providerStatusRaw)}.",
                        500),
                    CreatedAtUtc = nowUtc
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            _db.ChangeTracker.Clear();

            return !alreadyAlerted;
        }

        /// <summary>Fecha más tardía de las dos (para no cortar acceso antes de tiempo). Null si ambas nulas.</summary>
        private static DateTime? LatestOf(DateTime? a, DateTime? b)
        {
            if (a is null)
            {
                return b;
            }

            if (b is null)
            {
                return a;
            }

            return a.Value >= b.Value ? a : b;
        }

        // ── 10. Checkouts de cambio de plan abandonados (limpieza sin dinero) ─────────

        /// <summary>
        /// El cliente abrió un checkout de cambio de plan y nunca pagó. No hay riesgo de dinero
        /// (sin transacción, sin suscriptor, sin confirmación), pero el intento Pending consume el
        /// cupo de "un cambio abierto por tenant" y aparece en el health como si fuera un hallazgo.
        ///
        /// Se expira el pago y el intento juntos. La expiración genérica de pendientes
        /// (<see cref="ExpireStalePendingAttemptsAsync"/>, 7 días) NO sirve para este caso: expira
        /// el pago pero deja el intent Pending para siempre, que es exactamente lo que ensuciaba
        /// el contador.
        ///
        /// NUNCA toca Suscripciones, NUNCA llama a TiloPay, NUNCA cancela nada en el proveedor y
        /// NUNCA crea pagos: es limpieza puramente local de algo que no llegó a existir.
        /// </summary>
        private async Task ExpireAbandonedPlanChangeCheckoutsAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var expirationHours = Math.Max(1, _options.PlanChangePendingCheckoutExpirationHours);
            var cutoffUtc = nowUtc.AddHours(-expirationHours);

            // Prefiltro barato en SQL por antigüedad; las señales de dinero se evalúan en memoria
            // con la regla compartida (una sola definición de "hay dinero detrás").
            var candidates = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(intent =>
                    intent.Estado == PlanChangeIntentState.Pending &&
                    intent.CreatedAtUtc <= cutoffUtc)
                .Select(intent => new { intent.Id, intent.TenantId })
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (candidate.TenantId == Guid.Empty)
                {
                    continue;
                }

                DetachAllTracked();

                try
                {
                    await ExpireAbandonedCheckoutForTenantAsync(
                        candidate.TenantId,
                        candidate.Id,
                        expirationHours,
                        report,
                        nowUtc,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DetachAllTracked();
                    _logger.LogError(
                        ex,
                        "Expiración de checkout de cambio abandonado falló. TenantId {TenantId}. IntentId {IntentId}. Se continúa.",
                        candidate.TenantId,
                        candidate.Id);
                }
            }
        }

        private async Task ExpireAbandonedCheckoutForTenantAsync(
            Guid tenantId,
            Guid intentId,
            int expirationHours,
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(tenantId);

            var intent = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    i => i.Id == intentId &&
                         i.TenantId == tenantId &&
                         i.Estado == PlanChangeIntentState.Pending,
                    cancellationToken);

            if (intent is null)
            {
                return;
            }

            PagoSuscripcion? payment = null;
            var hasProviderEvent = false;

            if (intent.PagoSuscripcionId is { } paymentId)
            {
                payment = await _db.PagosSuscripcion
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.Id == paymentId && p.TenantId == tenantId, cancellationToken);

                // Un webhook asociado significa que TiloPay tocó este intento: no se descarta.
                hasProviderEvent = await _db.EventosPago
                    .IgnoreQueryFilters()
                    .AnyAsync(e => e.PagoSuscripcionId == paymentId, cancellationToken);
            }

            if (!PlanChangeCheckoutAbandonmentRules.IsAbandonedCheckout(
                    payment,
                    intent.CreatedAtUtc,
                    nowUtc,
                    expirationHours,
                    hasProviderEvent))
            {
                return;
            }

            // Solo el pago del propio checkout, y solo si sigue Pendiente. Un pago ya cerrado por
            // otra vía se deja como está: su historia ya la contó quien lo cerró.
            if (payment is not null && payment.Estado == EstadoPagoProveedor.Pendiente)
            {
                payment.Estado = EstadoPagoProveedor.Expirado;
                payment.ProviderResultCode = "EXPIRED_PLAN_CHANGE_CHECKOUT";
                payment.ProviderResultMessage = Trim(
                    $"Checkout de cambio de plan hacia {intent.ToPlanCode} abandonado: {expirationHours}h sin pago, sin transacción y sin suscriptor.",
                    300);
                payment.FechaActualizacionUtc = nowUtc;
            }

            intent.Estado = PlanChangeIntentState.Expired;
            intent.UpdatedAtUtc = nowUtc;
            intent.Notes = Trim(
                $"Checkout abandonado: {expirationHours}h sin pagar. No hubo cobro (sin transacción, sin suscriptor, sin confirmación). La suscripción {intent.FromPlanCode} sigue intacta.",
                300);

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.PlanChangePendingCheckoutExpired,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = intent.Id.ToString(),
                TenantId = tenantId,
                Reason = Trim(
                    $"Cambio {intent.FromPlanCode} → {intent.ToPlanCode} expirado por abandono ({expirationHours}h sin pagar). " +
                    $"Sin riesgo de dinero: el pago quedó Expirado y ni la suscripción ni TiloPay se tocaron. " +
                    $"Creado {intent.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC.",
                    500),
                CreatedAtUtc = nowUtc
            });

            await _db.SaveChangesAsync(cancellationToken);
            DetachAllTracked();

            report.PlanChangeCheckoutsExpired++;

            _logger.LogInformation(
                "Checkout de cambio de plan abandonado expirado. TenantId {TenantId}. IntentId {IntentId}. {From} → {To}. Creado {CreatedAtUtc}.",
                tenantId,
                intent.Id,
                intent.FromPlanCode,
                intent.ToPlanCode,
                intent.CreatedAtUtc);
        }

        // ── 9. Cambios pagados sin aplicar por resolución tardía del suscriptor ───────

        /// <summary>
        /// El cliente PAGÓ, el pago está Confirmado y ya sabemos el id_suscriptor nuevo, pero el
        /// cambio quedó sin aplicar: cuando corrió la transacción de aprobación, el suscriptor
        /// todavía no estaba resuelto (TiloPay no lo manda en el webhook) y el guard anti
        /// doble-cobro se negó a aplicar. Correcto entonces; pendiente ahora.
        ///
        /// Mientras esto no se aplica el cliente paga un plan y ve otro, y hay DOS suscriptores
        /// activos en el proveedor. Por eso la fase corre también en el worker rápido.
        ///
        /// Distinto de <see cref="RepairInconsistentPlanChangesAsync"/>: aquella repara intents ya
        /// APLICADOS con datos torcidos; esta aplica intents que siguen PENDING.
        /// Nunca crea pagos ni checkouts: solo termina lo que el webhook dejó a medias.
        /// </summary>
        private async Task ApplyPendingPlanChangesWithConfirmedPaymentAsync(
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            if (_planChangeLateApplicationService is null)
            {
                return;
            }

            // Escaneo cross-tenant SOLO lectura. El servicio abre el scope del tenant al aplicar.
            var candidates = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(intent =>
                    intent.Estado == PlanChangeIntentState.Pending &&
                    intent.PagoSuscripcionId != null &&
                    intent.NewProviderSubscriptionId == null)
                .Join(
                    _db.PagosSuscripcion.IgnoreQueryFilters().AsNoTracking()
                        .Where(payment =>
                            payment.Estado == EstadoPagoProveedor.Confirmado &&
                            payment.ProviderSubscriberId != null),
                    intent => intent.PagoSuscripcionId,
                    payment => payment.Id,
                    (intent, payment) => new { IntentId = intent.Id, PaymentId = payment.Id, intent.TenantId })
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (candidate.TenantId == Guid.Empty)
                {
                    continue;
                }

                DetachAllTracked();

                try
                {
                    var result = await _planChangeLateApplicationService
                        .ApplyPendingPlanChangeAfterSubscriberResolvedAsync(
                            candidate.PaymentId,
                            "reconciliation",
                            cancellationToken);

                    switch (result.Status)
                    {
                        case LatePlanChangeApplicationStatus.Applied:
                            report.LatePlanChangesApplied++;
                            break;
                        case LatePlanChangeApplicationStatus.ManualReview:
                            report.LatePlanChangesManualReview++;
                            break;
                        case LatePlanChangeApplicationStatus.LeftPendingNoActiveSubscriber:
                        case LatePlanChangeApplicationStatus.ProviderUnavailable:
                            report.LatePlanChangesLeftPending++;
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DetachAllTracked();
                    _logger.LogError(
                        ex,
                        "Aplicación tardía de cambio de plan falló. TenantId {TenantId}. IntentId {IntentId}. Se continúa.",
                        candidate.TenantId,
                        candidate.IntentId);
                }
            }
        }

        // ── 8. Reparación de cambios de plan en estado inconsistente ──────────────────

        /// <summary>
        /// Repara el estado exacto que dejó el bug de producción: el pago nuevo quedó confirmado con
        /// su ProviderSubscriberId, pero el intent quedó sin NewProviderSubscriptionId y la suscripción
        /// quedó en el plan DESTINO apuntando todavía al suscriptor VIEJO y con el ciclo encadenado al
        /// vencimiento anterior. Rellena el subscriber nuevo, corrige la suscripción y recalcula el
        /// ciclo desde la confirmación del pago. NUNCA crea pagos ni checkouts. Aislado por tenant.
        /// </summary>
        private async Task RepairInconsistentPlanChangesAsync(
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            var tenantIds = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(intent =>
                    intent.Estado == PlanChangeIntentState.Applied &&
                    intent.PagoSuscripcionId != null &&
                    (intent.NewProviderSubscriptionId == null ||
                     intent.OldProviderCancellation == ProviderCancellationState.PendingManualCancellation))
                .Select(intent => intent.TenantId)
                .Distinct()
                .ToListAsync(cancellationToken);

            foreach (var tenantId in tenantIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (tenantId == Guid.Empty)
                {
                    continue;
                }

                try
                {
                    await RepairPlanChangeForTenantAsync(tenantId, report, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DetachAllTracked();
                    _logger.LogError(
                        ex,
                        "Reparación de cambio de plan falló para el tenant {TenantId}; se continúa.",
                        tenantId);
                }
            }
        }

        private async Task RepairPlanChangeForTenantAsync(
            Guid tenantId,
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(tenantId);

            var intent = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .Where(i =>
                    i.TenantId == tenantId &&
                    i.Estado == PlanChangeIntentState.Applied &&
                    i.PagoSuscripcionId != null)
                .OrderByDescending(i => i.AppliedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (intent?.PagoSuscripcionId is not { } paymentId)
            {
                return;
            }

            // El suscriptor nuevo REAL vive en el pago confirmado (lo dejó la resolución por
            // getSuscriptorRepeat). Es la fuente de verdad para reparar el intent y la suscripción.
            var payment = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    p => p.Id == paymentId &&
                         p.TenantId == tenantId &&
                         p.Estado == EstadoPagoProveedor.Confirmado,
                    cancellationToken);

            var newSubscriberId = payment?.ProviderSubscriberId;
            if (payment is null || string.IsNullOrWhiteSpace(newSubscriberId))
            {
                return; // Sin subscriber nuevo conocido no hay reparación segura posible.
            }

            var nowUtc = GetUtcNow();
            var repairs = new List<string>();

            if (string.IsNullOrWhiteSpace(intent.NewProviderSubscriptionId))
            {
                intent.NewProviderSubscriptionId = newSubscriberId;
                intent.UpdatedAtUtc = nowUtc;
                repairs.Add("NewProviderSubscriptionId rellenado desde el pago confirmado");
            }

            var subscription = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            // Solo reparamos si la suscripción YA está en el plan destino (el cambio se aplicó) pero
            // quedó con el suscriptor equivocado (el viejo) — la firma exacta del bug.
            if (subscription is not null &&
                subscription.TilopayRecurringPlanId == intent.ToTilopayRecurringPlanId &&
                !string.Equals(subscription.ProviderSubscriptionId, newSubscriberId, StringComparison.OrdinalIgnoreCase))
            {
                repairs.Add($"ProviderSubscriptionId {SensitiveDataMasker.MaskReference(subscription.ProviderSubscriptionId)} → {SensitiveDataMasker.MaskReference(newSubscriberId)}");
                subscription.ProviderSubscriptionId = newSubscriberId;
                subscription.ProviderTransactionId = payment.ProviderTransactionId ?? subscription.ProviderTransactionId;
                subscription.FechaUltimaActualizacionUtc = nowUtc;

                // Ciclo: el cambio de plan inicia ciclo NUEVO desde la confirmación del pago; si el
                // ciclo actual arranca DESPUÉS de esa confirmación, quedó encadenado al plan viejo.
                if (payment.FechaConfirmacionUtc is { } confirmedAtUtc && subscription.FechaInicio > confirmedAtUtc)
                {
                    var targetPlan = await _db.Planes
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(p => p.Id == intent.ToPlanId, cancellationToken);
                    var cycle = targetPlan?.BillingCycle ?? BillingCycle.Monthly;
                    var periodEndUtc = cycle == BillingCycle.Annual
                        ? confirmedAtUtc.AddYears(1)
                        : confirmedAtUtc.AddMonths(1);

                    repairs.Add($"ciclo recalculado desde la confirmación del pago ({confirmedAtUtc:yyyy-MM-dd}) en vez de encadenar el vencimiento anterior");
                    subscription.FechaInicio = confirmedAtUtc;
                    subscription.FechaFin = periodEndUtc;
                    subscription.FechaProximoCobroUtc = periodEndUtc;
                }
            }

            if (repairs.Count == 0)
            {
                DetachAllTracked();
                return;
            }

            // El estado cambió bajo los pies del reintento: los intentos anteriores se hicieron
            // contra datos rotos (sin suscriptor nuevo, apuntando al viejo) y no prueban nada.
            // Reiniciar el presupuesto garantiza un intento real INMEDIATO sobre datos ya sanos,
            // en vez de esperar al backoff que se ganó fallando por una razón ya corregida.
            if (intent.OldProviderCancellation == ProviderCancellationState.PendingManualCancellation)
            {
                repairs.Add($"presupuesto de reintentos reiniciado (llevaba {intent.OldCancellationAttemptCount} intento(s) reales)");
                ResetOldCancellationBudget(intent, nowUtc);
            }

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.PlanChangeInconsistentStateRepaired,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = intent.Id.ToString(),
                TenantId = tenantId,
                Reason = Trim($"Cambio a {intent.ToPlanCode} reparado: {string.Join("; ", repairs)}.", 500),
                CreatedAtUtc = nowUtc
            });

            await _db.SaveChangesAsync(cancellationToken);
            DetachAllTracked();

            report.PlanChangesRepaired++;

            _logger.LogWarning(
                "Cambio de plan inconsistente reparado. TenantId {TenantId}. IntentId {IntentId}. Reparaciones {Repairs}.",
                tenantId,
                intent.Id,
                repairs.Count);
        }

        // ── 7. Reintento de cancelación del suscriptor viejo en cambios de plan ───────

        /// <summary>
        /// Un cambio de plan aplicado cuya cancelación del suscriptor viejo quedó pendiente
        /// (falló en el momento del webhook) haría que TiloPay siga rebajando el plan anterior.
        /// Aquí se reintenta POR INTENT, con scope de tenant aislado e idempotente: si el viejo
        /// ya quedó cancelado, el intent deja de estar pendiente y no se reintenta.
        ///
        /// El ritmo lo marca el backoff guardado en el propio intent, NO un conteo de auditorías
        /// por tenant. Contar auditorías por tenant fue un bug de producción: mezclaba intents,
        /// y contaba como "intentos" los pases que ni siquiera llamaron a TiloPay, dejando un
        /// suscriptor viejo cobrando durante 24h sin un solo intento real.
        /// </summary>
        private async Task RetryPendingPlanChangeCancellationsAsync(
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            // Sin integración admin no hay TiloPay que llamar: no hay nada que auditar tampoco.
            // OJO: AutoCancelOldSubscriberOnUpgrade=false NO se filtra aquí a propósito — ese caso
            // debe entrar y registrarse como skip explícito (sin gastar presupuesto).
            if (_providerSubscriptionManager is null || !_providerSubscriptionManager.IsEnabled)
            {
                return;
            }

            // Escaneo cross-tenant SOLO lectura; cada intent se procesa bajo el scope de SU tenant.
            var candidates = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(intent =>
                    intent.Estado == PlanChangeIntentState.Applied &&
                    intent.OldProviderCancellation == ProviderCancellationState.PendingManualCancellation &&
                    intent.FromProviderSubscriptionId != null)
                .OrderBy(intent => intent.AppliedAtUtc)
                .Select(intent => new { intent.Id, intent.TenantId })
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (candidate.TenantId == Guid.Empty)
                {
                    continue;
                }

                // ChangeTracker limpio ANTES de cada intent: ninguna entidad de un tenant puede
                // quedar colgada y viajar al SaveChanges del siguiente (guard cross-tenant).
                DetachAllTracked();

                try
                {
                    await TryRetryIntentAsync(
                        candidate.TenantId,
                        candidate.Id,
                        ignoreBackoff: false,
                        report,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Aislar por intent: un tenant que falle no puede impedir que los demás
                    // intenten cancelar su suscriptor viejo (cada uno es un riesgo de doble cobro).
                    DetachAllTracked();
                    _logger.LogError(
                        ex,
                        "Reintento de cancelación de suscriptor viejo falló. TenantId {TenantId}. IntentId {IntentId}. Se continúa.",
                        candidate.TenantId,
                        candidate.Id);
                }
            }
        }

        public async Task<PlanChangeCancellationRetryOutcome> ForceOldSubscriberCancellationRetryAsync(
            Guid intentId,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default)
        {
            var target = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(intent => intent.Id == intentId)
                .Select(intent => new { intent.Id, intent.TenantId })
                .FirstOrDefaultAsync(cancellationToken);

            if (target is null || target.TenantId == Guid.Empty)
            {
                return new PlanChangeCancellationRetryOutcome
                {
                    Status = PlanChangeCancellationRetryStatus.NotPending,
                    Message = "El cambio de plan indicado no existe."
                };
            }

            using (var tenantScope = _tenantExecutionContextAccessor.BeginScope(target.TenantId))
            {
                // Soporte toma el control: se reinicia el presupuesto para que el reintento sea
                // inmediato y los siguientes pases automáticos tampoco arrastren el backoff viejo.
                var intent = await _db.PlanChangeIntents
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(i => i.Id == intentId && i.TenantId == target.TenantId, cancellationToken);

                if (intent is not null)
                {
                    var nowUtc = GetUtcNow();
                    ResetOldCancellationBudget(intent, nowUtc);

                    _db.PlatformAuditLogs.Add(new PlatformAuditLog
                    {
                        Id = Guid.NewGuid(),
                        ActorUserId = string.IsNullOrWhiteSpace(actorUserId) ? "system" : actorUserId,
                        ActorEmail = string.IsNullOrWhiteSpace(actorEmail) ? "system" : actorEmail,
                        Action = PlatformAuditActions.PlanChangeOldSubscriberCancellationForcedRetry,
                        EntityType = PlatformAuditEntityTypes.Subscription,
                        EntityId = intent.Id.ToString(),
                        TenantId = target.TenantId,
                        Reason = Trim(
                            $"Retry forzado desde plataforma para el cambio a {intent.ToPlanCode}. " +
                            $"Presupuesto de reintentos reiniciado (llevaba {intent.OldCancellationAttemptCount} intento(s) reales). " +
                            $"ViejoSuffix {SensitiveDataMasker.MaskReference(intent.FromProviderSubscriptionId)}. " +
                            $"NuevoSuffix {SensitiveDataMasker.MaskReference(intent.NewProviderSubscriptionId)}.",
                            500),
                        CreatedAtUtc = nowUtc
                    });

                    await _db.SaveChangesAsync(cancellationToken);
                    DetachAllTracked();
                }
            }

            return await TryRetryIntentAsync(
                target.TenantId,
                intentId,
                ignoreBackoff: true,
                report: null,
                cancellationToken);
        }

        /// <summary>
        /// Núcleo del reintento, compartido por el worker y por el retry forzado de soporte.
        /// Orden deliberado de las guardas: primero lo que NO debe gastar presupuesto (estado,
        /// AutoCancel, datos faltantes) y solo al final el backoff. Así, encender AutoCancel o
        /// reparar el estado siempre habilita un intento real inmediato.
        /// </summary>
        private async Task<PlanChangeCancellationRetryOutcome> TryRetryIntentAsync(
            Guid tenantId,
            Guid intentId,
            bool ignoreBackoff,
            BillingReconciliationReport? report,
            CancellationToken cancellationToken)
        {
            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();

            var intent = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == intentId && i.TenantId == tenantId, cancellationToken);

            if (intent is null ||
                intent.Estado != PlanChangeIntentState.Applied ||
                intent.OldProviderCancellation != ProviderCancellationState.PendingManualCancellation)
            {
                return Outcome(PlanChangeCancellationRetryStatus.NotPending, "El cambio de plan no tiene una cancelación del suscriptor viejo pendiente.");
            }

            var oldSubscriberId = intent.FromProviderSubscriptionId;

            // ── Guarda 1: la cancelación automática está apagada (requisito: skip ≠ intento) ──
            var autoCancelEnabled =
                _providerSubscriptionManager is { IsEnabled: true } &&
                _adminOptions.AutoCancelOldSubscriberOnUpgrade;

            if (!autoCancelEnabled)
            {
                await TryAuditIntentSkipAsync(
                    PlatformAuditActions.PlanChangeOldSubscriberCancellationSkippedAutoCancelDisabled,
                    intent,
                    tenantId,
                    "AutoCancelOldSubscriberOnUpgrade está apagado: no se llamó a TiloPay y NO se consume presupuesto de reintentos. Al encenderlo, el próximo pase intentará de inmediato.",
                    autoCancelEnabled,
                    nowUtc,
                    cancellationToken);

                if (report is not null)
                {
                    report.OldCancellationSkippedAutoCancelDisabled++;
                }

                _logger.LogWarning(
                    "Cancelación de suscriptor viejo SALTADA (AutoCancel apagado). IntentId {IntentId}. TenantId {TenantId}. AttemptCount {AttemptCount}. OldProviderCancellation {State}. ViejoSuffix {OldSuffix}. NuevoSuffix {NewSuffix}.",
                    intent.Id,
                    tenantId,
                    intent.OldCancellationAttemptCount,
                    intent.OldProviderCancellation,
                    SensitiveDataMasker.MaskReference(oldSubscriberId),
                    SensitiveDataMasker.MaskReference(intent.NewProviderSubscriptionId));

                return Outcome(
                    PlanChangeCancellationRetryStatus.SkippedAutoCancelDisabled,
                    "La cancelación automática del suscriptor viejo está deshabilitada.",
                    intent);
            }

            // ── Guarda 2: datos mínimos para un intento VERIFICABLE ──
            // Sin plan viejo no hay getSuscriptorRepeat que confirme la baja, y en este módulo un
            // HTTP 200 sin verificar nunca basta: preferimos no llamar y dejarlo visible en health.
            if (intent.FromTilopayRecurringPlanId is null)
            {
                return await SkipNotEligibleAsync(
                    intent,
                    tenantId,
                    "Falta FromTilopayRecurringPlanId: la baja no se podría verificar con getSuscriptorRepeat. Requiere revisión manual.",
                    autoCancelEnabled,
                    nowUtc,
                    report,
                    cancellationToken);
            }

            // El suscriptor NUEVO es imprescindible: sin él no se puede descartar que viejo y nuevo
            // sean el mismo id, y cancelar "el viejo" podría matar la suscripción que está pagando.
            if (string.IsNullOrWhiteSpace(intent.NewProviderSubscriptionId))
            {
                var recovered = await RecoverNewSubscriberFromPaymentAsync(intent, tenantId, cancellationToken);

                if (string.IsNullOrWhiteSpace(recovered))
                {
                    return await SkipNotEligibleAsync(
                        intent,
                        tenantId,
                        "Falta NewProviderSubscriptionId y no se pudo recuperar desde el pago confirmado: sin el suscriptor nuevo no es seguro cancelar el viejo.",
                        autoCancelEnabled,
                        nowUtc,
                        report,
                        cancellationToken);
                }

                intent.NewProviderSubscriptionId = recovered;
                intent.UpdatedAtUtc = nowUtc;
                await _db.SaveChangesAsync(cancellationToken);
            }

            // ── Guarda 3: backoff (solo ahora que sabemos que el intento SERÍA real) ──
            if (!ignoreBackoff &&
                intent.OldCancellationNextRetryUtc is { } nextRetryUtc &&
                nextRetryUtc > nowUtc)
            {
                return await SkipBackoffAsync(
                    intent,
                    tenantId,
                    $"En backoff tras {intent.OldCancellationAttemptCount} intento(s) real(es). Próximo intento elegible {nextRetryUtc:yyyy-MM-dd HH:mm} UTC.",
                    nextRetryUtc,
                    autoCancelEnabled,
                    nowUtc,
                    report,
                    cancellationToken);
            }

            // ── Guarda 4: tope diario por intent (cinturón de seguridad, no el regulador) ──
            if (!ignoreBackoff)
            {
                var maxPerDay = Math.Max(1, _options.OldCancellationRetryMaxAttemptsPerIntentPerDay);
                var attemptsInWindow = await CountRealAttemptsInWindowAsync(intent, nowUtc, cancellationToken);

                if (attemptsInWindow >= maxPerDay)
                {
                    var resumeUtc = nowUtc.AddHours(24);
                    return await SkipBackoffAsync(
                        intent,
                        tenantId,
                        $"Tope diario alcanzado: {attemptsInWindow} intento(s) REALES contra TiloPay en las últimas 24h (máximo {maxPerDay}). Requiere revisión de soporte; el reintento continúa mañana.",
                        resumeUtc,
                        autoCancelEnabled,
                        nowUtc,
                        report,
                        cancellationToken);
                }
            }

            // ── Intento REAL ──
            // El presupuesto se consume ANTES de llamar al proveedor: si el proceso muere a mitad,
            // el intento igual cuenta y el backoff aplica. Lo contrario permitiría un loop de
            // llamadas a TiloPay ante fallos repetidos.
            intent.OldCancellationAttemptCount++;
            intent.OldCancellationLastAttemptUtc = nowUtc;
            intent.OldCancellationNextRetryUtc =
                PlanChangeCancellationBackoff.NextRetryUtc(nowUtc, intent.OldCancellationAttemptCount);
            intent.UpdatedAtUtc = nowUtc;
            await _db.SaveChangesAsync(cancellationToken);

            var attemptNumber = intent.OldCancellationAttemptCount;

            var result = await _providerSubscriptionManager!.TryCancelOldSubscriberForUpgradeAsync(
                tenantId,
                intent.Id,
                cancellationToken);

            if (!result.ProviderCalled)
            {
                // Una guarda interna del manager frenó la llamada (p. ej. viejo == nuevo). No fue
                // un intento real: se devuelve el presupuesto para no castigar al intent.
                intent.OldCancellationAttemptCount = attemptNumber - 1;
                intent.OldCancellationNextRetryUtc = null;
                await _db.SaveChangesAsync(cancellationToken);

                return await SkipNotEligibleAsync(
                    intent,
                    tenantId,
                    $"No se llamó a TiloPay: {result.Message}",
                    autoCancelEnabled,
                    nowUtc,
                    report,
                    cancellationToken);
            }

            // El manager ya movió OldProviderCancellation y auditó Completed/Failed sobre la MISMA
            // instancia rastreada; aquí solo cerramos el presupuesto y dejamos el rastro del intento.
            var cancelled = intent.OldProviderCancellation == ProviderCancellationState.Cancelled;
            if (cancelled)
            {
                intent.OldCancellationNextRetryUtc = null; // Ya no hay nada que reintentar.
            }

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.PlanChangeOldSubscriberCancellationRetried,
                EntityType = PlatformAuditEntityTypes.Subscription,
                // EntityId = el INTENT: es la clave con la que se cuenta el presupuesto por intent.
                EntityId = intent.Id.ToString(),
                TenantId = tenantId,
                Reason = Trim(
                    $"Intento REAL #{attemptNumber} de cancelación del suscriptor viejo (cambio a {intent.ToPlanCode}). " +
                    $"Resultado {(cancelled ? "baja VERIFICADA" : "sigue pendiente")}. " +
                    $"ViejoSuffix {SensitiveDataMasker.MaskReference(intent.FromProviderSubscriptionId)}. " +
                    $"NuevoSuffix {SensitiveDataMasker.MaskReference(intent.NewProviderSubscriptionId)}. " +
                    $"PlanViejo {intent.FromTilopayRecurringPlanId}. " +
                    $"Próximo elegible {(cancelled ? "n/a" : $"{intent.OldCancellationNextRetryUtc:yyyy-MM-dd HH:mm} UTC")}. " +
                    $"Detalle: {result.Message}",
                    500),
                CreatedAtUtc = nowUtc
            });

            await _db.SaveChangesAsync(cancellationToken);
            DetachAllTracked();

            if (report is not null)
            {
                report.OldSubscriberCancellationsRetried++;
                if (cancelled)
                {
                    report.OldSubscriberCancellationsCompleted++;
                }
            }

            _logger.LogInformation(
                "Intento real de cancelación del suscriptor viejo. IntentId {IntentId}. TenantId {TenantId}. Attempt {Attempt}. Cancelled {Cancelled}. VerificationFailed {VerificationFailed}. NextEligibleUtc {NextEligibleUtc}.",
                intent.Id,
                tenantId,
                attemptNumber,
                cancelled,
                result.VerificationFailed,
                intent.OldCancellationNextRetryUtc);

            return new PlanChangeCancellationRetryOutcome
            {
                Status = cancelled
                    ? PlanChangeCancellationRetryStatus.Cancelled
                    : PlanChangeCancellationRetryStatus.AttemptedStillPending,
                Message = cancelled
                    ? "Suscriptor viejo cancelado y verificado en TiloPay."
                    : $"Se intentó cancelar pero el suscriptor viejo sigue pendiente. Detalle: {result.Message}",
                AttemptCount = attemptNumber,
                NextEligibleUtc = intent.OldCancellationNextRetryUtc
            };
        }

        /// <summary>
        /// Recupera el id_suscriptor NUEVO desde el pago confirmado del cambio. El pago es la
        /// fuente de verdad: lo dejó la resolución por getSuscriptorRepeat al confirmarse.
        /// </summary>
        private async Task<string?> RecoverNewSubscriberFromPaymentAsync(
            PlanChangeIntent intent,
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            if (intent.PagoSuscripcionId is not { } paymentId)
            {
                return null;
            }

            return await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p =>
                    p.Id == paymentId &&
                    p.TenantId == tenantId &&
                    p.Estado == EstadoPagoProveedor.Confirmado &&
                    p.ProviderSubscriberId != null)
                .Select(p => p.ProviderSubscriberId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        /// <summary>
        /// Intentos REALES del intent dentro de la ventana de 24h, contados desde el último
        /// reinicio de presupuesto: los intentos previos a una reparación se hicieron sobre datos
        /// rotos y no deben bloquear el primer intento sobre datos ya sanos.
        /// </summary>
        private async Task<int> CountRealAttemptsInWindowAsync(
            PlanChangeIntent intent,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var windowStartUtc = nowUtc.AddHours(-24);

            if (intent.OldCancellationAttemptsResetAtUtc is { } resetAtUtc && resetAtUtc > windowStartUtc)
            {
                windowStartUtc = resetAtUtc;
            }

            var intentKey = intent.Id.ToString();

            return await _db.PlatformAuditLogs.CountAsync(
                log =>
                    log.Action == PlatformAuditActions.PlanChangeOldSubscriberCancellationRetried &&
                    log.EntityId == intentKey &&
                    log.CreatedAtUtc >= windowStartUtc,
                cancellationToken);
        }

        private async Task<PlanChangeCancellationRetryOutcome> SkipNotEligibleAsync(
            PlanChangeIntent intent,
            Guid tenantId,
            string reason,
            bool autoCancelEnabled,
            DateTime nowUtc,
            BillingReconciliationReport? report,
            CancellationToken cancellationToken)
        {
            await TryAuditIntentSkipAsync(
                PlatformAuditActions.PlanChangeOldSubscriberCancellationSkippedNotEligible,
                intent,
                tenantId,
                reason,
                autoCancelEnabled,
                nowUtc,
                cancellationToken);

            if (report is not null)
            {
                report.OldCancellationSkippedNotEligible++;
            }

            _logger.LogWarning(
                "Cancelación de suscriptor viejo SALTADA (no elegible). IntentId {IntentId}. TenantId {TenantId}. Reason {Reason}. ViejoSuffix {OldSuffix}. NuevoSuffix {NewSuffix}.",
                intent.Id,
                tenantId,
                reason,
                SensitiveDataMasker.MaskReference(intent.FromProviderSubscriptionId),
                SensitiveDataMasker.MaskReference(intent.NewProviderSubscriptionId));

            return Outcome(PlanChangeCancellationRetryStatus.SkippedNotEligible, reason, intent);
        }

        private async Task<PlanChangeCancellationRetryOutcome> SkipBackoffAsync(
            PlanChangeIntent intent,
            Guid tenantId,
            string reason,
            DateTime nextEligibleUtc,
            bool autoCancelEnabled,
            DateTime nowUtc,
            BillingReconciliationReport? report,
            CancellationToken cancellationToken)
        {
            await TryAuditIntentSkipAsync(
                PlatformAuditActions.PlanChangeOldSubscriberCancellationSkippedBackoff,
                intent,
                tenantId,
                reason,
                autoCancelEnabled,
                nowUtc,
                cancellationToken);

            if (report is not null)
            {
                report.OldCancellationSkippedBackoff++;
            }

            // Siempre se loguea (aunque la auditoría esté en cooldown): es la traza que permite
            // ver por qué un suscriptor viejo sigue vivo sin tener que abrir la BD.
            _logger.LogInformation(
                "Cancelación de suscriptor viejo SALTADA por backoff. IntentId {IntentId}. TenantId {TenantId}. AttemptCount {AttemptCount}. NextEligibleUtc {NextEligibleUtc}. Reason {Reason}. AutoCancel {AutoCancel}. OldProviderCancellation {State}. ViejoSuffix {OldSuffix}. NuevoSuffix {NewSuffix}.",
                intent.Id,
                tenantId,
                intent.OldCancellationAttemptCount,
                nextEligibleUtc,
                reason,
                autoCancelEnabled,
                intent.OldProviderCancellation,
                SensitiveDataMasker.MaskReference(intent.FromProviderSubscriptionId),
                SensitiveDataMasker.MaskReference(intent.NewProviderSubscriptionId));

            return new PlanChangeCancellationRetryOutcome
            {
                Status = PlanChangeCancellationRetryStatus.SkippedBackoff,
                Message = reason,
                AttemptCount = intent.OldCancellationAttemptCount,
                NextEligibleUtc = nextEligibleUtc
            };
        }

        /// <summary>
        /// Auditoría de skip con cooldown POR INTENT y POR ACCIÓN: el worker corre cada 20 min, así
        /// que auditar cada pase inundaría una bitácora append-only con la misma noticia. El log
        /// sí sale siempre; la auditoría es el registro duradero para soporte.
        /// </summary>
        private async Task<bool> TryAuditIntentSkipAsync(
            string action,
            PlanChangeIntent intent,
            Guid tenantId,
            string reason,
            bool autoCancelEnabled,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var cooldownCutoffUtc = nowUtc.AddHours(-Math.Max(1, _options.AlertCooldownHours));
            var intentKey = intent.Id.ToString();

            var alreadyAudited = await _db.PlatformAuditLogs.AnyAsync(
                log =>
                    log.Action == action &&
                    log.EntityId == intentKey &&
                    log.CreatedAtUtc >= cooldownCutoffUtc,
                cancellationToken);

            if (alreadyAudited)
            {
                return false;
            }

            // Contexto completo para soporte: nunca el id_suscriptor entero, solo el sufijo.
            var diagnostics = JsonSerializer.Serialize(new
            {
                intentId = intent.Id,
                tenantId,
                attemptCount = intent.OldCancellationAttemptCount,
                lastAttemptUtc = intent.OldCancellationLastAttemptUtc,
                nextEligibleUtc = intent.OldCancellationNextRetryUtc,
                autoCancelOldSubscriberOnUpgrade = autoCancelEnabled,
                oldProviderCancellation = intent.OldProviderCancellation.ToString(),
                fromProviderSubscriptionIdSuffix = SensitiveDataMasker.MaskReference(intent.FromProviderSubscriptionId),
                newProviderSubscriptionIdSuffix = SensitiveDataMasker.MaskReference(intent.NewProviderSubscriptionId),
                fromTilopayRecurringPlanId = intent.FromTilopayRecurringPlanId,
                toPlanCode = intent.ToPlanCode,
                reason
            });

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = action,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = intentKey,
                TenantId = tenantId,
                Reason = Trim(reason, 500),
                AfterJson = diagnostics,
                CreatedAtUtc = nowUtc
            });

            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        /// <summary>Presupuesto de reintentos a cero: el estado cambió, los intentos viejos ya no prueban nada.</summary>
        private static void ResetOldCancellationBudget(PlanChangeIntent intent, DateTime nowUtc)
        {
            intent.OldCancellationAttemptCount = 0;
            intent.OldCancellationNextRetryUtc = null;
            intent.OldCancellationAttemptsResetAtUtc = nowUtc;
            intent.UpdatedAtUtc = nowUtc;
        }

        private static PlanChangeCancellationRetryOutcome Outcome(
            PlanChangeCancellationRetryStatus status,
            string message,
            PlanChangeIntent? intent = null) =>
            new()
            {
                Status = status,
                Message = message,
                AttemptCount = intent?.OldCancellationAttemptCount ?? 0,
                NextEligibleUtc = intent?.OldCancellationNextRetryUtc
            };

        // ── Aislamiento de fases y multi-tenant ──────────────────────────────────────

        /// <summary>
        /// Ejecuta una fase con el ChangeTracker limpio y aislada de fallos: si lanza, se registra,
        /// se audita y el pase CONTINÚA con las demás fases. Nunca deja entidades tenant-scoped
        /// colgadas que puedan mezclar tenants en un SaveChanges posterior.
        /// </summary>
        private async Task RunPhaseAsync(string phase, Func<Task> body, CancellationToken cancellationToken)
        {
            DetachAllTracked();

            try
            {
                await body();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Descartar cualquier cambio parcial para poder auditar el fallo sin re-lanzar.
                DetachAllTracked();

                _logger.LogError(ex, "Fase de reconciliación aislada por fallo. Phase {Phase}.", phase);

                try
                {
                    _db.PlatformAuditLogs.Add(new PlatformAuditLog
                    {
                        Id = Guid.NewGuid(),
                        ActorUserId = "system",
                        ActorEmail = "system",
                        Action = PlatformAuditActions.BillingReconciliationAlert,
                        EntityType = PlatformAuditEntityTypes.Billing,
                        Reason = Trim($"La fase '{phase}' de la reconciliación falló y se aisló; el resto del pase continuó. Detalle: {ex.Message}", 500),
                        CreatedAtUtc = GetUtcNow()
                    });

                    await _db.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "No fue posible auditar el fallo de la fase {Phase}.", phase);
                    DetachAllTracked();
                }
            }
        }

        private static string Trim(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];

        /// <summary>Deja el ChangeTracker vacío para que ninguna fase arrastre entidades de otra.</summary>
        private void DetachAllTracked()
        {
            foreach (var entry in _db.ChangeTracker.Entries().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        /// <summary>
        /// Registra que se encontraron registros sin TenantId donde debería existir: no se mezclan
        /// con entidades tenant-scoped; se auditan y se saltan (task 6).
        /// </summary>
        private async Task AuditMissingTenantSkipAsync(string phase, int count, CancellationToken cancellationToken)
        {
            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.BillingReconciliationAlert,
                EntityType = PlatformAuditEntityTypes.Billing,
                Reason = $"La fase '{phase}' encontró {count} registro(s) sin TenantId resuelto. Se auditan y se saltan para no mezclar tenants.",
                CreatedAtUtc = GetUtcNow()
            });

            await _db.SaveChangesAsync(cancellationToken);
            DetachAllTracked();

            _logger.LogWarning(
                "Reconciliación: {Count} registro(s) sin TenantId en la fase {Phase}; saltados.",
                count,
                phase);
        }

        // ── 6. Backfill de id_suscriptor faltante (subscriber resolution) ─────────────

        private async Task BackfillMissingSubscriberIdsAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            // Solo si la integración admin de TiloPay está activa; si no, no hay forma de resolver.
            if (_subscriberResolutionService is null || !_subscriberResolutionService.IsEnabled)
            {
                return;
            }

            // Pass A (local, sin API): suscripción base con ProviderSubscriptionId NULL cuando ya
            // existe un pago confirmado del mismo tenant con ProviderSubscriberId conocido → copiar.
            // Escaneo cross-tenant SOLO lectura; la escritura va por tenant bajo su propio scope.
            var subscriptionsMissingId = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(subscription =>
                    subscription.Proveedor == PaymentProviderType.Tilopay &&
                    subscription.TilopayRecurringPlanId != null &&
                    subscription.ProviderSubscriptionId == null &&
                    (subscription.Estado == EstadoSuscripcion.Activa ||
                     subscription.Estado == EstadoSuscripcion.Morosa))
                .Select(subscription => new { subscription.Id, subscription.TenantId })
                .ToListAsync(cancellationToken);

            foreach (var group in subscriptionsMissingId.GroupBy(subscription => subscription.TenantId))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (group.Key == Guid.Empty)
                {
                    await AuditMissingTenantSkipAsync("SubscriberBackfillLocal", group.Count(), cancellationToken);
                    continue;
                }

                try
                {
                    await BackfillSubscriberIdsLocallyForTenantAsync(group.Key, report, nowUtc, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DetachAllTracked();
                    _logger.LogError(
                        ex,
                        "Backfill local de id_suscriptor falló para el tenant {TenantId}; se continúa con los demás.",
                        group.Key);
                }
            }

            // Pass B (API): pagos recurrentes CONFIRMADOS sin ProviderSubscriberId y con email.
            // Se resuelve por (plan, email) contra TiloPay; el servicio persiste y audita.
            var lookbackUtc = nowUtc.AddDays(-Math.Max(1, _options.ConfirmedPaymentLookbackDays));
            var paymentsMissingSubscriber = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment =>
                    payment.Proveedor == PaymentProviderType.Tilopay &&
                    payment.Estado == EstadoPagoProveedor.Confirmado &&
                    payment.TilopayRecurringPlanId != null &&
                    payment.ProviderSubscriberId == null &&
                    payment.ClienteEmail != null &&
                    payment.FechaConfirmacionUtc >= lookbackUtc)
                .OrderByDescending(payment => payment.FechaConfirmacionUtc)
                .Select(payment => new
                {
                    payment.Id,
                    payment.TenantId,
                    payment.PlanId,
                    payment.TilopayRecurringPlanId,
                    payment.ClienteEmail
                })
                .Take(100)
                .ToListAsync(cancellationToken);

            foreach (var payment in paymentsMissingSubscriber)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // No reintentar indefinidamente: si ya hubo demasiados intentos fallidos/pending,
                // se deja como alerta persistente (visible en BillingHealth) y no se llama más.
                var priorAttempts = await _db.PlatformAuditLogs.CountAsync(
                    log =>
                        (log.Action == PlatformAuditActions.ProviderSubscriberResolutionPending ||
                         log.Action == PlatformAuditActions.ProviderSubscriberResolutionFailed) &&
                        log.EntityId == payment.Id.ToString(),
                    cancellationToken);

                if (priorAttempts >= Math.Max(1, _adminMaxAttempts))
                {
                    continue;
                }

                var isAddon = _repeatOptions.FindByRecurringPlanId(payment.TilopayRecurringPlanId)?.IsAddon ?? false;

                var outcome = await _subscriberResolutionService.TryResolveAndPersistAsync(
                    new SubscriberResolutionContext
                    {
                        TenantId = payment.TenantId,
                        TilopayRecurringPlanId = payment.TilopayRecurringPlanId!.Value,
                        Email = payment.ClienteEmail,
                        PaymentId = payment.Id,
                        IsAddon = isAddon,
                        Source = "reconciliation"
                    },
                    cancellationToken);

                switch (outcome)
                {
                    case SubscriberPersistenceOutcome.Resolved:
                        report.SubscriberIdsResolved++;
                        break;
                    case SubscriberPersistenceOutcome.Ambiguous:
                        report.SubscriberIdsAmbiguous++;
                        break;
                    case SubscriberPersistenceOutcome.Pending:
                    case SubscriberPersistenceOutcome.Failed:
                        report.SubscriberIdsPending++;
                        break;
                }
            }
        }

        /// <summary>
        /// Copia el id_suscriptor conocido (de un pago confirmado) a las suscripciones del tenant
        /// que lo tienen NULL. Todo bajo el scope del tenant y un único SaveChanges por tenant.
        /// </summary>
        private async Task BackfillSubscriberIdsLocallyForTenantAsync(
            Guid tenantId,
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(tenantId);

            // Filtros EXPLÍCITOS por tenant: no dependemos del query filter ambiente.
            var knownSubscriberId = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment =>
                    payment.TenantId == tenantId &&
                    payment.Proveedor == PaymentProviderType.Tilopay &&
                    payment.Estado == EstadoPagoProveedor.Confirmado &&
                    payment.ProviderSubscriberId != null)
                .OrderByDescending(payment => payment.FechaConfirmacionUtc)
                .Select(payment => payment.ProviderSubscriberId)
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(knownSubscriberId))
            {
                return;
            }

            var subscriptions = await _db.Suscripciones
                .IgnoreQueryFilters()
                .Where(subscription =>
                    subscription.TenantId == tenantId &&
                    subscription.Proveedor == PaymentProviderType.Tilopay &&
                    subscription.TilopayRecurringPlanId != null &&
                    subscription.ProviderSubscriptionId == null &&
                    (subscription.Estado == EstadoSuscripcion.Activa ||
                     subscription.Estado == EstadoSuscripcion.Morosa))
                .ToListAsync(cancellationToken);

            if (subscriptions.Count == 0)
            {
                return;
            }

            foreach (var subscription in subscriptions)
            {
                subscription.ProviderSubscriptionId = knownSubscriberId;
                subscription.FechaUltimaActualizacionUtc = nowUtc;

                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = PlatformAuditActions.ProviderSubscriberResolved,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    EntityId = subscription.Id.ToString(),
                    TenantId = subscription.TenantId,
                    Reason = "id_suscriptor copiado localmente desde un pago confirmado del mismo tenant (reconciliación).",
                    CreatedAtUtc = nowUtc
                });

                report.SubscriberIdsBackfilledLocally++;
            }

            await _db.SaveChangesAsync(cancellationToken);
            DetachAllTracked();
        }

        // ── 1. Pagos confirmados sin activación ──────────────────────────────────────

        private async Task ReconcileOrphanConfirmedPaymentsAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var lookbackUtc = nowUtc.AddDays(-Math.Max(1, _options.ConfirmedPaymentLookbackDays));

            var confirmedPayments = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(payment => payment.Plan)
                .Where(payment =>
                    payment.Proveedor == PaymentProviderType.Tilopay &&
                    payment.Estado == EstadoPagoProveedor.Confirmado &&
                    payment.TilopayRecurringPlanId != null &&
                    payment.FechaConfirmacionUtc != null &&
                    payment.FechaConfirmacionUtc >= lookbackUtc)
                .ToListAsync(cancellationToken);

            foreach (var payment in confirmedPayments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var repeatPlan = _repeatOptions.FindByRecurringPlanId(payment.TilopayRecurringPlanId);

                if (repeatPlan is null || payment.Plan is null || !payment.Plan.Activo)
                {
                    continue; // Plan retirado del catálogo: no hay forma segura de decidir nada.
                }

                if (repeatPlan.IsAddon)
                {
                    await ReconcileOrphanAddonPaymentAsync(report, payment, cancellationToken);
                    continue;
                }

                var suscripcion = await _db.Suscripciones
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(current => current.TenantId == payment.TenantId, cancellationToken);

                if (suscripcion is not null && _suscripcionService.CanAccessApp(suscripcion))
                {
                    continue; // Sano: hay acceso vigente, el pago fue aplicado.
                }

                var paymentAppliedToSubscription =
                    suscripcion is not null &&
                    suscripcion.FechaUltimoPagoUtc.HasValue &&
                    payment.FechaConfirmacionUtc.HasValue &&
                    suscripcion.FechaUltimoPagoUtc.Value >= payment.FechaConfirmacionUtc.Value.AddMinutes(-2);

                if (paymentAppliedToSubscription)
                {
                    // El pago sí se aplicó y la suscripción venció después por causas normales.
                    // Eso lo cubre el chequeo de renovaciones vencidas, no este.
                    continue;
                }

                if (suscripcion is not null && suscripcion.Estado == EstadoSuscripcion.Cancelada)
                {
                    // Pago confirmado sobre una suscripción cancelada explícitamente: ambiguo
                    // (¿cobro póstumo de TiloPay? ¿cancelación equivocada?). Humano decide.
                    if (await TryAddAlertAsync(
                        report,
                        PlatformAuditEntityTypes.Subscription,
                        payment.Id.ToString(),
                        payment.TenantId,
                        $"Pago recurrente confirmado ({payment.Monto:0.00} {payment.Moneda}, tx {payment.ProviderTransactionId}) sobre una suscripción CANCELADA. Posible cobro póstumo en TiloPay: revisar y cancelar la suscripción del proveedor o reactivar según corresponda.",
                        cancellationToken))
                    {
                        report.OrphanPaymentsAlerted++;
                    }

                    continue;
                }

                if (!_options.AutoRepairEnabled)
                {
                    if (await TryAddAlertAsync(
                        report,
                        PlatformAuditEntityTypes.Subscription,
                        payment.Id.ToString(),
                        payment.TenantId,
                        $"Pago recurrente confirmado sin activación ({payment.Monto:0.00} {payment.Moneda}, tx {payment.ProviderTransactionId}). AutoRepair está deshabilitado: activar manualmente desde Platform/RecurringCheckouts.",
                        cancellationToken))
                    {
                        report.OrphanPaymentsAlerted++;
                    }

                    continue;
                }

                await RepairOrphanBasePaymentAsync(report, payment, suscripcion, cancellationToken);
            }
        }

        private async Task RepairOrphanBasePaymentAsync(
            BillingReconciliationReport report,
            PagoSuscripcion payment,
            Suscripcion? suscripcionBefore,
            CancellationToken cancellationToken)
        {
            var estadoAnterior = suscripcionBefore?.Estado.ToString() ?? "(sin suscripción)";

            // Tenant scope: SESSION_CONTEXT correcto para RLS al escribir fuera de un request.
            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(payment.TenantId);

            await _suscripcionService.ActivarSuscripcionRecurrenteAsync(
                payment.TenantId,
                payment.Plan!,
                payment.TilopayRecurringPlanId!.Value,
                payment.ProviderSubscriberId,
                payment.ProviderTransactionId,
                payment.ProviderReference ?? payment.ReferenciaInterna,
                motivo: "Reparación automática: pago confirmado sin activación detectado por reconciliación.",
                cancellationToken: cancellationToken);

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.BillingAutoRepairApplied,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = payment.Id.ToString(),
                TenantId = payment.TenantId,
                BeforeJson = JsonSerializer.Serialize(new { estado = estadoAnterior }),
                AfterJson = JsonSerializer.Serialize(new { estado = EstadoSuscripcion.Activa.ToString() }),
                Reason = $"Pago confirmado sin activación reparado. Monto {payment.Monto:0.00} {payment.Moneda}. Tx {payment.ProviderTransactionId}. Plan {payment.Plan!.Codigo ?? payment.Plan.Nombre}.",
                CreatedAtUtc = GetUtcNow()
            });

            await _db.SaveChangesAsync(cancellationToken);

            report.OrphanPaymentsRepaired++;

            _logger.LogWarning(
                "Reconciliación reparó pago confirmado sin activación. TenantId {TenantId}. PaymentId {PaymentId}. PlanId {PlanId}. Monto {Monto} {Moneda}. TransactionId {TransactionId}. EstadoAnterior {EstadoAnterior}. EstadoNuevo {EstadoNuevo}.",
                payment.TenantId,
                payment.Id,
                payment.PlanId,
                payment.Monto,
                payment.Moneda,
                payment.ProviderTransactionId,
                estadoAnterior,
                EstadoSuscripcion.Activa);
        }

        private async Task ReconcileOrphanAddonPaymentAsync(
            BillingReconciliationReport report,
            PagoSuscripcion payment,
            CancellationToken cancellationToken)
        {
            var addon = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.TenantId == payment.TenantId, cancellationToken);

            if (addon is not null && _suscripcionService.IsWhatsAppAddonActive(addon))
            {
                return;
            }

            var paymentApplied =
                addon is not null &&
                payment.FechaConfirmacionUtc.HasValue &&
                addon.UpdatedAtUtc >= payment.FechaConfirmacionUtc.Value.AddMinutes(-2);

            if (paymentApplied)
            {
                return;
            }

            // Los add-ons NO se auto-reparan: activarlos enciende automatizaciones de WhatsApp
            // hacia clientes finales. Un humano confirma primero que el plan base está sano.
            if (await TryAddAlertAsync(
                report,
                PlatformAuditEntityTypes.Subscription,
                payment.Id.ToString(),
                payment.TenantId,
                $"Pago de add-on WhatsApp confirmado ({payment.Monto:0.00} {payment.Moneda}, tx {payment.ProviderTransactionId}) sin add-on activo. Revisar y activar manualmente desde Platform/RecurringCheckouts.",
                cancellationToken))
            {
                report.OrphanPaymentsAlerted++;
            }
        }

        // ── 2. Renovaciones vencidas (solo alerta) ───────────────────────────────────

        private async Task AlertOverdueRenewalsAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var overdueCutoffUtc = nowUtc.AddHours(-Math.Max(1, _options.OverdueRenewalToleranceHours));

            var overdueSubscriptions = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(subscription =>
                    subscription.Proveedor == PaymentProviderType.Tilopay &&
                    subscription.TilopayRecurringPlanId != null &&
                    subscription.Estado == EstadoSuscripcion.Activa &&
                    !subscription.CancelAtPeriodEnd &&
                    subscription.FechaProximoCobroUtc != null &&
                    subscription.FechaProximoCobroUtc < overdueCutoffUtc &&
                    // "Sin webhook" = no sabemos nada. Si ya hay un fallo/recuperación registrado, el
                    // cobro SÍ se reportó (falló) y el incidente lo sigue: alertar sería ruido.
                    subscription.PaymentRecoveryStatus == null &&
                    subscription.LastPaymentFailedAtUtc == null &&
                    // Solo vencida si TiloPay TAMPOCO va a cobrar más tarde: si el proveedor tiene un
                    // expire posterior (reactivó/extendió), no es una renovación vencida, es que
                    // nuestra fecha local va por detrás. Evita una alerta falsa de renovación vencida.
                    (subscription.ProviderExpiresAtUtc == null ||
                     subscription.ProviderExpiresAtUtc < overdueCutoffUtc))
                .Select(subscription => new
                {
                    subscription.Id,
                    subscription.TenantId,
                    subscription.CodigoPlan,
                    subscription.FechaProximoCobroUtc
                })
                .ToListAsync(cancellationToken);

            foreach (var subscription in overdueSubscriptions)
            {
                if (await TryAddAlertAsync(
                    report,
                    PlatformAuditEntityTypes.Subscription,
                    subscription.Id.ToString(),
                    subscription.TenantId,
                    $"Renovación vencida sin webhook: plan {subscription.CodigoPlan}, próximo cobro esperado {subscription.FechaProximoCobroUtc:yyyy-MM-dd HH:mm} UTC. Verificar en TiloPay si el cobro ocurrió (activar por conciliación) o falló (el período de gracia aplicará solo).",
                    cancellationToken))
                {
                    report.OverdueRenewalsAlerted++;
                }
            }

            var overdueAddons = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(addon =>
                    addon.Estado == EstadoSuscripcion.Activa &&
                    addon.TilopayRecurringPlanId != null &&
                    addon.FechaProximoCobroUtc != null &&
                    addon.FechaProximoCobroUtc < overdueCutoffUtc)
                .Select(addon => new { addon.Id, addon.TenantId, addon.AddonCode, addon.FechaProximoCobroUtc })
                .ToListAsync(cancellationToken);

            foreach (var addon in overdueAddons)
            {
                if (await TryAddAlertAsync(
                    report,
                    PlatformAuditEntityTypes.Subscription,
                    addon.Id.ToString(),
                    addon.TenantId,
                    $"Renovación de add-on vencida sin webhook: {addon.AddonCode}, próximo cobro esperado {addon.FechaProximoCobroUtc:yyyy-MM-dd HH:mm} UTC. Verificar el cobro en TiloPay.",
                    cancellationToken))
                {
                    report.OverdueAddonsAlerted++;
                }
            }
        }

        // ── 2b. Add-on: reintento de cancelación saliente + alerta add-on sin base ────

        /// <summary>
        /// Reintenta la baja del suscriptor del add-on pendiente (huérfano de Strategy B, cascada del
        /// plan base o cambio manual). Delega en <see cref="IAddonSubscriptionManager"/>, que verifica
        /// contra TiloPay (un 200 nunca basta) y lleva el presupuesto/backoff en la fila del add-on.
        /// Si el API admin está apagado, ALERTA el dinero en riesgo (no puede cancelar). Aislado por
        /// tenant. NUNCA toca el plan base.
        /// </summary>
        private async Task RetryPendingAddonCancellationsAsync(
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            var nowUtc = GetUtcNow();

            var candidates = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(addon =>
                    addon.ProviderCancellation == ProviderCancellationState.PendingManualCancellation &&
                    addon.PendingCancellationProviderSubscriptionId != null &&
                    (addon.ProviderCancellationNextRetryUtc == null ||
                     addon.ProviderCancellationNextRetryUtc <= nowUtc))
                .OrderBy(addon => addon.UpdatedAtUtc)
                .Select(addon => new { addon.Id, addon.TenantId })
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0)
            {
                return;
            }

            // API admin apagado: no hay forma de cancelar (ni verificar) el suscriptor del add-on.
            // Cada pendiente es dinero en riesgo → alertar para baja manual en TiloPay.
            if (_addonSubscriptionManager is null || !_addonSubscriptionManager.IsEnabled)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.TenantId == Guid.Empty)
                    {
                        continue;
                    }

                    DetachAllTracked();
                    if (await TryAddAlertAsync(
                        report,
                        PlatformAuditEntityTypes.WhatsAppAddon,
                        candidate.Id.ToString(),
                        candidate.TenantId,
                        "Suscriptor de add-on WhatsApp pendiente de cancelación en TiloPay, pero el API admin está deshabilitado. Riesgo de doble cobro del add-on: cancelar manualmente en TiloPay.",
                        cancellationToken))
                    {
                        report.AddonCancellationsPendingProviderDisabled++;
                    }
                }

                return;
            }

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (candidate.TenantId == Guid.Empty)
                {
                    continue;
                }

                DetachAllTracked();

                try
                {
                    var outcome = await _addonSubscriptionManager.TryCancelPendingAddonSubscriberAsync(
                        candidate.TenantId,
                        cancellationToken);

                    if (outcome.ProviderCalled)
                    {
                        report.AddonCancellationsRetried++;
                        if (outcome.Cancelled)
                        {
                            report.AddonCancellationsCompleted++;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DetachAllTracked();
                    _logger.LogError(
                        ex,
                        "Reintento de cancelación de suscriptor de add-on falló. TenantId {TenantId}. AddonId {AddonId}. Se continúa.",
                        candidate.TenantId,
                        candidate.Id);
                }
            }
        }

        /// <summary>
        /// Le pregunta a TiloPay cuántos suscriptores de add-on puede cobrarle a cada tenant con
        /// add-on recurrente vivo, y deja el snapshot para BillingHealth/Mission Control.
        ///
        /// Es la única fase que puede ver este fallo: el estado local puede quedar
        /// perfecto (un WA800 activo) mientras TiloPay tenía WA400 y WA800 cobrables a la vez. Solo
        /// LECTURA contra el proveedor: nunca cancela nada (eso lo decide un humano o
        /// RetryPendingAddonCancellations con su verificación).
        ///
        /// Aislada por tenant y con tope por pase para no disparar la cuota del API admin.
        /// </summary>
        private async Task AuditAddonProviderStateAsync(
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            if (_addonProviderAudit is null || !_addonProviderAudit.IsEnabled)
            {
                return;
            }

            var maxTenants = Math.Clamp(_options.MaxAddonProviderAuditsPerRun, 0, 200);
            if (maxTenants == 0)
            {
                return;
            }

            // Solo tenants con add-on PAGADO por TiloPay: los manuales/cortesía no tienen
            // suscriptor recurrente y preguntarle al proveedor por ellos no aporta nada.
            var candidates = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(addon =>
                    addon.BillingSource == WhatsAppAddonBillingSource.ProviderRecurring &&
                    (addon.Estado == EstadoSuscripcion.Activa ||
                     addon.Estado == EstadoSuscripcion.Morosa ||
                     addon.PendingCancellationProviderSubscriptionId != null))
                .OrderBy(addon => addon.UpdatedAtUtc)
                .Select(addon => addon.TenantId)
                .Distinct()
                .Take(maxTenants)
                .ToListAsync(cancellationToken);

            foreach (var tenantId in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (tenantId == Guid.Empty)
                {
                    continue;
                }

                DetachAllTracked();

                try
                {
                    var audit = await _addonProviderAudit.AuditAsync(
                        tenantId,
                        customerEmail: null,
                        source: "reconciliation",
                        auditAction: PlatformAuditActions.AddonProviderDoubleActiveDetected,
                        cancellationToken);

                    if (!audit.Executed)
                    {
                        continue;
                    }

                    report.AddonProviderAudits++;

                    if (audit.HasDoubleActive)
                    {
                        report.AddonProviderDoubleActiveDetected++;
                        _logger.LogCritical(
                            "Auditoría del proveedor: {Count} suscriptores de add-on COBRABLES para el tenant {TenantId}. Riesgo de doble cobro. {Detail}",
                            audit.ChargeableCount,
                            tenantId,
                            audit.Detail);
                    }
                    else if (audit.IsInconclusive)
                    {
                        report.AddonProviderAuditsInconclusive++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DetachAllTracked();
                    _logger.LogError(
                        ex,
                        "La auditoría del proveedor del add-on falló para el tenant {TenantId}. Se continúa.",
                        tenantId);
                }
            }
        }

        /// <summary>
        /// R5/regla 11: alerta los add-ons de WhatsApp ACTIVOS cuyo plan base está cancelado/vencido
        /// (o no puede acceder a la app). Solo lectura y local (sin HTTP). Nunca toca datos: un humano
        /// decide cancelar el add-on o corregir el estado del base. El add-on NO se auto-cancela aquí
        /// para no cortar automatizaciones hacia clientes finales sin revisión.
        /// </summary>
        private async Task AlertAddonsWithoutActiveBaseAsync(
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            var addons = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(addon =>
                    addon.Estado == EstadoSuscripcion.Activa ||
                    addon.Estado == EstadoSuscripcion.Morosa ||
                    addon.Estado == EstadoSuscripcion.Trial)
                .ToListAsync(cancellationToken);

            var activeAddons = addons
                .Where(addon => _suscripcionService.IsWhatsAppAddonActive(addon))
                .ToList();

            if (activeAddons.Count == 0)
            {
                return;
            }

            var tenantIds = activeAddons.Select(addon => addon.TenantId).Distinct().ToList();

            var baseSubscriptions = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(subscription => tenantIds.Contains(subscription.TenantId))
                .ToListAsync(cancellationToken);

            var baseByTenant = baseSubscriptions
                .GroupBy(subscription => subscription.TenantId)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(subscription => subscription.FechaUltimaActualizacionUtc ?? subscription.FechaInicio)
                        .First());

            foreach (var addon in activeAddons)
            {
                cancellationToken.ThrowIfCancellationRequested();

                baseByTenant.TryGetValue(addon.TenantId, out var baseSubscription);

                var basePlanCode = baseSubscription?.CodigoPlan;
                var baseIsRealBasePlan = baseSubscription is not null &&
                    (string.IsNullOrWhiteSpace(basePlanCode) ||
                     !PlanCodes.WhatsAppAddons.Contains(basePlanCode, StringComparer.OrdinalIgnoreCase));

                var hasActiveBase = baseSubscription is not null &&
                                    baseIsRealBasePlan &&
                                    _suscripcionService.CanAccessApp(baseSubscription);

                if (hasActiveBase)
                {
                    continue;
                }

                var estadoBase = baseSubscription is null
                    ? "sin plan base"
                    : _suscripcionService.GetEffectiveStatus(baseSubscription).ToString();

                DetachAllTracked();
                if (await TryAddAlertAsync(
                    report,
                    PlatformAuditEntityTypes.WhatsAppAddon,
                    addon.Id.ToString(),
                    addon.TenantId,
                    $"Add-on WhatsApp {addon.AddonCode} ACTIVO con plan base {estadoBase}. Revisar: cancelar el add-on (no debe seguir cobrando sin SaaS) o corregir el estado del plan base.",
                    cancellationToken))
                {
                    report.AddonsWithoutActiveBaseAlerted++;
                }
            }
        }

        // ── 3. Pendientes abandonados (limpieza segura) ──────────────────────────────

        private async Task ExpireStalePendingAttemptsAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var staleCutoffUtc = nowUtc.AddDays(-Math.Max(1, _options.StalePendingDays));

            // Escaneo cross-tenant SOLO lectura (id + tenant). La escritura se hace por tenant,
            // cada uno bajo su propio scope y SaveChanges, para no mezclar tenants (guard RLS).
            var stalePendings = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment =>
                    payment.Proveedor == PaymentProviderType.Tilopay &&
                    payment.Estado == EstadoPagoProveedor.Pendiente &&
                    payment.FechaCreacionUtc < staleCutoffUtc)
                .Select(payment => new { payment.Id, payment.TenantId })
                .ToListAsync(cancellationToken);

            if (stalePendings.Count == 0)
            {
                return;
            }

            foreach (var group in stalePendings.GroupBy(payment => payment.TenantId))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (group.Key == Guid.Empty)
                {
                    // Task 6: registros sin TenantId no se mezclan; se auditan y se saltan.
                    await AuditMissingTenantSkipAsync("ExpireStalePendings", group.Count(), cancellationToken);
                    continue;
                }

                try
                {
                    await ExpireStalePendingsForTenantAsync(
                        group.Key,
                        group.Select(payment => payment.Id).ToList(),
                        report,
                        nowUtc,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Aislar por tenant: un tenant que falle no detiene la expiración de los demás.
                    DetachAllTracked();
                    _logger.LogError(
                        ex,
                        "No fue posible expirar pendientes stale del tenant {TenantId}; se continúa con los demás.",
                        group.Key);
                }
            }
        }

        private async Task ExpireStalePendingsForTenantAsync(
            Guid tenantId,
            IReadOnlyList<Guid> paymentIds,
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(tenantId);

            // Filtro EXPLÍCITO por tenant (no depende del query filter ambiente): recargamos tracked
            // solo las filas de ESTE tenant. El scope sirve para que el guard use la ruta tenant.
            var payments = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .Where(payment => payment.TenantId == tenantId &&
                                  paymentIds.Contains(payment.Id) &&
                                  payment.Estado == EstadoPagoProveedor.Pendiente)
                .ToListAsync(cancellationToken);

            if (payments.Count == 0)
            {
                return;
            }

            foreach (var payment in payments)
            {
                payment.Estado = EstadoPagoProveedor.Expirado;
                payment.ProviderResultCode = "EXPIRED_STALE";
                payment.ProviderResultMessage =
                    $"Expirado por reconciliación: {_options.StalePendingDays} días sin webhook ni confirmación (checkout abandonado).";
                payment.FechaActualizacionUtc = nowUtc;

                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = PlatformAuditActions.BillingReconciliationCleanup,
                    EntityType = PlatformAuditEntityTypes.Billing,
                    EntityId = payment.Id.ToString(),
                    TenantId = payment.TenantId,
                    Reason = $"Intento Pendiente abandonado ({payment.FechaCreacionUtc:yyyy-MM-dd}) marcado Expirado. Plan {payment.PlanId}.",
                    CreatedAtUtc = nowUtc
                });

                report.StalePendingsExpired++;
            }

            // Changeset de un solo tenant: el guard usa la ruta tenant y no lanza "mezclar tenants".
            await _db.SaveChangesAsync(cancellationToken);
            DetachAllTracked();
        }

        // ── 4. ManualReview viejos (solo alerta, nunca se tocan) ─────────────────────

        private async Task AlertStaleManualReviewsAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var staleCutoffUtc = nowUtc.AddHours(-Math.Max(1, _options.StaleManualReviewHours));

            var staleReviews = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment =>
                    payment.Proveedor == PaymentProviderType.Tilopay &&
                    payment.Estado == EstadoPagoProveedor.ManualReview &&
                    (payment.FechaActualizacionUtc ?? payment.FechaCreacionUtc) < staleCutoffUtc)
                .Select(payment => new { payment.Id, payment.TenantId, payment.Monto, payment.Moneda, payment.ProviderResultMessage })
                .ToListAsync(cancellationToken);

            foreach (var payment in staleReviews)
            {
                if (await TryAddAlertAsync(
                    report,
                    PlatformAuditEntityTypes.Subscription,
                    payment.Id.ToString(),
                    payment.TenantId,
                    $"Pago en ManualReview sin resolver hace más de {_options.StaleManualReviewHours}h ({payment.Monto:0.00} {payment.Moneda}). Puede haber dinero cobrado sin activar y el tenant tiene el checkout bloqueado. Resolver en Platform/RecurringCheckouts. Detalle: {payment.ProviderResultMessage}",
                    cancellationToken))
                {
                    report.StaleManualReviewsAlerted++;
                }
            }
        }

        // ── 5. Eventos de pago atascados (solo alerta) ───────────────────────────────

        private async Task AlertStuckEventsAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var stuckCutoffUtc = nowUtc.AddMinutes(-Math.Max(5, _options.StuckEventMinutes));

            var stuckEvents = await _db.EventosPago
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(evento =>
                    !evento.Procesado &&
                    (evento.EstadoProcesamiento == "Recibido" || evento.EstadoProcesamiento == "Error") &&
                    evento.FechaRecepcionUtc < stuckCutoffUtc)
                .Select(evento => new { evento.Id, evento.TenantId, evento.Tipo, evento.EstadoProcesamiento, evento.Error })
                .ToListAsync(cancellationToken);

            foreach (var evento in stuckEvents)
            {
                if (await TryAddAlertAsync(
                    report,
                    PlatformAuditEntityTypes.Billing,
                    evento.Id.ToString(),
                    evento.TenantId,
                    $"Evento de pago atascado en '{evento.EstadoProcesamiento}' (tipo {evento.Tipo}). El procesamiento se interrumpió y TiloPay no reintentó. Error: {evento.Error ?? "(sin detalle)"}",
                    cancellationToken))
                {
                    report.StuckEventsAlerted++;
                }
            }
        }

        // ── Infraestructura de alertas ───────────────────────────────────────────────

        /// <summary>
        /// Alerta idempotente: no repite la misma alerta sobre la misma entidad dentro
        /// de la ventana de cooldown, para que el pase diario no llene la bitácora.
        /// </summary>
        // ── Sanar recuperaciones falsas: provider Active + renovado, local en gracia/morosa ──────

        /// <summary>
        /// Repara el caso del webhook success que quedó SinRelacion: una suscripción BASE local en
        /// gracia/morosa que en realidad YA pagó un período nuevo. Verifica SIEMPRE contra
        /// getSuscriptorRepeat (un estado local nunca basta) y exige el suscriptor Active.
        ///
        /// "Active" NO es evidencia de pago: tras un rechazo TiloPay sigue Active mientras reintenta,
        /// con el mismo expire. Solo se sana con evidencia de un período NUEVO posterior al fallo:
        /// (A) un PagoSuscripcion Confirmado del plan después del fallo, o (B) el expire del proveedor
        /// avanzó sobre el fallo (<see cref="PaymentRecoveryRenewalEvidence"/>). Sin evidencia no se
        /// toca NADA: la gracia original sigue corriendo y el pase de gracia decide al vencer.
        ///
        /// Con evidencia: cierra los incidentes base vivos (Open/GraceExpired) del plan, reactiva,
        /// limpia la recuperación, alinea fechas con el expire (solo extiende) y audita. NUNCA degrada.
        /// HTTP fuera de transacción.
        /// </summary>
        private async Task HealRecoveredBaseSubscriptionsAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            if (_adminService is null || !_adminService.IsEnabled)
            {
                return;
            }

            var candidates = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(subscription =>
                    subscription.Proveedor == PaymentProviderType.Tilopay &&
                    subscription.TilopayRecurringPlanId != null &&
                    subscription.ProviderSubscriptionId != null &&
                    !subscription.CancelAtPeriodEnd &&
                    (subscription.PaymentRecoveryStatus == "GraceActive" ||
                     subscription.PaymentRecoveryStatus == "GraceExpired" ||
                     subscription.Estado == EstadoSuscripcion.Morosa))
                .Select(subscription => new
                {
                    subscription.Id,
                    subscription.TenantId,
                    subscription.TilopayRecurringPlanId,
                    subscription.ProviderSubscriptionId,
                    subscription.CodigoPlan,
                    subscription.LastPaymentFailedAtUtc,
                    subscription.FechaFin
                })
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.TenantId == Guid.Empty || candidate.TilopayRecurringPlanId is not { } recurringPlanId)
                {
                    continue;
                }

                DetachAllTracked();

                try
                {
                    // Verificación en el proveedor (HTTP fuera de tx): ¿Active con expire vigente?
                    var subscribers = await _adminService.GetSuscriptorRepeatAsync(recurringPlanId, cancellationToken);
                    var match = subscribers.FirstOrDefault(s =>
                        string.Equals(s.SubscriberId, candidate.ProviderSubscriptionId, StringComparison.OrdinalIgnoreCase));

                    if (match is null || !ProviderSubscriberStatusRules.IsProviderSubscriberActive(match.Status))
                    {
                        continue; // no aparece o no está Active (p.ej. Pause By Commerce): no sanar
                    }

                    var providerExpiry = match.ExpiresAtUtc;
                    if (providerExpiry is { } expiry && expiry <= nowUtc)
                    {
                        continue; // expire ya vencido: no sanar
                    }

                    using var scope = _tenantExecutionContextAccessor.BeginScope(candidate.TenantId);

                    // Incidentes base VIVOS del plan: su primer fallo es el ancla de la evidencia.
                    var liveIncidents = await _db.SubscriptionPaymentIncidents
                        .IgnoreQueryFilters()
                        .Where(i =>
                            i.TenantId == candidate.TenantId &&
                            i.Scope == PaymentIncidentScope.BasePlan &&
                            (i.Status == PaymentIncidentStatus.Open || i.Status == PaymentIncidentStatus.GraceExpired) &&
                            i.TilopayRecurringPlanId == recurringPlanId)
                        .ToListAsync(cancellationToken);

                    var evidence = await ResolveRecoveryEvidenceAsync(
                        candidate.TenantId,
                        recurringPlanId,
                        liveIncidents,
                        candidate.LastPaymentFailedAtUtc,
                        candidate.FechaFin,
                        match,
                        nowUtc,
                        cancellationToken);

                    if (evidence is null)
                    {
                        // Active SIN período nuevo (mismo expire que al fallar): la morosidad sigue.
                        // No se cierra el incidente, no se limpia la gracia, no se reactiva.
                        DetachAllTracked();
                        _logger.LogInformation(
                            "Proveedor Active sin evidencia de período nuevo: la recuperación sigue abierta. TenantId {TenantId}. Plan {PlanCode}. ExpireRaw {ExpireRaw}.",
                            candidate.TenantId, candidate.CodigoPlan, match.ExpiresRaw);
                        continue;
                    }
                    var subscription = await _db.Suscripciones
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(s => s.Id == candidate.Id, cancellationToken);

                    if (subscription is null)
                    {
                        continue;
                    }

                    // Reevaluar: pudo cambiar entre la lectura y ahora, o ya no ser recovery.
                    var stillInRecovery =
                        subscription.PaymentRecoveryStatus is "GraceActive" or "GraceExpired" ||
                        subscription.Estado == EstadoSuscripcion.Morosa;
                    if (!stillInRecovery || subscription.CancelAtPeriodEnd)
                    {
                        continue;
                    }

                    subscription.Estado = EstadoSuscripcion.Activa;
                    subscription.PaymentRecoveryStatus = null;
                    subscription.FechaFinGraciaUtc = null;
                    subscription.LastPaymentFailedAtUtc = null;
                    subscription.ProviderStatusRaw = Trim(ProviderSubscriberStatusRules.Sanitize(match.Status), 40);
                    subscription.ProviderStatusLastSyncedUtc = nowUtc;

                    if (providerExpiry is { } expiry2)
                    {
                        subscription.ProviderExpiresAtUtc = expiry2;
                        subscription.ProviderExpiryRaw = match.ExpiresRaw;
                        subscription.ProviderExpiryLastSyncedUtc = nowUtc;
                        var effectiveEndUtc = SubscriptionEffectiveDates.GetEffectiveEndUtc(subscription.FechaFin, expiry2);
                        subscription.FechaFin = effectiveEndUtc;
                        subscription.FechaProximoCobroUtc = effectiveEndUtc;
                    }

                    // Fecha del último pago: la del cobro confirmado si existe; si la evidencia es solo el
                    // expire del proveedor, el cobro ocurrió en TiloPay y se usa la hora de verificación.
                    subscription.FechaUltimoPagoUtc = evidence.ConfirmedPaymentAtUtc ?? nowUtc;
                    subscription.FechaUltimaActualizacionUtc = nowUtc;
                    subscription.MotivoEstado = "Recuperación sanada por reconciliación: evidencia de período nuevo pagado (el webhook success quedó SinRelacion).";

                    foreach (var incident in liveIncidents)
                    {
                        incident.Status = PaymentIncidentStatus.Resolved;
                        incident.ResolvedAtUtc = nowUtc;
                        incident.UpdatedAtUtc = nowUtc;
                    }

                    _db.PlatformAuditLogs.Add(new PlatformAuditLog
                    {
                        Id = Guid.NewGuid(),
                        ActorUserId = "system",
                        ActorEmail = "system",
                        Action = PlatformAuditActions.PaymentRecoveryResolvedByProviderRenewal,
                        EntityType = PlatformAuditEntityTypes.Subscription,
                        EntityId = subscription.Id.ToString(),
                        TenantId = candidate.TenantId,
                        Reason = Trim(
                            $"Recuperación sanada: {evidence.Description}. Proveedor Active, expire {match.ExpiresRaw ?? "-"}. " +
                            $"Fallo ancla {evidence.FailureAnchorUtc:yyyy-MM-dd HH:mm} UTC. Plan {candidate.CodigoPlan}. " +
                            $"Incidentes cerrados {liveIncidents.Count}. SuscriptorSuffix {SensitiveDataMasker.MaskReference(candidate.ProviderSubscriptionId)}.",
                            500),
                        CreatedAtUtc = nowUtc
                    });

                    await _db.SaveChangesAsync(cancellationToken);
                    _accessCache?.Invalidate(candidate.TenantId);
                    report.RecoveredSubscriptionsHealed++;

                    _logger.LogInformation(
                        "Suscripción base sanada por reconciliación (provider Active + renovado). TenantId {TenantId}. Plan {PlanCode}.",
                        candidate.TenantId, candidate.CodigoPlan);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DetachAllTracked();
                    _logger.LogError(
                        ex,
                        "No se pudo sanar la suscripción en recuperación. TenantId {TenantId}. Se continúa.",
                        candidate.TenantId);
                }
            }
        }

        /// <summary>Evidencia objetiva de que la recuperación terminó (período nuevo pagado).</summary>
        private sealed record RecoveryEvidence(DateTime FailureAnchorUtc, DateTime? ConfirmedPaymentAtUtc, string Description);

        /// <summary>
        /// Busca evidencia de un período NUEVO pagado después del fallo. Ancla = primer fallo del ciclo
        /// (incidente vivo más antiguo) → LastPaymentFailedAtUtc → último PagoSuscripcion fallido del
        /// plan. Sin ancla no hay forma de probar la recuperación ⇒ null (no se sana). Evidencias:
        /// (A) PagoSuscripcion Confirmado del plan posterior al ancla, YA aplicado a un período que sigue
        /// vigente con holgura (si el cobro no extendió el período, cerrar la gracia dejaría la cuenta a
        /// horas de cortarse sin gracia: eso lo resuelve el flujo de éxito, no la sanación); (B) expire
        /// del proveedor avanzado (<see cref="PaymentRecoveryRenewalEvidence"/>). Solo lectura; filtrado
        /// explícito por tenant.
        /// </summary>
        private async Task<RecoveryEvidence?> ResolveRecoveryEvidenceAsync(
            Guid tenantId,
            int recurringPlanId,
            IReadOnlyCollection<SubscriptionPaymentIncident> liveIncidents,
            DateTime? lastPaymentFailedAtUtc,
            DateTime? localPeriodEndUtc,
            TilopaySubscriber providerSubscriber,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var anchorUtc = liveIncidents.Count > 0
                ? liveIncidents.Min(i => i.FailureDetectedAtUtc)
                : lastPaymentFailedAtUtc;

            anchorUtc ??= await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p =>
                    p.TenantId == tenantId &&
                    p.TilopayRecurringPlanId == recurringPlanId &&
                    p.Estado == EstadoPagoProveedor.Fallido)
                .MaxAsync(p => (DateTime?)(p.FechaActualizacionUtc ?? p.FechaCreacionUtc), cancellationToken);

            if (anchorUtc is not { } anchor)
            {
                return null;
            }

            var confirmedAfterFailureUtc = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p =>
                    p.TenantId == tenantId &&
                    p.TilopayRecurringPlanId == recurringPlanId &&
                    p.Estado == EstadoPagoProveedor.Confirmado &&
                    p.FechaConfirmacionUtc > anchor)
                .MaxAsync(p => p.FechaConfirmacionUtc, cancellationToken);

            var effectiveEndUtc = SubscriptionEffectiveDates.GetEffectiveEndUtc(localPeriodEndUtc, providerSubscriber.ExpiresAtUtc);
            if (confirmedAfterFailureUtc is not null &&
                effectiveEndUtc is { } end &&
                end > nowUtc + PaymentRecoveryRenewalEvidence.MinimumPeriodAdvance)
            {
                return new RecoveryEvidence(anchor, confirmedAfterFailureUtc, "pago confirmado posterior al fallo");
            }

            if (PaymentRecoveryRenewalEvidence.ProviderShowsNewPaidPeriod(
                    providerSubscriber.Status,
                    providerSubscriber.ExpiresAtUtc,
                    anchor))
            {
                return new RecoveryEvidence(anchor, null, "expire del proveedor avanzó a un período nuevo");
            }

            return null;
        }

        // ── Reconciliación de eventos success huérfanos (trazabilidad financiera) ─────

        /// <summary>
        /// Cierra la traza financiera de un repeat_payment_success que quedó SinRelacion: un cobro
        /// real por url_renew (renovación/regularización de un suscriptor EXISTENTE, sin pending local)
        /// que el webhook no supo correlacionar. Si el proveedor confirma que el ÚNICO suscriptor
        /// local ACTIVO de ese plan recurrente está Active y renovado más allá de la fecha del cobro,
        /// se marca el evento ReconciliadoPorProveedor y —si no existía— se registra el PagoSuscripcion
        /// Confirmado del cobro (para que el ingreso quede auditado localmente). NUNCA toca fechas de la
        /// suscripción (no extiende: la renovación ya la aplicó el proveedor / la sanación) y es
        /// idempotente por transactionId. Verifica SIEMPRE contra getSuscriptorRepeat (un estado local
        /// nunca basta). Ambiguo (0 o &gt;1 suscriptor local activo del plan) ⇒ no se toca, porque no se
        /// puede atribuir el cobro huérfano con seguridad sin el id_suscriptor/correo (redactado en el
        /// payload) del evento. HTTP fuera de transacción.
        /// </summary>
        private async Task ReconcileOrphanedRenewalSuccessEventsAsync(
            BillingReconciliationReport report,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            if (_adminService is null || !_adminService.IsEnabled)
            {
                return; // sin verificación contra el proveedor no se reconcilia a ciegas
            }

            // Candidatos: eventos success no procesados que quedaron SinRelacion con datos suficientes
            // para verificar (plan recurrente, transacción y monto). El tipo se filtra en memoria.
            var candidates = await _db.EventosPago
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(e =>
                    !e.Procesado &&
                    e.EstadoProcesamiento == "SinRelacion" &&
                    e.TilopayRecurringPlanId != null &&
                    e.ProviderTransactionId != null &&
                    e.Monto != null)
                .Select(e => new
                {
                    e.Id,
                    e.Tipo,
                    e.TilopayRecurringPlanId,
                    e.ProviderTransactionId,
                    e.ProviderSubscriberId,
                    e.Monto,
                    e.Moneda,
                    e.ReferenciaExterna,
                    e.FechaRecepcionUtc
                })
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsRecurringPaymentSuccessEventType(candidate.Tipo) ||
                    candidate.TilopayRecurringPlanId is not { } recurringPlanId ||
                    string.IsNullOrWhiteSpace(candidate.ProviderTransactionId))
                {
                    continue;
                }

                DetachAllTracked();

                try
                {
                    // Único suscriptor local ACTIVO de ese plan recurrente (base), con id de proveedor.
                    // 0 o >1 ⇒ no se puede atribuir el cobro huérfano con seguridad ⇒ no se toca.
                    var localMatches = await _db.Suscripciones
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(s =>
                            s.Proveedor == PaymentProviderType.Tilopay &&
                            s.TilopayRecurringPlanId == recurringPlanId &&
                            s.ProviderSubscriptionId != null &&
                            s.Estado == EstadoSuscripcion.Activa)
                        .Select(s => new { s.Id, s.TenantId, s.PlanId, s.ProviderSubscriptionId, s.CodigoPlan })
                        .Take(2)
                        .ToListAsync(cancellationToken);

                    if (localMatches.Count != 1)
                    {
                        continue;
                    }

                    var local = localMatches[0];
                    if (local.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(local.ProviderSubscriptionId))
                    {
                        continue;
                    }

                    // Verificación en el proveedor: el suscriptor ligado a la suscripción local debe
                    // estar Active con expire posterior al cobro (prueba de que ESTE cobro lo renovó).
                    var subscribers = await _adminService.GetSuscriptorRepeatAsync(recurringPlanId, cancellationToken);
                    var match = subscribers.FirstOrDefault(s =>
                        string.Equals(s.SubscriberId, local.ProviderSubscriptionId, StringComparison.OrdinalIgnoreCase));

                    if (match is null || !ProviderSubscriberStatusRules.IsProviderSubscriberActive(match.Status))
                    {
                        continue; // no aparece o no está Active: no atribuir el cobro
                    }

                    // Si viene expire, debe cubrir la fecha del cobro. Sin expire, basta el Active verificado.
                    if (match.ExpiresAtUtc is { } expiry && expiry <= candidate.FechaRecepcionUtc)
                    {
                        continue;
                    }

                    await ReconcileOneOrphanedEventAsync(
                        local.TenantId,
                        candidate.Id,
                        local.PlanId,
                        local.ProviderSubscriptionId!,
                        local.CodigoPlan,
                        recurringPlanId,
                        candidate.ProviderTransactionId!,
                        candidate.ProviderSubscriberId,
                        candidate.Monto ?? 0m,
                        candidate.Moneda,
                        candidate.ReferenciaExterna,
                        candidate.FechaRecepcionUtc,
                        match.ExpiresRaw,
                        report,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DetachAllTracked();
                    _logger.LogError(
                        ex,
                        "No se pudo reconciliar el evento success huérfano {EventId}; se continúa.",
                        candidate.Id);
                }
            }
        }

        private async Task ReconcileOneOrphanedEventAsync(
            Guid tenantId,
            Guid eventId,
            Guid planId,
            string providerSubscriptionId,
            string? planCode,
            int recurringPlanId,
            string providerTransactionId,
            string? eventProviderSubscriberId,
            decimal amount,
            string? currency,
            string? providerReference,
            DateTime chargeReceivedUtc,
            string? providerExpiryRaw,
            BillingReconciliationReport report,
            CancellationToken cancellationToken)
        {
            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);

            // Reevaluar el evento bajo el registro tracked: pudo procesarse entre la lectura y ahora.
            var evento = await _db.EventosPago
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);

            if (evento is null || evento.Procesado || evento.EstadoProcesamiento != "SinRelacion")
            {
                return; // idempotente: ya reconciliado/procesado por otra vía
            }

            var nowUtc = GetUtcNow();

            // Idempotencia del PAGO: si ya existe un PagoSuscripcion con ese transactionId, se reutiliza
            // (nunca se duplica). Si no, se registra uno Confirmado para que el ingreso quede auditado.
            // Se crea Confirmado a propósito: un replay futuro de ese transactionId lo detecta la guarda
            // de idempotencia del webhook (MatchesConfirmedAttempt ⇒ "Duplicado"), sin extender de nuevo.
            var existingPayment = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    p => p.TenantId == tenantId &&
                         p.Proveedor == PaymentProviderType.Tilopay &&
                         p.ProviderTransactionId == providerTransactionId,
                    cancellationToken);

            var paymentWasCreated = false;
            if (existingPayment is null)
            {
                existingPayment = new PagoSuscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PlanId = planId,
                    Proveedor = PaymentProviderType.Tilopay,
                    Estado = EstadoPagoProveedor.Confirmado,
                    ReferenciaInterna = $"RECON-{Guid.NewGuid():N}"[..20],
                    ProviderReference = string.IsNullOrWhiteSpace(providerReference) ? providerTransactionId : providerReference,
                    ProviderTransactionId = providerTransactionId,
                    TilopayRecurringPlanId = recurringPlanId,
                    ProviderSubscriberId = string.IsNullOrWhiteSpace(eventProviderSubscriberId)
                        ? providerSubscriptionId
                        : eventProviderSubscriberId,
                    Descripcion = "Renovacion recurrente reconciliada (webhook success SinRelacion).",
                    Monto = amount,
                    Moneda = string.IsNullOrWhiteSpace(currency) ? "CRC" : currency,
                    ProviderResultCode = "1",
                    ProviderResultMessage = Trim(
                        "Cobro recurrente reconciliado por la reconciliación: el proveedor confirmó el suscriptor Active y renovado. El webhook success había quedado SinRelacion.",
                        300),
                    FechaCreacionUtc = chargeReceivedUtc,
                    FechaConfirmacionUtc = chargeReceivedUtc,
                    FechaActualizacionUtc = nowUtc
                };
                _db.PagosSuscripcion.Add(existingPayment);
                paymentWasCreated = true;
            }

            evento.Procesado = true;
            evento.EstadoProcesamiento = "ReconciliadoPorProveedor";
            evento.TenantId = tenantId;
            evento.PlanId = planId;
            evento.PagoSuscripcionId = existingPayment.Id;
            evento.ProviderSubscriberId = string.IsNullOrWhiteSpace(evento.ProviderSubscriberId)
                ? providerSubscriptionId
                : evento.ProviderSubscriberId;
            evento.FechaProcesamientoUtc = nowUtc;
            evento.Error = null;

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.PaymentEventReconciledByProviderRenewal,
                EntityType = PlatformAuditEntityTypes.Billing,
                EntityId = eventId.ToString(),
                TenantId = tenantId,
                Reason = Trim(
                    $"Evento success SinRelacion reconciliado contra la suscripción renovada. Plan {planCode}. " +
                    $"TxnSuffix {SensitiveDataMasker.MaskReference(providerTransactionId)}. " +
                    $"SuscriptorSuffix {SensitiveDataMasker.MaskReference(providerSubscriptionId)}. Expire {providerExpiryRaw ?? "-"}. " +
                    $"Monto {amount:0.00} {(string.IsNullOrWhiteSpace(currency) ? "CRC" : currency)}. " +
                    (paymentWasCreated
                        ? "Se registró el PagoSuscripcion Confirmado del cobro."
                        : "El PagoSuscripcion ya existía; solo se ligó el evento."),
                    500),
                CreatedAtUtc = nowUtc
            });

            await _db.SaveChangesAsync(cancellationToken);
            DetachAllTracked();

            report.RenewalSuccessEventsReconciled++;

            _logger.LogInformation(
                "Evento success huérfano reconciliado. TenantId {TenantId}. EventId {EventId}. PaymentCreated {PaymentCreated}. Plan {PlanCode}.",
                tenantId, eventId, paymentWasCreated, planCode);
        }

        /// <summary>Tipos de evento que representan un pago recurrente EXITOSO (espeja SaaSPaymentService).</summary>
        private static bool IsRecurringPaymentSuccessEventType(string? eventType)
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                return false;
            }

            var normalized = eventType.Trim().ToLowerInvariant().Replace('-', '_').Replace('.', '_');
            return normalized is "repeat_payment_success" or "repeat_payment_paid";
        }

        private async Task<bool> TryAddAlertAsync(
            BillingReconciliationReport report,
            string entityType,
            string entityId,
            Guid? tenantId,
            string reason,
            CancellationToken cancellationToken)
        {
            var cooldownCutoffUtc = GetUtcNow().AddHours(-Math.Max(1, _options.AlertCooldownHours));

            var alreadyAlerted = await _db.PlatformAuditLogs.AnyAsync(
                log =>
                    log.Action == PlatformAuditActions.BillingReconciliationAlert &&
                    log.EntityId == entityId &&
                    log.CreatedAtUtc >= cooldownCutoffUtc,
                cancellationToken);

            if (alreadyAlerted)
            {
                report.AlertsSuppressedByCooldown++;
                return false;
            }

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.BillingReconciliationAlert,
                EntityType = entityType,
                EntityId = entityId,
                TenantId = tenantId,
                Reason = reason.Length <= 500 ? reason : reason[..500],
                CreatedAtUtc = GetUtcNow()
            });

            // Persistir la alerta de INMEDIATO: es un PlatformAuditLog (NO ITenantEntity), así que
            // guardarlo solo nunca mezcla tenants; y así el aislamiento de fases (DetachAllTracked)
            // no puede descartar alertas aún no guardadas.
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Alerta de reconciliación Billing. EntityType {EntityType}. EntityId {EntityId}. TenantId {TenantId}. Reason {Reason}.",
                entityType,
                entityId,
                tenantId,
                reason);

            return true;
        }

        private DateTime GetUtcNow() => _clock.NowOffset().UtcDateTime;
    }
}
