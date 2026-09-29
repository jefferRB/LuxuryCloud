using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Security;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Fotografía operativa del módulo Billing para diagnóstico sin acceso directo a la BD.
    /// Solo lectura, cross-tenant, consumida únicamente por la consola de plataforma.
    /// </summary>
    public sealed record BillingHealthSnapshot
    {
        public DateTime GeneratedAtUtc { get; init; }

        // Suscripciones base por estado EFECTIVO (calculado con fechas, no el almacenado).
        public int ActiveSubscriptions { get; init; }
        public int TrialSubscriptions { get; init; }
        public int MorosaSubscriptions { get; init; }
        public int SuspendedSubscriptions { get; init; }
        public int PendingSubscriptions { get; init; }
        public int CancelledSubscriptions { get; init; }
        public int FailedSubscriptions { get; init; }
        public int ActiveWhatsAppAddons { get; init; }

        // Pagos.
        public int PendingPayments { get; init; }
        public int ManualReviewPayments { get; init; }
        public int ConfirmedPaymentsLast24h { get; init; }
        public int FailedPaymentsLast24h { get; init; }

        // Renovaciones.
        public int OverdueRenewals { get; init; }

        // Webhooks / eventos.
        public int UnprocessedEvents { get; init; }
        public int ErrorEventsLast24h { get; init; }
        public int ManualReviewEventsLast7d { get; init; }
        public int UnmatchedEventsLast7d { get; init; }
        public DateTime? LastWebhookReceivedUtc { get; init; }
        public DateTime? LastWebhookProcessedUtc { get; init; }
        public double? AvgWebhookProcessingMsLast24h { get; init; }

        // Reconciliación y alertas.
        public DateTime? LastReconciliationUtc { get; init; }
        public string? LastReconciliationSummaryJson { get; init; }
        public int OpenAlertsLast24h { get; init; }
        public int AutoRepairsLast7d { get; init; }

        // Suscriptor recurrente (TiloPay Repeat Admin).
        public int ActiveSubscriptionsWithoutSubscriberId { get; init; }
        public int ConfirmedPaymentsWithoutSubscriberId { get; init; }
        public int SubscriberResolutionsPendingLast7d { get; init; }
        public int SubscriberResolutionsAmbiguousLast7d { get; init; }
        public int SubscriberResolvedLast7d { get; init; }
        public int CheckoutsBlockedByDuplicateLast7d { get; init; }
        public int ProviderCancellationsFailedLast7d { get; init; }
        public DateTime? LastSuccessfulSubscriberResolutionUtc { get; init; }

        // Cambio de plan base (estrategia B).

        /// <summary>
        /// Todos los cambios abiertos. OJO: no todo pending es riesgo. Se conserva por compatibilidad;
        /// para saber si hay que preocuparse, mirar <see cref="PlanChangeMoneyRiskCount"/>.
        /// </summary>
        public int PlanChangePendingCount { get; init; }

        public int PlanChangeManualReviewCount { get; init; }
        public DateTime? LastSuccessfulPlanChangeUtc { get; init; }

        /// <summary>
        /// Checkouts de cambio abiertos SIN dinero detrás: el cliente abrió el link y no pagó.
        /// Es ruido esperado, no un hallazgo; se expiran solos por antigüedad.
        /// </summary>
        public int PlanChangePendingCheckoutCount { get; init; }

        /// <summary>
        /// Lo que SÍ es riesgo de dinero: pagos confirmados esperando aplicarse + suscriptores
        /// viejos sin cancelar. Este es el número que importa: si es 0, no hay nada que revisar.
        /// </summary>
        public int PlanChangeMoneyRiskCount { get; init; }

        /// <summary>Checkout abandonado más antiguo: si envejece más que la ventana, la expiración no está corriendo.</summary>
        public DateTime? OldestPendingCheckoutUtc { get; init; }

        public double? OldestPendingCheckoutAgeHours { get; init; }

        // ── Fecha del proveedor vs fecha local (riesgo de morosidad/fecha incorrecta) ──
        // Señal DISTINTA del doble cobro: aquí el riesgo es marcar moroso o cortar acceso a
        // destiempo, no cobrar dos veces.

        /// <summary>Suscripciones activas donde local y proveedor difieren de forma significativa (cualquier dirección).</summary>
        public int ProviderExpiryMismatchCount { get; init; }

        /// <summary>Suscripciones donde el proveedor cobra MÁS TARDE que lo local (vigencia a extender / ya extendida).</summary>
        public int ActiveSubscriptionsProviderExpiryAheadCount { get; init; }

        /// <summary>Suscripciones donde el proveedor cobra MÁS TEMPRANO que lo local (posible corte injusto).</summary>
        public int ActiveSubscriptionsProviderExpiryEarlierCount { get; init; }

        /// <summary>Extensiones de vigencia a la fecha del proveedor aplicadas en los últimos 7 días.</summary>
        public int ProviderExpiryReconciledLast7d { get; init; }

        // ── Ciclo de vida: cancelación programada, pausa y reactivación ──

        /// <summary>Suscripciones con la renovación cancelada (CancelAtPeriodEnd) aún con acceso vigente.</summary>
        public int SubscriptionsCancelAtPeriodEnd { get; init; }

        /// <summary>CancelAtPeriodEnd cuya baja en el proveedor NO quedó verificada (riesgo de seguir cobrando).</summary>
        public int ProviderCancellationsPendingVerification { get; init; }

        /// <summary>Suscripciones marcadas como pausadas en el proveedor (ProviderPausedAtUtc no nulo).</summary>
        public int ProviderPausedSubscriptions { get; init; }

        /// <summary>Cancelaciones que quedaron en revisión manual (baja no verificada) en los últimos 7 días.</summary>
        public int ProviderCancellationFailedLast7d { get; init; }

        /// <summary>Pausas que quedaron en revisión manual (no verificadas) en los últimos 7 días.</summary>
        public int PauseFailedLast7d { get; init; }

        /// <summary>Reactivaciones que quedaron en revisión manual (no verificadas/bloqueadas) en los últimos 7 días.</summary>
        public int ReactivationFailedLast7d { get; init; }

        /// <summary>Desajustes local↔proveedor de ciclo de vida detectados por la reconciliación en los últimos 7 días.</summary>
        public int ProviderStatusMismatchCount { get; init; }

        // ── Recuperación de pago (pago fallido / gracia / suspensión / tarjeta) ──

        /// <summary>Incidentes de recuperación de pago abiertos (pago fallido en curso).</summary>
        public int OpenPaymentRecoveryIncidents { get; init; }

        /// <summary>Incidentes abiertos con la gracia todavía vigente (acceso preservado).</summary>
        public int ActiveGracePeriods { get; init; }

        /// <summary>Gracias vencidas SIN suspensión (dry-run, AutoSuspendAfterGrace=false): requieren atención.</summary>
        public int GraceExpiredNotSuspended { get; init; }

        /// <summary>Suscripciones suspendidas por impago (AutoSuspendAfterGrace=true).</summary>
        public int SuspendedForNonPayment { get; init; }

        /// <summary>Incidentes que no se pudieron correlacionar/decidir y quedaron en revisión manual.</summary>
        public int PaymentRecoveryManualReviewCount { get; init; }

        /// <summary>Notificaciones de recuperación de pago que fallaron en los últimos 7 días.</summary>
        public int PaymentRecoveryNotificationsFailedLast7d { get; init; }

        /// <summary>Fallos al generar la URL de actualización de método de pago en los últimos 7 días.</summary>
        public int PaymentMethodUpdateUrlFailuresLast7d { get; init; }

        /// <summary>Detalle (acotado) de suscripciones con local ≠ proveedor, para que soporte lo vea.</summary>
        public IReadOnlyList<ProviderExpiryMismatchItem> ProviderExpiryMismatches { get; init; } =
            Array.Empty<ProviderExpiryMismatchItem>();

        // ── Cancelación del suscriptor viejo tras cambio de plan (riesgo de DOBLE COBRO) ──
        // Cada intent aquí es un cliente que puede estar pagando dos suscripciones a la vez.

        /// <summary>Cambios aplicados cuyo suscriptor viejo sigue sin cancelarse. Mismo conjunto que PlanChangeManualReviewCount, con el nombre de la operación.</summary>
        public int OldCancellationPendingCount { get; init; }

        /// <summary>El próximo intento real más cercano entre los pendientes. NULL = hay al menos uno elegible YA.</summary>
        public DateTime? OldCancellationNextRetryUtc { get; init; }

        /// <summary>Pendientes que ahora mismo están esperando su ventana de backoff.</summary>
        public int OldCancellationBackoffBlockedCount { get; init; }

        /// <summary>Skips por AutoCancel apagado en las últimas 24h: nadie está cancelando esos viejos.</summary>
        public int OldCancellationSkippedAutoCancelDisabledCount { get; init; }

        /// <summary>Pendientes saltados por datos incompletos en 24h: no se pueden intentar sin intervención.</summary>
        public int OldCancellationSkippedNotEligibleCount { get; init; }

        /// <summary>
        /// CRÍTICO: pendientes en los que la verificación contra TiloPay mostró el viejo TODAVÍA
        /// Activo en las últimas 24h. Se deriva de la auditoría de verificación (evidencia real
        /// de getSuscriptorRepeat) y no de una llamada en vivo: este health lo abre un humano y no
        /// debe golpear el API del proveedor una vez por intent en cada carga de página.
        /// </summary>
        public int OldCancellationVerifiedStillActiveCount { get; init; }

        /// <summary>Intentos reales acumulados del pendiente más castigado: si crece, la baja automática no está funcionando.</summary>
        public int OldCancellationMaxAttemptCount { get; init; }

        /// <summary>Detalle de los pendientes (acotado) para que soporte pueda actuar por intent.</summary>
        public IReadOnlyList<OldCancellationPendingItem> OldCancellationPendingItems { get; init; } =
            Array.Empty<OldCancellationPendingItem>();

        // ── Add-on de WhatsApp (sección SEPARADA: su dinero-en-riesgo NO se mezcla con el del base) ──

        /// <summary>Add-ons de WhatsApp ACTIVOS con el plan base cancelado/vencido (regla 11: no deben seguir cobrando).</summary>
        public int WhatsAppAddonsWithoutActiveBase { get; init; }

        /// <summary>Suscriptores de add-on pendientes de baja en TiloPay (Strategy B / cascada / manual): riesgo de DOBLE COBRO del add-on.</summary>
        public int WhatsAppAddonsPendingProviderCancellation { get; init; }

        /// <summary>Tenants con más de un add-on ACTIVO. El índice único por TenantId lo impide; debe ser 0 siempre (guarda).</summary>
        public int WhatsAppAddonsDoubleActiveTenants { get; init; }

        /// <summary>Incidentes de recuperación de pago del ADD-ON abiertos (pago del add-on fallido en curso). Separado del base.</summary>
        public int WhatsAppAddonOpenPaymentIncidents { get; init; }

        /// <summary>El número del add-on que importa: pendientes de cancelación + doble activo LOCAL + doble activo en el PROVEEDOR. Si es 0, no hay dinero-en-riesgo de add-on.</summary>
        public int WhatsAppAddonMoneyRiskCount { get; init; }

        // ── Estado REAL del proveedor (última auditoría contra getSuscriptorRepeat) ──
        // El estado local no puede ver un doble suscriptor en TiloPay: eso deja al tablero en falso
        // verde. Estas métricas vienen del snapshot que escriben el sondeo post-rechazo del webhook
        // y la reconciliación; se leen de BD, sin pegarle a TiloPay al cargar la pantalla.

        /// <summary>Tenants con MÁS DE UN suscriptor de add-on COBRABLE en TiloPay. Doble cobro real o inminente: nunca puede coexistir con "riesgo 0".</summary>
        public int ProviderDoubleActiveWhatsAppAddonTenants { get; init; }

        /// <summary>Tenants cuya última auditoría del proveedor NO fue concluyente: no se puede afirmar que estén sanos.</summary>
        public int ProviderWhatsAppAddonAuditInconclusiveTenants { get; init; }

        /// <summary>Máximo de suscriptores de add-on cobrables en un solo tenant según el último sondeo (debe ser 1).</summary>
        public int ProviderMaxActiveAddonSubscribersByTenant { get; init; }

        /// <summary>Cuándo se auditó el proveedor por última vez. Null = nunca se preguntó: el verde no está verificado.</summary>
        public DateTime? LastProviderAddonAuditUtc { get; init; }

        /// <summary>
        /// AVISO INFORMATIVO (Opción A), NO riesgo de dinero: paquete comercial de WhatsApp activo
        /// pero SIN configuración técnica persistida (no existe TenantWhatsAppSettings). El cobro es
        /// correcto; solo falta que el cliente entre a "Configurar WhatsApp". No se envían mensajes.
        /// </summary>
        public int WhatsAppAddonsActiveWithoutConfiguration { get; init; }

        /// <summary>
        /// AVISO OPERATIVO (no riesgo de dinero): configuración de WhatsApp habilitada (IsEnabled)
        /// pero SIN un add-on comercial vigente. El envío queda bloqueado por el gate de entitlement
        /// (NoActiveWhatsAppAddon): el tenant configuró pero no tiene (o venció) el paquete.
        /// </summary>
        public int WhatsAppSettingsEnabledWithoutActiveAddon { get; init; }

        // ── Clasificación por fuente del add-on (rule 10): dinero vs informativo vs operativo ──

        /// <summary>RIESGO DE DINERO: add-ons ProviderRecurring ACTIVOS, recurrentes, SIN ProviderSubscriptionId.</summary>
        public int PaidAddonsActiveWithoutProviderRisk { get; init; }

        /// <summary>INFORMATIVO: accesos manuales (cortesía/canje/interno) vigentes. No es dinero.</summary>
        public int ManualWhatsAppGrantsActive { get; init; }

        /// <summary>ALERTA OPERATIVA: accesos manuales VENCIDOS pero con la fila aún activa (no envían, no cobran).</summary>
        public int ManualWhatsAppGrantsExpiredStillActive { get; init; }

        /// <summary>INFORMATIVO/LIMPIEZA: add-ons legacy/test con Estado activo (nunca son entitlement efectivo).</summary>
        public int LegacyWhatsAppAddonsActive { get; init; }

        /// <summary>ALERTA OPERATIVA: settings habilitados sin entitlement comercial EFECTIVO (envíos bloqueados).</summary>
        public int WhatsAppSettingsEnabledWithoutEffectiveEntitlement { get; init; }

        /// <summary>
        /// Eventos de pago recurrente (success) que estaban SinRelacion y la reconciliación los cerró
        /// contra la suscripción ya renovada por el proveedor (trazabilidad financiera), últimos 7 días.
        /// </summary>
        public int RenewalSuccessEventsReconciledLast7d { get; init; }

        /// <summary>
        /// Config EFECTIVA de checkout por add-on (appsettings + env vars ya resueltas): HasCheckoutUrl
        /// enmascarado. Si algún add-on tiene HasCheckoutUrl=false, no se puede vender por checkout
        /// recurrente hasta cargar el hosted link real en TiloPay.
        /// </summary>
        public IReadOnlyList<ManagedPlanCheckoutStatus> WhatsAppAddonCheckoutConfig { get; init; } =
            Array.Empty<ManagedPlanCheckoutStatus>();
    }

    /// <summary>
    /// Un cambio de plan aplicado cuyo suscriptor viejo sigue vivo en TiloPay. Cada fila es un
    /// riesgo de doble cobro concreto. Los id_suscriptor van enmascarados: esta vista la abre un
    /// humano y no necesita el id completo para identificar el caso.
    /// </summary>
    public sealed record OldCancellationPendingItem
    {
        public required Guid IntentId { get; init; }
        public required Guid TenantId { get; init; }
        public string? TenantName { get; init; }
        public string? ToPlanCode { get; init; }
        public int AttemptCount { get; init; }
        public DateTime? NextRetryUtc { get; init; }
        public DateTime? AppliedAtUtc { get; init; }
        public string? OldSubscriberSuffix { get; init; }
        public string? NewSubscriberSuffix { get; init; }
        public int? OldRecurringPlanId { get; init; }

        /// <summary>La verificación contra TiloPay mostró el viejo todavía Activo en las últimas 24h.</summary>
        public bool VerifiedStillActive { get; init; }
    }

    /// <summary>Una suscripción activa cuya fecha local difiere de la real del proveedor.</summary>
    public sealed record ProviderExpiryMismatchItem
    {
        public required Guid TenantId { get; init; }
        public string? TenantName { get; init; }
        public string? PlanCode { get; init; }
        public DateTime? LocalEndUtc { get; init; }
        public DateTime? ProviderExpiresAtUtc { get; init; }

        /// <summary>Fechas de calendario Tica para mostrar (no el UTC crudo, que corre un día).</summary>
        public string? LocalEndDisplay { get; init; }
        public string? ProviderExpiryDisplay { get; init; }

        /// <summary>True si el proveedor va por delante (extiende), false si va por detrás (posible corte).</summary>
        public bool ProviderIsAhead { get; init; }

        public DateTime? LastSyncedUtc { get; init; }
    }

    public interface IBillingHealthService
    {
        Task<BillingHealthSnapshot> BuildAsync(CancellationToken cancellationToken = default);
    }

    public sealed class BillingHealthService : IBillingHealthService
    {
        /// <summary>
        /// Tolerancia para contar un desajuste local↔proveedor. Constante (no opción inyectada)
        /// porque es un contador de diagnóstico: alinearlo con el default de conciliación (12h)
        /// basta y evita romper todas las construcciones de este servicio en tests.
        /// </summary>
        private const int ProviderExpiryMismatchToleranceHours = 12;

        private readonly ApplicationDbContext _db;
        private readonly SuscripcionService _suscripcionService;
        // Opcional (patrón del módulo): DI lo inyecta en producción; en tests con ctor mínimo queda
        // null y la config de checkout sale vacía (no rompe las construcciones existentes).
        private readonly IManagedPlanCheckoutInspector? _checkoutInspector;

        public BillingHealthService(
            ApplicationDbContext db,
            SuscripcionService suscripcionService,
            IManagedPlanCheckoutInspector? checkoutInspector = null)
        {
            _db = db;
            _suscripcionService = suscripcionService;
            _checkoutInspector = checkoutInspector;
        }

        public async Task<BillingHealthSnapshot> BuildAsync(CancellationToken cancellationToken = default)
        {
            var nowUtc = DateTime.UtcNow;
            var last24hUtc = nowUtc.AddHours(-24);
            var last7dUtc = nowUtc.AddDays(-7);

            // Estado efectivo: el volumen de suscripciones es bajo (una por tenant), por lo que
            // proyectarlas y clasificarlas en memoria con la MISMA regla del resolver evita
            // divergencias entre el health check y el control de acceso real.
            var subscriptions = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Select(s => new Suscripcion
                {
                    Id = s.Id,
                    TenantId = s.TenantId,
                    PlanId = s.PlanId,
                    Estado = s.Estado,
                    FechaFin = s.FechaFin,
                    FechaTrialFin = s.FechaTrialFin,
                    FechaFinGraciaUtc = s.FechaFinGraciaUtc
                })
                .ToListAsync(cancellationToken);

            int active = 0, trial = 0, morosa = 0, suspended = 0, pending = 0, cancelled = 0, failed = 0;
            foreach (var subscription in subscriptions)
            {
                switch (_suscripcionService.GetEffectiveStatus(subscription))
                {
                    case EstadoSuscripcion.Activa: active++; break;
                    case EstadoSuscripcion.Trial: trial++; break;
                    case EstadoSuscripcion.Morosa: morosa++; break;
                    case EstadoSuscripcion.Suspendida: suspended++; break;
                    case EstadoSuscripcion.Pendiente: pending++; break;
                    case EstadoSuscripcion.Cancelada: cancelled++; break;
                    case EstadoSuscripcion.Fallida: failed++; break;
                }
            }

            var addons = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            // Clasificación por fuente (ÚNICA fuente de verdad, sin strings mágicos). Separa el riesgo
            // real de dinero (ProviderRecurring activo sin provider sub) de los accesos manuales/legacy.
            var classifiedAddons = addons
                .Select(addon => new { Addon = addon, Entitlement = _suscripcionService.ResolveWhatsAppEntitlement(addon) })
                .ToList();
            var activeAddonList = classifiedAddons.Where(x => x.Entitlement.IsEffective).Select(x => x.Addon).ToList();
            var activeAddons = activeAddonList.Count;

            // ── Add-ons por fuente (rule 10): dinero vs informativo vs operativo ──
            // RIESGO DE DINERO: recurrente pagado activo pero sin ProviderSubscriptionId.
            var paidAddonsProviderRisk = classifiedAddons.Count(x => x.Entitlement.IsProviderRisk);
            // Informativo: accesos manuales vigentes (cortesía/canje/interno).
            var manualGrantsActive = classifiedAddons.Count(x => x.Entitlement.IsManualGrant && x.Entitlement.IsEffective);
            // Alerta operativa: acceso manual VENCIDO pero la fila sigue Activa (no envía, no cobra).
            var manualGrantsExpiredStillActive = classifiedAddons.Count(x => x.Entitlement.IsManualGrantExpired);
            // Informativo/limpieza: add-ons legacy/test con Estado activo (nunca son entitlement efectivo).
            var legacyAddonsActive = classifiedAddons.Count(x =>
                x.Entitlement.IsLegacy && x.Addon.Estado == EstadoSuscripcion.Activa);
            var effectiveEntitlementTenantIds = classifiedAddons
                .Where(x => x.Entitlement.IsEffective)
                .Select(x => x.Addon.TenantId)
                .ToHashSet();

            // ── Add-on: métricas propias (sección separada del dinero-en-riesgo del base) ──
            var addonPendingProviderCancellation = addons.Count(addon =>
                addon.ProviderCancellation == ProviderCancellationState.PendingManualCancellation &&
                addon.PendingCancellationProviderSubscriptionId != null);

            // El índice único por TenantId lo impide estructuralmente; se cuenta como guarda (debe ser 0).
            var addonDoubleActiveTenants = activeAddonList
                .GroupBy(addon => addon.TenantId)
                .Count(group => group.Count() > 1);

            // Add-on activo sin plan base con acceso (regla 11). Se recomputa el base de esos tenants
            // con TODOS los campos que necesita CanAccessApp (la proyección base de arriba es mínima).
            var addonWithoutActiveBase = 0;
            var addonTenantIds = activeAddonList.Select(addon => addon.TenantId).Distinct().ToList();
            if (addonTenantIds.Count > 0)
            {
                var baseForAddons = await _db.Suscripciones
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(s => addonTenantIds.Contains(s.TenantId))
                    .Select(s => new Suscripcion
                    {
                        Id = s.Id,
                        TenantId = s.TenantId,
                        Estado = s.Estado,
                        CodigoPlan = s.CodigoPlan,
                        FechaInicio = s.FechaInicio,
                        FechaFin = s.FechaFin,
                        FechaTrialFin = s.FechaTrialFin,
                        FechaFinGraciaUtc = s.FechaFinGraciaUtc,
                        ProviderExpiresAtUtc = s.ProviderExpiresAtUtc,
                        CancelAtPeriodEnd = s.CancelAtPeriodEnd,
                        CancellationEffectiveAtUtc = s.CancellationEffectiveAtUtc,
                        FechaUltimaActualizacionUtc = s.FechaUltimaActualizacionUtc
                    })
                    .ToListAsync(cancellationToken);

                var baseByTenant = baseForAddons
                    .GroupBy(s => s.TenantId)
                    .ToDictionary(
                        group => group.Key,
                        group => group
                            .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                            .First());

                // El acceso base NO siempre viene de una fila de Suscripciones: un tenant exento o
                // interno con plan forzado (p. ej. WA1200 ManualGrant + base exento) tiene acceso
                // legitimo y no tiene suscripcion. Antes se contaba como "add-on sin plan base" y
                // producia una alerta falsa. Se consulta el modo comercial explicitamente.
                var platformGrantedTenants = (await _db.Tenants
                        .AsNoTracking()
                        .Where(tenant =>
                            addonTenantIds.Contains(tenant.Id) &&
                            tenant.Activo &&
                            tenant.ForcedPlanId != null &&
                            (tenant.CommercialAccessMode == TenantCommercialAccessMode.Exempt ||
                             tenant.CommercialAccessMode == TenantCommercialAccessMode.Internal))
                        .Select(tenant => tenant.Id)
                        .ToListAsync(cancellationToken))
                    .ToHashSet();

                foreach (var addonTenantId in addonTenantIds)
                {
                    if (platformGrantedTenants.Contains(addonTenantId))
                    {
                        // Acceso base otorgado por plataforma: ni riesgo de dinero ni alerta operativa.
                        continue;
                    }

                    baseByTenant.TryGetValue(addonTenantId, out var baseSubscription);
                    var basePlanCode = baseSubscription?.CodigoPlan;
                    var isRealBase = baseSubscription is not null &&
                        (string.IsNullOrWhiteSpace(basePlanCode) ||
                         !PlanCodes.WhatsAppAddons.Contains(basePlanCode, StringComparer.OrdinalIgnoreCase));
                    var hasActiveBase = baseSubscription is not null && isRealBase &&
                                        _suscripcionService.CanAccessApp(baseSubscription);
                    if (!hasActiveBase)
                    {
                        addonWithoutActiveBase++;
                    }
                }
            }

            var addonOpenPaymentIncidents = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .CountAsync(incident =>
                    incident.Scope == PaymentIncidentScope.WhatsAppAddon &&
                    incident.Status == PaymentIncidentStatus.Open, cancellationToken);

            // ── Estado REAL del proveedor (no local): último sondeo de add-ons en TiloPay ──
            // El estado local no puede ver esto. Regresión: local puede mostrar un solo
            // add-on WA800 sano mientras TiloPay tenía WA400 y WA800 cobrables a
            // la vez. Mientras el snapshot diga doble activo, este tablero NO puede decir riesgo 0.
            var providerAuditSnapshots = await _db.ProviderAddonAuditSnapshots
                .AsNoTracking()
                .Select(snapshot => new
                {
                    snapshot.TenantId,
                    snapshot.HasDoubleActive,
                    snapshot.IsInconclusive,
                    snapshot.ActiveAddonSubscriberCount,
                    snapshot.CapturedAtUtc
                })
                .ToListAsync(cancellationToken);

            var providerDoubleActiveAddonTenants = providerAuditSnapshots.Count(s => s.HasDoubleActive);
            var providerAddonAuditInconclusiveTenants = providerAuditSnapshots.Count(s => s.IsInconclusive);
            var providerActiveAddonSubscribersMax = providerAuditSnapshots.Count == 0
                ? 0
                : providerAuditSnapshots.Max(s => s.ActiveAddonSubscriberCount);
            var lastProviderAddonAuditUtc = providerAuditSnapshots.Count == 0
                ? (DateTime?)null
                : providerAuditSnapshots.Max(s => s.CapturedAtUtc);

            // Riesgo de dinero del add-on: baja de suscriptor pendiente + doble activo LOCAL + doble
            // activo en el PROVEEDOR + recurrente pagado activo SIN provider sub
            // (paidAddonsProviderRisk). Los manuales/legacy NUNCA suman.
            var addonMoneyRisk = addonPendingProviderCancellation +
                                 addonDoubleActiveTenants +
                                 providerDoubleActiveAddonTenants +
                                 paidAddonsProviderRisk;

            // ── Opción A: entitlement ≠ configuración (avisos informativos, NO dinero-en-riesgo) ──
            // Una fila por tenant (bajo volumen): se cruza en memoria con los add-ons activos.
            var whatsAppSettingsByTenant = await _db.TenantWhatsAppSettings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Select(s => new { s.TenantId, s.IsEnabled })
                .ToListAsync(cancellationToken);
            var activeAddonTenantIds = activeAddonList.Select(addon => addon.TenantId).ToHashSet();
            var tenantsWithSettings = whatsAppSettingsByTenant.Select(s => s.TenantId).ToHashSet();

            // Paquete activo pero sin configurar (no hay fila de settings): informativo, no envía nada.
            var addonsActiveWithoutConfiguration = activeAddonTenantIds
                .Count(tenantId => !tenantsWithSettings.Contains(tenantId));

            // Configuración habilitada sin add-on vigente: operativo (los envíos quedan bloqueados por
            // el gate de entitlement). No es dinero-en-riesgo.
            var settingsEnabledWithoutActiveAddon = whatsAppSettingsByTenant
                .Count(s => s.IsEnabled && !activeAddonTenantIds.Contains(s.TenantId));

            // Igual que arriba pero contra el ENTITLEMENT EFECTIVO (incluye manuales vigentes): settings
            // habilitados sin ningún acceso comercial efectivo ⇒ los envíos se bloquean. Operativo, no dinero.
            var settingsEnabledWithoutEffectiveEntitlement = whatsAppSettingsByTenant
                .Count(s => s.IsEnabled && !effectiveEntitlementTenantIds.Contains(s.TenantId));

            var renewalSuccessEventsReconciled7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.PaymentEventReconciledByProviderRenewal &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            var pendingPayments = await _db.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p => p.Estado == EstadoPagoProveedor.Pendiente, cancellationToken);
            var manualReviewPayments = await _db.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p => p.Estado == EstadoPagoProveedor.ManualReview, cancellationToken);
            var confirmed24h = await _db.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p => p.Estado == EstadoPagoProveedor.Confirmado && p.FechaConfirmacionUtc >= last24hUtc, cancellationToken);
            var failed24h = await _db.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p =>
                    (p.Estado == EstadoPagoProveedor.Fallido || p.Estado == EstadoPagoProveedor.Cancelado) &&
                    p.FechaActualizacionUtc >= last24hUtc, cancellationToken);

            var overdueRenewals = await _db.Suscripciones.IgnoreQueryFilters()
                .CountAsync(s =>
                    s.Proveedor == PaymentProviderType.Tilopay &&
                    s.TilopayRecurringPlanId != null &&
                    s.Estado == EstadoSuscripcion.Activa &&
                    !s.CancelAtPeriodEnd &&
                    s.FechaProximoCobroUtc != null &&
                    s.FechaProximoCobroUtc < nowUtc &&
                    // Con un fallo/recuperación registrado el cobro no está "perdido": lo sigue el incidente.
                    s.PaymentRecoveryStatus == null &&
                    s.LastPaymentFailedAtUtc == null &&
                    // Vencida solo si el proveedor tampoco cobra más tarde (fecha efectiva pasada).
                    (s.ProviderExpiresAtUtc == null || s.ProviderExpiresAtUtc < nowUtc), cancellationToken);

            var unprocessedEvents = await _db.EventosPago.IgnoreQueryFilters()
                .CountAsync(e => !e.Procesado &&
                    (e.EstadoProcesamiento == "Recibido" || e.EstadoProcesamiento == "Error"), cancellationToken);
            var errorEvents24h = await _db.EventosPago.IgnoreQueryFilters()
                .CountAsync(e => e.EstadoProcesamiento == "Error" && e.FechaRecepcionUtc >= last24hUtc, cancellationToken);
            var manualReviewEvents7d = await _db.EventosPago.IgnoreQueryFilters()
                .CountAsync(e => e.EstadoProcesamiento == "PendingManualReview" && e.FechaRecepcionUtc >= last7dUtc, cancellationToken);
            var unmatchedEvents7d = await _db.EventosPago.IgnoreQueryFilters()
                .CountAsync(e => e.EstadoProcesamiento == "SinRelacion" && e.FechaRecepcionUtc >= last7dUtc, cancellationToken);

            var lastWebhookReceived = await _db.EventosPago.IgnoreQueryFilters()
                .MaxAsync(e => (DateTime?)e.FechaRecepcionUtc, cancellationToken);
            var lastWebhookProcessed = await _db.EventosPago.IgnoreQueryFilters()
                .Where(e => e.Procesado)
                .MaxAsync(e => (DateTime?)e.FechaProcesamientoUtc, cancellationToken);

            // Duración promedio: proyección acotada y cálculo en memoria (fechas de C#,
            // sin depender de traducción SQL de DateDiff).
            var processedTimings = await _db.EventosPago.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(e => e.Procesado && e.FechaProcesamientoUtc != null && e.FechaRecepcionUtc >= last24hUtc)
                .OrderByDescending(e => e.FechaRecepcionUtc)
                .Take(500)
                .Select(e => new { e.FechaRecepcionUtc, e.FechaProcesamientoUtc })
                .ToListAsync(cancellationToken);
            double? avgProcessingMs = processedTimings.Count == 0
                ? null
                : processedTimings.Average(t => (t.FechaProcesamientoUtc!.Value - t.FechaRecepcionUtc).TotalMilliseconds);

            var lastReconciliation = await _db.PlatformAuditLogs
                .AsNoTracking()
                .Where(log => log.Action == PlatformAuditActions.BillingReconciliationCompleted)
                .OrderByDescending(log => log.CreatedAtUtc)
                .Select(log => new { log.CreatedAtUtc, log.AfterJson })
                .FirstOrDefaultAsync(cancellationToken);

            var openAlerts24h = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    (log.Action == PlatformAuditActions.BillingReconciliationAlert ||
                     log.Action == PlatformAuditActions.PaymentWebhookRequiresManualReview) &&
                    log.CreatedAtUtc >= last24hUtc, cancellationToken);

            var autoRepairs7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.BillingAutoRepairApplied &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            // ── Suscriptor recurrente (TiloPay Repeat Admin) ──
            var activeSubsWithoutSubscriberId = await _db.Suscripciones.IgnoreQueryFilters()
                .CountAsync(s =>
                    s.Proveedor == PaymentProviderType.Tilopay &&
                    s.TilopayRecurringPlanId != null &&
                    s.ProviderSubscriptionId == null &&
                    (s.Estado == EstadoSuscripcion.Activa || s.Estado == EstadoSuscripcion.Morosa),
                    cancellationToken);

            var confirmedPaymentsWithoutSubscriberId = await _db.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p =>
                    p.Proveedor == PaymentProviderType.Tilopay &&
                    p.Estado == EstadoPagoProveedor.Confirmado &&
                    p.TilopayRecurringPlanId != null &&
                    p.ProviderSubscriberId == null,
                    cancellationToken);

            var resolutionsPending7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.ProviderSubscriberResolutionPending &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            var resolutionsAmbiguous7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.ProviderSubscriberResolutionAmbiguous &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            var resolved7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.ProviderSubscriberResolved &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            var checkoutsBlocked7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    (log.Action == PlatformAuditActions.CheckoutBlockedExistingProviderSubscriber ||
                     log.Action == PlatformAuditActions.CheckoutBlockedProviderVerificationUnavailable) &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            var providerCancellationsFailed7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    (log.Action == PlatformAuditActions.UpgradeOldProviderSubscriptionCancellationFailed ||
                     log.Action == PlatformAuditActions.PlanChangeOldSubscriberCancellationVerificationFailed ||
                     log.Action == PlatformAuditActions.ProviderSubscriptionDeleteFailed) &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            var lastResolutionUtc = await _db.PlatformAuditLogs
                .Where(log => log.Action == PlatformAuditActions.ProviderSubscriberResolved)
                .MaxAsync(log => (DateTime?)log.CreatedAtUtc, cancellationToken);

            // ── Cambio de plan base (estrategia B) ──
            // Los cambios abiertos se proyectan con su pago para separar el ruido (checkout que
            // nadie pagó) del riesgo real (dinero cobrado esperando aplicarse). Contarlos juntos
            // hacía que un cliente arrepentido se viera igual que un doble cobro inminente.
            var openIntents = await _db.PlanChangeIntents.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(intent => intent.Estado == PlanChangeIntentState.Pending)
                .Select(intent => new
                {
                    intent.Id,
                    intent.CreatedAtUtc,
                    Payment = _db.PagosSuscripcion.IgnoreQueryFilters()
                        .FirstOrDefault(payment => payment.Id == intent.PagoSuscripcionId)
                })
                .ToListAsync(cancellationToken);

            var planChangePending = openIntents.Count;

            var moneyRiskIntents = openIntents
                .Where(intent => PlanChangeCheckoutAbandonmentRules.HasMoneySignals(intent.Payment))
                .ToList();

            var pendingCheckouts = openIntents
                .Where(intent => !PlanChangeCheckoutAbandonmentRules.HasMoneySignals(intent.Payment))
                .ToList();

            var oldestPendingCheckoutUtc = pendingCheckouts.Count == 0
                ? (DateTime?)null
                : pendingCheckouts.Min(intent => intent.CreatedAtUtc);

            // Cambio aplicado cuyo suscriptor viejo AÚN no se canceló en el proveedor: riesgo de doble cobro.
            // Se proyectan (son pocos: uno por cambio de plan sin cerrar) para derivar en memoria
            // el estado del backoff sin repetir consultas sobre la misma tabla.
            var pendingOldCancellations = await _db.PlanChangeIntents.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(intent =>
                    intent.Estado == PlanChangeIntentState.Applied &&
                    intent.OldProviderCancellation == ProviderCancellationState.PendingManualCancellation)
                .OrderBy(intent => intent.AppliedAtUtc)
                .Select(intent => new
                {
                    intent.Id,
                    intent.TenantId,
                    intent.ToPlanCode,
                    intent.AppliedAtUtc,
                    intent.OldCancellationAttemptCount,
                    intent.OldCancellationNextRetryUtc,
                    intent.FromProviderSubscriptionId,
                    intent.NewProviderSubscriptionId,
                    intent.FromTilopayRecurringPlanId
                })
                .ToListAsync(cancellationToken);

            var planChangeManualReview = pendingOldCancellations.Count;

            // El mínimo NextRetry: null en algún pendiente significa "elegible ya", y eso es más
            // urgente que cualquier fecha, así que gana sobre el mínimo de los que sí tienen fecha.
            DateTime? oldCancellationNextRetryUtc = pendingOldCancellations.Count == 0
                ? null
                : pendingOldCancellations.Any(intent => intent.OldCancellationNextRetryUtc is null)
                    ? null
                    : pendingOldCancellations.Min(intent => intent.OldCancellationNextRetryUtc);

            var oldCancellationBackoffBlocked = pendingOldCancellations
                .Count(intent => intent.OldCancellationNextRetryUtc is { } next && next > nowUtc);

            var oldCancellationMaxAttempts = pendingOldCancellations.Count == 0
                ? 0
                : pendingOldCancellations.Max(intent => intent.OldCancellationAttemptCount);

            var skippedAutoCancelDisabled24h = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.PlanChangeOldSubscriberCancellationSkippedAutoCancelDisabled &&
                    log.CreatedAtUtc >= last24hUtc, cancellationToken);

            var skippedNotEligible24h = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.PlanChangeOldSubscriberCancellationSkippedNotEligible &&
                    log.CreatedAtUtc >= last24hUtc, cancellationToken);

            // Viejo verificado como TODAVÍA Activo: la auditoría de verificación fallida es la
            // única evidencia real (viene de getSuscriptorRepeat) sin llamar al proveedor aquí.
            var pendingIntentKeys = pendingOldCancellations.Select(intent => intent.Id.ToString()).ToList();

            var verifiedStillActiveKeys = pendingIntentKeys.Count == 0
                ? new List<string>()
                : await _db.PlatformAuditLogs
                    .Where(log =>
                        log.Action == PlatformAuditActions.PlanChangeOldSubscriberCancellationVerificationFailed &&
                        log.EntityId != null &&
                        pendingIntentKeys.Contains(log.EntityId) &&
                        log.CreatedAtUtc >= last24hUtc)
                    .Select(log => log.EntityId!)
                    .Distinct()
                    .ToListAsync(cancellationToken);

            var tenantNames = pendingOldCancellations.Count == 0
                ? new Dictionary<Guid, string>()
                : await _db.Tenants
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(tenant => pendingOldCancellations.Select(p => p.TenantId).Contains(tenant.Id))
                    .ToDictionaryAsync(tenant => tenant.Id, tenant => tenant.Nombre, cancellationToken);

            // Detalle acotado: la lista es para que un humano actúe, no para volcar la tabla.
            var pendingItems = pendingOldCancellations
                .Take(50)
                .Select(intent => new OldCancellationPendingItem
                {
                    IntentId = intent.Id,
                    TenantId = intent.TenantId,
                    TenantName = tenantNames.TryGetValue(intent.TenantId, out var name) ? name : null,
                    ToPlanCode = intent.ToPlanCode,
                    AttemptCount = intent.OldCancellationAttemptCount,
                    NextRetryUtc = intent.OldCancellationNextRetryUtc,
                    AppliedAtUtc = intent.AppliedAtUtc,
                    OldSubscriberSuffix = SensitiveDataMasker.MaskReference(intent.FromProviderSubscriptionId),
                    NewSubscriberSuffix = SensitiveDataMasker.MaskReference(intent.NewProviderSubscriptionId),
                    OldRecurringPlanId = intent.FromTilopayRecurringPlanId,
                    VerifiedStillActive = verifiedStillActiveKeys.Contains(intent.Id.ToString())
                })
                .ToList();

            var lastPlanChangeUtc = await _db.PlanChangeIntents.IgnoreQueryFilters()
                .Where(intent => intent.Estado == PlanChangeIntentState.Applied)
                .MaxAsync(intent => (DateTime?)intent.AppliedAtUtc, cancellationToken);

            // ── Fecha del proveedor vs local ──
            // Solo suscripciones activas ya sincronizadas (ProviderExpiresAtUtc no nulo). El
            // desajuste se juzga con la MISMA tolerancia de conciliación (constante local: es un
            // contador de diagnóstico, no una decisión de dinero).
            var providerExpiryTolerance = TimeSpan.FromHours(ProviderExpiryMismatchToleranceHours);
            var syncedSubscriptions = await _db.Suscripciones.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s =>
                    s.Proveedor == PaymentProviderType.Tilopay &&
                    s.ProviderExpiresAtUtc != null &&
                    (s.Estado == EstadoSuscripcion.Activa || s.Estado == EstadoSuscripcion.Morosa))
                .Select(s => new
                {
                    s.TenantId,
                    s.CodigoPlan,
                    s.FechaFin,
                    s.ProviderExpiresAtUtc,
                    s.ProviderExpiryRaw,
                    s.ProviderExpiryLastSyncedUtc
                })
                .ToListAsync(cancellationToken);

            var aheadSubs = syncedSubscriptions
                .Where(s => SubscriptionEffectiveDates.ProviderIsAhead(s.FechaFin, s.ProviderExpiresAtUtc, providerExpiryTolerance))
                .ToList();
            var earlierSubs = syncedSubscriptions
                .Where(s => SubscriptionEffectiveDates.ProviderIsEarlier(s.FechaFin, s.ProviderExpiresAtUtc, providerExpiryTolerance))
                .ToList();

            var mismatchTenantIds = aheadSubs.Concat(earlierSubs).Select(s => s.TenantId).Distinct().ToList();
            var mismatchTenantNames = mismatchTenantIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await _db.Tenants.IgnoreQueryFilters().AsNoTracking()
                    .Where(t => mismatchTenantIds.Contains(t.Id))
                    .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

            var providerExpiryMismatches = aheadSubs.Select(s => (Sub: s, Ahead: true))
                .Concat(earlierSubs.Select(s => (Sub: s, Ahead: false)))
                .Take(50)
                .Select(x => new ProviderExpiryMismatchItem
                {
                    TenantId = x.Sub.TenantId,
                    TenantName = mismatchTenantNames.TryGetValue(x.Sub.TenantId, out var name) ? name : null,
                    PlanCode = x.Sub.CodigoPlan,
                    LocalEndUtc = x.Sub.FechaFin,
                    ProviderExpiresAtUtc = x.Sub.ProviderExpiresAtUtc,
                    // Fechas de calendario Tica para mostrar. La del proveedor prefiere su raw exacto.
                    LocalEndDisplay = SubscriptionDisplayDates.Format(
                        x.Sub.FechaFin is { } f ? DateOnly.FromDateTime(f) : null),
                    ProviderExpiryDisplay = SubscriptionDisplayDates.FormatEffective(
                        null, x.Sub.ProviderExpiresAtUtc, x.Sub.ProviderExpiryRaw),
                    ProviderIsAhead = x.Ahead,
                    LastSyncedUtc = x.Sub.ProviderExpiryLastSyncedUtc
                })
                .ToList();

            var providerExpiryReconciled7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.BillingProviderExpiryReconciled &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            // ── Ciclo de vida: cancelación programada, pausa y reactivación ──
            var subscriptionsCancelAtPeriodEnd = await _db.Suscripciones.IgnoreQueryFilters()
                .CountAsync(s =>
                    s.CancelAtPeriodEnd &&
                    (s.Estado == EstadoSuscripcion.Activa || s.Estado == EstadoSuscripcion.Morosa),
                    cancellationToken);

            // Renovación cancelada pero SIN baja verificada en el proveedor: podría seguir cobrando.
            var providerCancellationsPendingVerification = await _db.Suscripciones.IgnoreQueryFilters()
                .CountAsync(s => s.CancelAtPeriodEnd && s.ProviderCancelledAtUtc == null, cancellationToken);

            var providerPausedSubscriptions = await _db.Suscripciones.IgnoreQueryFilters()
                .CountAsync(s => s.ProviderPausedAtUtc != null, cancellationToken);

            var cancellationFailed7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.SubscriptionCancellationFailedManualReview &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            var pauseFailed7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.SubscriptionPauseFailedManualReview &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            var reactivationFailed7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.SubscriptionReactivateFailedManualReview &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            var providerStatusMismatch7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.SubscriptionProviderStatusMismatch &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            // ── Recuperación de pago (SOLO base: los incidentes de add-on van en su propia sección) ──
            var openPaymentIncidents = await _db.SubscriptionPaymentIncidents.IgnoreQueryFilters()
                .CountAsync(i => i.Scope == PaymentIncidentScope.BasePlan && i.Status == PaymentIncidentStatus.Open, cancellationToken);
            var activeGracePeriods = await _db.SubscriptionPaymentIncidents.IgnoreQueryFilters()
                .CountAsync(i =>
                    i.Scope == PaymentIncidentScope.BasePlan &&
                    i.Status == PaymentIncidentStatus.Open &&
                    i.GraceEndsAtUtc != null &&
                    i.GraceEndsAtUtc > nowUtc, cancellationToken);
            var graceExpiredNotSuspended = await _db.SubscriptionPaymentIncidents.IgnoreQueryFilters()
                .CountAsync(i => i.Scope == PaymentIncidentScope.BasePlan && i.Status == PaymentIncidentStatus.GraceExpired, cancellationToken);
            var suspendedForNonPayment = await _db.Suscripciones.IgnoreQueryFilters()
                .CountAsync(s => s.PaymentRecoveryStatus == "Suspended", cancellationToken);
            var paymentRecoveryManualReview = await _db.SubscriptionPaymentIncidents.IgnoreQueryFilters()
                .CountAsync(i => i.Scope == PaymentIncidentScope.BasePlan && i.Status == PaymentIncidentStatus.ManualReview, cancellationToken);
            var notificationsFailed7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.PaymentRecoveryNotificationFailed &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);
            var updateUrlFailures7d = await _db.PlatformAuditLogs
                .CountAsync(log =>
                    log.Action == PlatformAuditActions.PaymentMethodUpdateUrlFailed &&
                    log.CreatedAtUtc >= last7dUtc, cancellationToken);

            return new BillingHealthSnapshot
            {
                GeneratedAtUtc = nowUtc,
                WhatsAppAddonsWithoutActiveBase = addonWithoutActiveBase,
                WhatsAppAddonsPendingProviderCancellation = addonPendingProviderCancellation,
                WhatsAppAddonsDoubleActiveTenants = addonDoubleActiveTenants,
                WhatsAppAddonOpenPaymentIncidents = addonOpenPaymentIncidents,
                WhatsAppAddonMoneyRiskCount = addonMoneyRisk,
                ProviderDoubleActiveWhatsAppAddonTenants = providerDoubleActiveAddonTenants,
                ProviderWhatsAppAddonAuditInconclusiveTenants = providerAddonAuditInconclusiveTenants,
                ProviderMaxActiveAddonSubscribersByTenant = providerActiveAddonSubscribersMax,
                LastProviderAddonAuditUtc = lastProviderAddonAuditUtc,
                WhatsAppAddonsActiveWithoutConfiguration = addonsActiveWithoutConfiguration,
                WhatsAppSettingsEnabledWithoutActiveAddon = settingsEnabledWithoutActiveAddon,
                PaidAddonsActiveWithoutProviderRisk = paidAddonsProviderRisk,
                ManualWhatsAppGrantsActive = manualGrantsActive,
                ManualWhatsAppGrantsExpiredStillActive = manualGrantsExpiredStillActive,
                LegacyWhatsAppAddonsActive = legacyAddonsActive,
                WhatsAppSettingsEnabledWithoutEffectiveEntitlement = settingsEnabledWithoutEffectiveEntitlement,
                RenewalSuccessEventsReconciledLast7d = renewalSuccessEventsReconciled7d,
                WhatsAppAddonCheckoutConfig = _checkoutInspector?.InspectAddons() ?? Array.Empty<ManagedPlanCheckoutStatus>(),
                ActiveSubscriptions = active,
                TrialSubscriptions = trial,
                MorosaSubscriptions = morosa,
                SuspendedSubscriptions = suspended,
                PendingSubscriptions = pending,
                CancelledSubscriptions = cancelled,
                FailedSubscriptions = failed,
                ActiveWhatsAppAddons = activeAddons,
                PendingPayments = pendingPayments,
                ManualReviewPayments = manualReviewPayments,
                ConfirmedPaymentsLast24h = confirmed24h,
                FailedPaymentsLast24h = failed24h,
                OverdueRenewals = overdueRenewals,
                UnprocessedEvents = unprocessedEvents,
                ErrorEventsLast24h = errorEvents24h,
                ManualReviewEventsLast7d = manualReviewEvents7d,
                UnmatchedEventsLast7d = unmatchedEvents7d,
                LastWebhookReceivedUtc = lastWebhookReceived,
                LastWebhookProcessedUtc = lastWebhookProcessed,
                AvgWebhookProcessingMsLast24h = avgProcessingMs,
                LastReconciliationUtc = lastReconciliation?.CreatedAtUtc,
                LastReconciliationSummaryJson = lastReconciliation?.AfterJson,
                OpenAlertsLast24h = openAlerts24h,
                AutoRepairsLast7d = autoRepairs7d,
                ActiveSubscriptionsWithoutSubscriberId = activeSubsWithoutSubscriberId,
                ConfirmedPaymentsWithoutSubscriberId = confirmedPaymentsWithoutSubscriberId,
                SubscriberResolutionsPendingLast7d = resolutionsPending7d,
                SubscriberResolutionsAmbiguousLast7d = resolutionsAmbiguous7d,
                SubscriberResolvedLast7d = resolved7d,
                CheckoutsBlockedByDuplicateLast7d = checkoutsBlocked7d,
                ProviderCancellationsFailedLast7d = providerCancellationsFailed7d,
                LastSuccessfulSubscriberResolutionUtc = lastResolutionUtc,
                PlanChangePendingCount = planChangePending,
                PlanChangeManualReviewCount = planChangeManualReview,
                LastSuccessfulPlanChangeUtc = lastPlanChangeUtc,
                PlanChangePendingCheckoutCount = pendingCheckouts.Count,
                // Riesgo = pago confirmado sin aplicar + viejo sin cancelar. Son conjuntos
                // distintos (uno sigue Pending, el otro ya está Applied), así que suman.
                PlanChangeMoneyRiskCount = moneyRiskIntents.Count + planChangeManualReview,
                OldestPendingCheckoutUtc = oldestPendingCheckoutUtc,
                OldestPendingCheckoutAgeHours = oldestPendingCheckoutUtc is { } oldestUtc
                    ? Math.Round((nowUtc - oldestUtc).TotalHours, 1)
                    : null,
                ProviderExpiryMismatchCount = aheadSubs.Count + earlierSubs.Count,
                ActiveSubscriptionsProviderExpiryAheadCount = aheadSubs.Count,
                ActiveSubscriptionsProviderExpiryEarlierCount = earlierSubs.Count,
                ProviderExpiryReconciledLast7d = providerExpiryReconciled7d,
                SubscriptionsCancelAtPeriodEnd = subscriptionsCancelAtPeriodEnd,
                ProviderCancellationsPendingVerification = providerCancellationsPendingVerification,
                ProviderPausedSubscriptions = providerPausedSubscriptions,
                ProviderCancellationFailedLast7d = cancellationFailed7d,
                PauseFailedLast7d = pauseFailed7d,
                ReactivationFailedLast7d = reactivationFailed7d,
                ProviderStatusMismatchCount = providerStatusMismatch7d,
                OpenPaymentRecoveryIncidents = openPaymentIncidents,
                ActiveGracePeriods = activeGracePeriods,
                GraceExpiredNotSuspended = graceExpiredNotSuspended,
                SuspendedForNonPayment = suspendedForNonPayment,
                PaymentRecoveryManualReviewCount = paymentRecoveryManualReview,
                PaymentRecoveryNotificationsFailedLast7d = notificationsFailed7d,
                PaymentMethodUpdateUrlFailuresLast7d = updateUrlFailures7d,
                ProviderExpiryMismatches = providerExpiryMismatches,
                OldCancellationPendingCount = planChangeManualReview,
                OldCancellationNextRetryUtc = oldCancellationNextRetryUtc,
                OldCancellationBackoffBlockedCount = oldCancellationBackoffBlocked,
                OldCancellationSkippedAutoCancelDisabledCount = skippedAutoCancelDisabled24h,
                OldCancellationSkippedNotEligibleCount = skippedNotEligible24h,
                OldCancellationVerifiedStillActiveCount = verifiedStillActiveKeys.Count,
                OldCancellationMaxAttemptCount = oldCancellationMaxAttempts,
                OldCancellationPendingItems = pendingItems
            };
        }
    }
}
