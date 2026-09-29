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
    public sealed record ProviderSubscriptionActionResult
    {
        public required bool Succeeded { get; init; }
        public string? Message { get; init; }

        public static ProviderSubscriptionActionResult Ok(string message) => new() { Succeeded = true, Message = message };
        public static ProviderSubscriptionActionResult Fail(string message) => new() { Succeeded = false, Message = message };
    }

    /// <summary>
    /// Resultado de un intento de cancelación del suscriptor viejo. Distingue lo que el
    /// presupuesto de reintentos necesita saber: si REALMENTE se llamó al proveedor
    /// (<see cref="ProviderCalled"/>) o si se salió antes por una guarda. Un skip no es un
    /// intento fallido y no debe gastar presupuesto.
    /// </summary>
    public sealed record ProviderCancellationAttemptResult
    {
        /// <summary>True solo si se invocó deleteSuscriptorRepeat/editSuscriptorRepeat o la verificación contra TiloPay.</summary>
        public required bool ProviderCalled { get; init; }

        /// <summary>True solo con baja VERIFICADA (ausente o inactivo en getSuscriptorRepeat).</summary>
        public required bool Cancelled { get; init; }

        /// <summary>TiloPay dijo éxito pero la verificación no lo confirmó: el caso más peligroso.</summary>
        public bool VerificationFailed { get; init; }

        public string? Message { get; init; }

        public static ProviderCancellationAttemptResult NotCalled(string message) =>
            new() { ProviderCalled = false, Cancelled = false, Message = message };
    }

    public interface IProviderSubscriptionManager
    {
        bool IsEnabled { get; }

        /// <summary>
        /// Tras aplicar un upgrade, cancela en TiloPay el suscriptor ANTERIOR para evitar doble
        /// cobro. Best-effort y post-commit (HTTP fuera de transacción). Éxito => intent Cancelled +
        /// audit Completed; fallo => queda PendingManualCancellation + audit crítico Failed.
        /// Con <paramref name="intentId"/> apunta a un intent exacto (reintento por intent);
        /// sin él toma el último cambio aplicado del tenant (ruta del webhook).
        /// </summary>
        Task<ProviderCancellationAttemptResult> TryCancelOldSubscriberForUpgradeAsync(
            Guid tenantId,
            Guid? intentId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancelación del cliente = cancelar la RENOVACIÓN sin cortar acceso. Elimina el suscriptor
        /// en TiloPay (con VERIFICACIÓN obligatoria), marca CancelAtPeriodEnd y deja el acceso vivo
        /// hasta la fecha efectiva ya pagada. Idempotente: si el suscriptor ya está inactivo, marca
        /// igual sin fallar. Si TiloPay dice 200 pero la verificación no confirma la baja, NO marca
        /// y deja el caso a revisión manual.
        /// </summary>
        Task<ProviderSubscriptionActionResult> RequestCancellationAtPeriodEndAsync(Guid tenantId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancelación INMEDIATA (solo plataforma): elimina el suscriptor en TiloPay, VERIFICA la
        /// baja y solo entonces corta el acceso (Estado=Cancelada, FechaFin=ahora). Sin verificación
        /// no corta acceso: audita revisión manual.
        /// </summary>
        Task<ProviderSubscriptionActionResult> CancelAsync(Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default);

        /// <summary>
        /// Pausa el suscriptor en TiloPay (VERIFICADO status 3). Por defecto mantiene el acceso ya
        /// pagado; con <paramref name="immediate"/> la plataforma suspende el acceso de una vez.
        /// </summary>
        Task<ProviderSubscriptionActionResult> PauseAsync(Guid tenantId, string actorUserId, string actorEmail, bool immediate = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reactiva un suscriptor PAUSADO (VERIFICADO status 1). Un suscriptor Eliminado NO se
        /// reactiva: se deja a revisión manual (preferir hosted checkout nuevo). Idempotente si ya
        /// está Activo.
        /// </summary>
        Task<ProviderSubscriptionActionResult> ReactivateAsync(Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reactiva una RENOVACIÓN cancelada (cancel-at-period-end) que AÚN está vigente: vuelve a
        /// dar de alta el MISMO suscriptor (reactiveSuscriptorRepeat / edit status=1) y, si verifica
        /// Active, limpia CancelAtPeriodEnd. Distinto de <see cref="ReactivateAsync"/>: acá el
        /// suscriptor está Delete a propósito (lo cancelamos nosotros) y solo se reactiva dentro del
        /// período pagado. Si el período ya venció o no hay cancelación pendiente, NO reactiva.
        /// </summary>
        Task<ProviderSubscriptionActionResult> ReactivateRenewalAsync(Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sincroniza el snapshot del estado del proveedor consultando getSuscriptorRepeat (SOLO
        /// lectura, sin operar sobre el suscriptor). Persiste ProviderStatusRaw + LastSynced y ajusta
        /// las banderas informativas: ProviderPausedAtUtc si está Pausado, ProviderCancelledAtUtc si
        /// está Eliminado/ausente, y limpia ProviderPausedAtUtc si volvió a Activo. NUNCA cambia el
        /// Estado local ni el acceso: es un refresco de diagnóstico, no una operación money-critical.
        /// HTTP fuera de transacción (primero TiloPay, luego BeginScope + SaveChanges).
        /// </summary>
        Task<ProviderSubscriptionActionResult> SyncProviderStatusAsync(Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default);
    }

    public sealed class ProviderSubscriptionManager : IProviderSubscriptionManager
    {
        private readonly ApplicationDbContext _db;
        private readonly ITilopayRepeatAdminService _adminService;
        private readonly ITenantExecutionContextAccessor _tenantExecutionContextAccessor;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly OpcionesTilopayRepeatAdmin _adminOptions;
        private readonly ILogger<ProviderSubscriptionManager> _logger;

        // Opcional (patrón del módulo): en producción DI lo inyecta; en tests que construyen el
        // manager con el ctor mínimo queda null y la invalidación de acceso se salta sin efecto.
        private readonly ITenantCommercialAccessCache? _accessCache;

        // Opcional: cascada de cancelación al add-on de WhatsApp. Null en tests con ctor mínimo.
        private readonly IAddonSubscriptionManager? _addonSubscriptionManager;

        public ProviderSubscriptionManager(
            ApplicationDbContext db,
            ITilopayRepeatAdminService adminService,
            ITenantExecutionContextAccessor tenantExecutionContextAccessor,
            IBusinessDateTimeProvider clock,
            IOptions<OpcionesTilopayRepeatAdmin> adminOptions,
            ILogger<ProviderSubscriptionManager> logger,
            ITenantCommercialAccessCache? accessCache = null,
            IAddonSubscriptionManager? addonSubscriptionManager = null)
        {
            _db = db;
            _adminService = adminService;
            _tenantExecutionContextAccessor = tenantExecutionContextAccessor;
            _clock = clock;
            _adminOptions = adminOptions.Value;
            _logger = logger;
            _accessCache = accessCache;
            _addonSubscriptionManager = addonSubscriptionManager;
        }

        public bool IsEnabled => _adminService.IsEnabled;

        public async Task<ProviderCancellationAttemptResult> TryCancelOldSubscriberForUpgradeAsync(
            Guid tenantId,
            Guid? intentId = null,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled || !_adminOptions.AutoCancelOldSubscriberOnUpgrade)
            {
                // Deshabilitado: queda la alerta PendingManualCancellation existente. NO es un
                // intento fallido — el reconciliador no debe gastarle presupuesto al intent.
                return ProviderCancellationAttemptResult.NotCalled(
                    "La cancelación automática del suscriptor viejo está deshabilitada.");
            }

            var intent = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .Where(i =>
                    i.TenantId == tenantId &&
                    i.Estado == PlanChangeIntentState.Applied &&
                    i.OldProviderCancellation == ProviderCancellationState.PendingManualCancellation &&
                    i.FromProviderSubscriptionId != null &&
                    (intentId == null || i.Id == intentId))
                .OrderByDescending(i => i.AppliedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (intent?.FromProviderSubscriptionId is not { } oldSubscriberId ||
                string.IsNullOrWhiteSpace(oldSubscriberId))
            {
                return ProviderCancellationAttemptResult.NotCalled(
                    "No hay cambio de plan aplicado con cancelación pendiente y suscriptor viejo conocido.");
            }

            // No cancelar si el suscriptor viejo es el mismo que el nuevo (mismo id => sin doble cobro).
            if (string.Equals(oldSubscriberId.Trim(), intent.NewProviderSubscriptionId?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return ProviderCancellationAttemptResult.NotCalled(
                    "El suscriptor viejo y el nuevo son el mismo: no hay nada que cancelar.");
            }

            var result = await CancelOrDeleteOldSubscriberAsync(
                oldSubscriberId,
                intent.FromTilopayRecurringPlanId,
                tenantId,
                cancellationToken);

            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();

            var tracked = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == intent.Id, cancellationToken);

            if (tracked is null)
            {
                return ProviderCancellationAttemptResult.NotCalled("El intent desapareció durante la cancelación.");
            }

            if (result.Succeeded)
            {
                tracked.OldProviderCancellation = ProviderCancellationState.Cancelled;
                tracked.UpdatedAtUtc = nowUtc;
                tracked.Notes = $"Suscriptor anterior {oldSubscriberId} cancelado automáticamente en TiloPay.";

                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = PlatformAuditActions.UpgradeOldProviderSubscriptionCancellationCompleted,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    EntityId = tracked.Id.ToString(),
                    TenantId = tenantId,
                    Reason = $"Upgrade a {tracked.ToPlanCode}: suscriptor anterior cancelado en TiloPay. SubscriberIdSuffix {SensitiveDataMasker.MaskReference(oldSubscriberId)}.",
                    CreatedAtUtc = nowUtc
                });

                _logger.LogInformation(
                    "Suscriptor anterior cancelado en upgrade. TenantId {TenantId}. IntentId {IntentId}.",
                    tenantId,
                    tracked.Id);
            }
            else
            {
                // NO ocultar: queda pendiente + alerta crítica para revisión manual (evita doble cobro).
                // Si el fallo fue de VERIFICACIÓN (TiloPay dijo 200 pero el viejo sigue Activo),
                // se audita con su acción propia: es el caso más peligroso y debe distinguirse.
                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = result.VerificationFailed
                        ? PlatformAuditActions.PlanChangeOldSubscriberCancellationVerificationFailed
                        : PlatformAuditActions.UpgradeOldProviderSubscriptionCancellationFailed,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    EntityId = tracked.Id.ToString(),
                    TenantId = tenantId,
                    Reason = $"CRÍTICO: no se pudo cancelar el suscriptor anterior {SensitiveDataMasker.MaskReference(oldSubscriberId)} en TiloPay tras upgrade a {tracked.ToPlanCode}. Riesgo de doble cobro. Detalle: {result.Message}",
                    CreatedAtUtc = nowUtc
                });

                _logger.LogError(
                    "Falló la cancelación automática del suscriptor anterior en upgrade. TenantId {TenantId}. IntentId {IntentId}.",
                    tenantId,
                    tracked.Id);
            }

            await _db.SaveChangesAsync(cancellationToken);

            // ProviderCalled = true: se llamó a TiloPay (baja y/o verificación). Este intento SÍ
            // consume presupuesto, haya salido bien o mal.
            return new ProviderCancellationAttemptResult
            {
                ProviderCalled = true,
                Cancelled = result.Succeeded,
                VerificationFailed = result.VerificationFailed,
                Message = result.Message
            };
        }

        /// <summary>
        /// Cancela/elimina el suscriptor viejo en TiloPay con VERIFICACIÓN OBLIGATORIA:
        /// 1) deleteSuscriptorRepeat; 2) si falla, editSuscriptorRepeat status=4 (Eliminado);
        /// 3) SIEMPRE verifica vía getSuscriptorRepeat del plan viejo. La regla de oro:
        ///    un HTTP 200 del proveedor NUNCA basta — solo se reporta éxito si la verificación
        ///    confirma que el suscriptor ya no aparece o su status no es Active. Esto cubre el
        ///    caso "TiloPay responde 200 pero no cancela realmente" y también el idempotente
        ///    "ya estaba eliminado" (delete falla pero la verificación muestra ausente).
        ///    Si TiloPay respondió éxito pero la verificación muestra Active (o no se pudo
        ///    verificar), devuelve fallo con VerificationFailed=true para reintento con backoff.
        /// </summary>
        private async Task<TilopayAdminOperationResult> CancelOrDeleteOldSubscriberAsync(
            string oldSubscriberId,
            int? oldPlanId,
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            TilopayAdminOperationResult result;
            try
            {
                result = await _adminService.DeleteSubscriberAsync(oldSubscriberId, cancellationToken);

                if (!result.Succeeded)
                {
                    // Fallback documentado: editSuscriptorRepeat estado 4 = Eliminado.
                    result = await _adminService.EditSubscriberStatusAsync(
                        oldSubscriberId,
                        TilopaySubscriberStatus.Deleted,
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción cancelando suscriptor viejo en upgrade. TenantId {TenantId}.", tenantId);
                result = TilopayAdminOperationResult.Fail("Excepción al cancelar el suscriptor anterior.");
            }

            if (oldPlanId is not { } planId)
            {
                // Sin plan viejo no hay forma de verificar por getSuscriptorRepeat. En la práctica
                // el cambio de plan siempre lo tiene (guard del checkout); este es un fallback
                // defensivo para datos legacy: se acepta el resultado del proveedor tal cual y se
                // deja rastro para diagnóstico.
                _logger.LogWarning(
                    "Cancelación sin FromTilopayRecurringPlanId: no se puede verificar post-baja. TenantId {TenantId}. ProviderOk {ProviderOk}.",
                    tenantId,
                    result.Succeeded);
                return result;
            }

            bool stillActive;
            try
            {
                stillActive = await IsSubscriberStillActiveAsync(planId, oldSubscriberId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo verificar el estado del suscriptor viejo. TenantId {TenantId}.", tenantId);

                // Regla: sin verificación NO hay Cancelled, aunque el proveedor haya dicho 200.
                // Queda pendiente y el worker de reintento vuelve (delete idempotente + verify).
                return result.Succeeded
                    ? TilopayAdminOperationResult.FailVerification(
                        "TiloPay aceptó la baja pero la verificación posterior no se pudo completar; se reintentará.")
                    : result;
            }

            if (!stillActive)
            {
                // Verificado: ya no cobrable. Cubre proveedor-éxito+confirmado y "ya estaba eliminado".
                _logger.LogInformation(
                    "Cancelación del suscriptor viejo VERIFICADA (ausente o inactivo). TenantId {TenantId}. SubscriberIdSuffix {Suffix}. ProviderOk {ProviderOk}.",
                    tenantId,
                    SensitiveDataMasker.MaskReference(oldSubscriberId),
                    result.Succeeded);
                return TilopayAdminOperationResult.Ok("Cancelación verificada: el suscriptor viejo ya no está activo.");
            }

            // Sigue ACTIVO: aunque el proveedor haya devuelto 200, la baja NO ocurrió realmente.
            _logger.LogError(
                "El suscriptor viejo SIGUE ACTIVO tras la operación de baja. TenantId {TenantId}. SubscriberIdSuffix {Suffix}. ProviderOk {ProviderOk}.",
                tenantId,
                SensitiveDataMasker.MaskReference(oldSubscriberId),
                result.Succeeded);

            return result.Succeeded
                ? TilopayAdminOperationResult.FailVerification(
                    "TiloPay respondió éxito pero el suscriptor viejo sigue Activo según getSuscriptorRepeat; se reintentará.")
                : result;
        }

        /// <summary>
        /// True si NO podemos descartar que el suscriptor siga cobrando. Ausente ⇒ false (no
        /// cobrable). Presente ⇒ solo un status explícitamente inactivo (Delete/Cancelado/…) cierra
        /// el caso: un status que no sabemos leer se trata como "todavía puede cobrar" y deja el
        /// intent pendiente con backoff, en vez de dar por buena una baja que nadie confirmó.
        /// La clasificación vive en ProviderSubscriberStatusRules (fuente única).
        /// </summary>
        private async Task<bool> IsSubscriberStillActiveAsync(
            int planId,
            string subscriberId,
            CancellationToken cancellationToken)
        {
            var subscribers = await _adminService.GetSuscriptorRepeatAsync(planId, cancellationToken);
            var match = subscribers.FirstOrDefault(s =>
                string.Equals(s.SubscriberId, subscriberId, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return false; // Ya no aparece: no cobrable.
            }

            return ProviderSubscriberStatusRules.MayStillCharge(match.Status);
        }

        public async Task<ProviderSubscriptionActionResult> RequestCancellationAtPeriodEndAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            var result = await ExecuteCancellationAsync(tenantId, actorUserId, actorEmail, reason, immediate: false, cancellationToken);
            await CascadeAddonCancellationAsync(tenantId, actorUserId, result, immediate: false, cancellationToken);
            return result;
        }

        public async Task<ProviderSubscriptionActionResult> CancelAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default)
        {
            var result = await ExecuteCancellationAsync(tenantId, actorUserId, actorEmail, "Cancelación inmediata (plataforma).", immediate: true, cancellationToken);
            await CascadeAddonCancellationAsync(tenantId, actorUserId, result, immediate: true, cancellationToken);
            return result;
        }

        /// <summary>
        /// Cascada base→add-on: si la cancelación del plan base se aplicó, el add-on de WhatsApp no
        /// debe seguir cobrando (regla: nunca un add-on vivo sin SaaS). Best-effort y FUERA del scope
        /// de la cancelación base (ya committeada). Nunca rompe la operación principal.
        /// </summary>
        private async Task CascadeAddonCancellationAsync(
            Guid tenantId,
            string actorUserId,
            ProviderSubscriptionActionResult baseResult,
            bool immediate,
            CancellationToken cancellationToken)
        {
            if (!baseResult.Succeeded || _addonSubscriptionManager is null)
            {
                return;
            }

            try
            {
                await _addonSubscriptionManager.ScheduleAddonCancellationForBaseCancellationAsync(
                    tenantId,
                    actorUserId,
                    "Cascada de la cancelación del plan base.",
                    immediate,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Cascada de cancelación del add-on tras cancelar el plan base no se completó. TenantId {TenantId}.",
                    tenantId);
            }
        }

        /// <summary>
        /// Núcleo compartido de cancelación. Da de baja el suscriptor en TiloPay CON verificación
        /// obligatoria (un 200 nunca basta). Con <paramref name="immediate"/> corta el acceso ya;
        /// sin él, marca CancelAtPeriodEnd y mantiene el acceso hasta el fin efectivo ya pagado.
        /// HTTP siempre FUERA de la transacción: primero el proveedor, luego BeginScope + SaveChanges.
        /// </summary>
        private async Task<ProviderSubscriptionActionResult> ExecuteCancellationAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            string? reason,
            bool immediate,
            CancellationToken cancellationToken)
        {
            if (!_adminService.IsEnabled)
            {
                return ProviderSubscriptionActionResult.Fail("La integración de TiloPay Repeat Admin está deshabilitada.");
            }

            var context = await GetSubscriberContextAsync(tenantId, cancellationToken);
            if (context is not { } ctx || string.IsNullOrWhiteSpace(ctx.SubscriberId))
            {
                return ProviderSubscriptionActionResult.Fail("La suscripción no tiene un id_suscriptor de TiloPay registrado.");
            }

            var subscriberId = ctx.SubscriberId!;
            var outcome = await EnsureProviderInactiveAsync(subscriberId, ctx.PlanId, tenantId, cancellationToken);

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();
            var subscription = await LoadTrackedSubscriptionAsync(tenantId, cancellationToken);

            AddOperationAudit(
                immediate
                    ? PlatformAuditActions.SubscriptionImmediateCancellationRequested
                    : PlatformAuditActions.SubscriptionCancellationRequested,
                tenantId, actorUserId, actorEmail, subscriberId,
                immediate
                    ? "Solicitud de cancelación inmediata (plataforma)."
                    : $"Solicitud de cancelación de renovación. Motivo: {Trunc(reason, 180) ?? "(sin motivo)"}",
                nowUtc);

            if (!outcome.Inactive)
            {
                // TiloPay respondió 200 pero la verificación no confirmó la baja (o sigue Activo):
                // NO se marca cancelada ni se corta acceso. Revisión manual. El status observado se
                // guarda igual (no se pierde el dato aunque no se pudiera actuar sobre él).
                if (subscription is not null)
                {
                    PersistProviderStatus(subscription, outcome.RawStatus, nowUtc);
                    subscription.FechaUltimaActualizacionUtc = nowUtc;
                }

                AddOperationAudit(
                    PlatformAuditActions.SubscriptionCancellationFailedManualReview,
                    tenantId, actorUserId, actorEmail, subscriberId,
                    $"CRÍTICO: no se pudo verificar la baja del suscriptor. {outcome.Message}. Estado provider {Sanitize(outcome.RawStatus)}. NO se cortó acceso ni se marcó cancelada.",
                    nowUtc);
                await _db.SaveChangesAsync(cancellationToken);
                return ProviderSubscriptionActionResult.Fail(
                    "TiloPay no confirmó la cancelación del suscriptor. El caso quedó en revisión y NO se cortó el acceso.");
            }

            AddOperationAudit(
                outcome.AlreadyInactive
                    ? PlatformAuditActions.SubscriptionCancellationAlreadyProviderInactive
                    : PlatformAuditActions.SubscriptionProviderCancellationVerified,
                tenantId, actorUserId, actorEmail, subscriberId,
                outcome.AlreadyInactive
                    ? "El suscriptor ya estaba inactivo en TiloPay (cancelación idempotente)."
                    : "Baja del suscriptor VERIFICADA en TiloPay (no habrá nuevos cobros).",
                nowUtc);

            DateTime? effectiveEndUtc = null;
            if (subscription is not null)
            {
                subscription.CancellationRequestedAtUtc ??= nowUtc;
                subscription.CancellationRequestedByUserId = actorUserId;
                subscription.CancellationReason = Trunc(reason, 250);
                subscription.ProviderCancelledAtUtc = nowUtc;
                PersistProviderStatus(subscription, outcome.RawStatus, nowUtc);
                subscription.ProviderPausedAtUtc = null; // una baja no es una pausa
                subscription.FechaUltimaActualizacionUtc = nowUtc;

                if (immediate)
                {
                    subscription.CancelAtPeriodEnd = false;
                    subscription.Estado = EstadoSuscripcion.Cancelada;
                    subscription.FechaCancelacionUtc = nowUtc;
                    subscription.FechaFin = nowUtc;
                    subscription.CancellationEffectiveAtUtc = nowUtc;
                    subscription.MotivoEstado = "Cancelación inmediata verificada en TiloPay (plataforma).";
                }
                else
                {
                    subscription.CancelAtPeriodEnd = true;
                    // Acceso vivo hasta el fin EFECTIVO ya pagado (máximo entre local y proveedor).
                    effectiveEndUtc = SubscriptionEffectiveDates.GetEffectiveEndUtc(
                        subscription.FechaFin,
                        subscription.ProviderExpiresAtUtc);
                    subscription.CancellationEffectiveAtUtc = effectiveEndUtc;
                    subscription.MotivoEstado = "Renovación cancelada: acceso activo hasta el fin del período ya pagado.";
                    // Estado se MANTIENE: la cancelación de renovación no corta el acceso.
                }
            }

            if (!immediate)
            {
                // PROGRAMADA (no finalizada): el corte real ocurre al vencer el período; ahí se
                // emite SubscriptionCancellationAtPeriodEndFinalized desde la reconciliación/worker.
                AddOperationAudit(
                    PlatformAuditActions.SubscriptionCancellationScheduledAtPeriodEnd,
                    tenantId, actorUserId, actorEmail, subscriberId,
                    $"Cancelación de renovación PROGRAMADA. Acceso hasta {FormatDate(effectiveEndUtc)} (UTC); se cerrará al vencer.",
                    nowUtc);
            }

            await _db.SaveChangesAsync(cancellationToken);

            if (immediate)
            {
                InvalidateAccess(tenantId);
                return ProviderSubscriptionActionResult.Ok("Suscripción cancelada y acceso cortado (verificado en TiloPay).");
            }

            return ProviderSubscriptionActionResult.Ok(
                $"Renovación cancelada. Tu acceso seguirá activo hasta {FormatDate(effectiveEndUtc)} (UTC). No se harán nuevos cobros.");
        }

        public async Task<ProviderSubscriptionActionResult> PauseAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            bool immediate = false,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled)
            {
                return ProviderSubscriptionActionResult.Fail("La integración de TiloPay Repeat Admin está deshabilitada.");
            }

            var context = await GetSubscriberContextAsync(tenantId, cancellationToken);
            if (context is not { } ctx || string.IsNullOrWhiteSpace(ctx.SubscriberId))
            {
                return ProviderSubscriptionActionResult.Fail("La suscripción no tiene un id_suscriptor de TiloPay registrado.");
            }

            var subscriberId = ctx.SubscriberId!;
            var before = await VerifyProviderStateAsync(ctx.PlanId, subscriberId, cancellationToken);

            bool verifiedPaused;
            var alreadyPaused = false;
            string? providerRaw;
            string? failMessage = null;

            if (before is { } b && b.State == ProviderSubscriberState.Paused)
            {
                verifiedPaused = true;
                alreadyPaused = true;
                providerRaw = b.Raw;
            }
            else if (before is { } bInactive && bInactive.State == ProviderSubscriberState.Inactive)
            {
                verifiedPaused = false;
                providerRaw = bInactive.Raw;
                failMessage = "el suscriptor ya está inactivo/eliminado en TiloPay: no se puede pausar";
            }
            else
            {
                var op = await PauseWithFallbackAsync(subscriberId, tenantId, cancellationToken);
                var after = await VerifyProviderStateAsync(ctx.PlanId, subscriberId, cancellationToken);
                if (after is { } a && a.State == ProviderSubscriberState.Paused)
                {
                    verifiedPaused = true;
                    providerRaw = a.Raw;
                }
                else
                {
                    verifiedPaused = false;
                    providerRaw = after is { } a2 ? a2.Raw : "(no verificable)";
                    failMessage = op.Succeeded
                        ? "TiloPay respondió éxito pero la verificación no confirmó Pausado"
                        : op.Message ?? "no se pudo pausar";
                }
            }

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();
            var subscription = await LoadTrackedSubscriptionAsync(tenantId, cancellationToken);

            AddOperationAudit(
                PlatformAuditActions.SubscriptionPauseRequested,
                tenantId, actorUserId, actorEmail, subscriberId,
                immediate ? "Solicitud de pausa con suspensión inmediata (plataforma)." : "Solicitud de pausa (mantiene acceso).",
                nowUtc);

            if (!verifiedPaused)
            {
                if (subscription is not null)
                {
                    PersistProviderStatus(subscription, providerRaw, nowUtc);
                    subscription.FechaUltimaActualizacionUtc = nowUtc;
                }

                AddOperationAudit(
                    PlatformAuditActions.SubscriptionPauseFailedManualReview,
                    tenantId, actorUserId, actorEmail, subscriberId,
                    $"No se pudo verificar la pausa. {failMessage}. Estado provider {Sanitize(providerRaw)}.",
                    nowUtc);
                await _db.SaveChangesAsync(cancellationToken);
                return ProviderSubscriptionActionResult.Fail("TiloPay no confirmó la pausa del suscriptor. El caso quedó en revisión.");
            }

            AddOperationAudit(
                alreadyPaused
                    ? PlatformAuditActions.SubscriptionPauseAlreadyProviderPaused
                    : PlatformAuditActions.SubscriptionProviderPauseVerified,
                tenantId, actorUserId, actorEmail, subscriberId,
                alreadyPaused ? "El suscriptor ya estaba pausado en TiloPay (idempotente)." : "Pausa VERIFICADA en TiloPay (status 3).",
                nowUtc);

            if (subscription is not null)
            {
                subscription.ProviderPausedAtUtc ??= nowUtc;
                PersistProviderStatus(subscription, providerRaw, nowUtc);
                subscription.FechaUltimaActualizacionUtc = nowUtc;

                if (immediate)
                {
                    subscription.Estado = EstadoSuscripcion.Suspendida;
                    subscription.MotivoEstado = "Suscripción pausada con suspensión inmediata (plataforma).";
                }
                else
                {
                    subscription.MotivoEstado = "Suscripción pausada en TiloPay. El acceso se mantiene hasta el fin del período ya pagado.";
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            if (immediate)
            {
                InvalidateAccess(tenantId);
                return ProviderSubscriptionActionResult.Ok("Suscripción pausada y acceso suspendido (verificado en TiloPay).");
            }

            return ProviderSubscriptionActionResult.Ok("Suscripción pausada en TiloPay. El acceso se mantiene hasta el fin del período ya pagado.");
        }

        public async Task<ProviderSubscriptionActionResult> ReactivateAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled)
            {
                return ProviderSubscriptionActionResult.Fail("La integración de TiloPay Repeat Admin está deshabilitada.");
            }

            var context = await GetSubscriberContextAsync(tenantId, cancellationToken);
            if (context is not { } ctx || string.IsNullOrWhiteSpace(ctx.SubscriberId))
            {
                return ProviderSubscriptionActionResult.Fail("La suscripción no tiene un id_suscriptor de TiloPay registrado.");
            }

            var subscriberId = ctx.SubscriberId!;
            var before = await VerifyProviderStateAsync(ctx.PlanId, subscriberId, cancellationToken);

            bool verifiedActive;
            var alreadyActive = false;
            var blockedDeleted = false;
            string? providerRaw;
            string? failMessage = null;

            if (before is { } bDeleted && bDeleted.State == ProviderSubscriberState.Inactive)
            {
                // Un suscriptor ELIMINADO no se reactiva a ciegas: preferir hosted checkout nuevo.
                verifiedActive = false;
                blockedDeleted = true;
                providerRaw = bDeleted.Raw;
                failMessage = "el suscriptor está eliminado en TiloPay: no se reactiva automáticamente";
            }
            else if (before is { } bActive && bActive.State == ProviderSubscriberState.Active)
            {
                verifiedActive = true;
                alreadyActive = true;
                providerRaw = bActive.Raw;
            }
            else if (before is null)
            {
                verifiedActive = false;
                providerRaw = "(no verificable)";
                failMessage = "no se pudo consultar el estado actual del suscriptor";
            }
            else
            {
                // Pausado o desconocido: intentar reactivar y verificar Activo.
                var op = await ReactivateWithFallbackAsync(subscriberId, tenantId, cancellationToken);
                var after = await VerifyProviderStateAsync(ctx.PlanId, subscriberId, cancellationToken);
                if (after is { } a && a.State == ProviderSubscriberState.Active)
                {
                    verifiedActive = true;
                    providerRaw = a.Raw;
                }
                else
                {
                    verifiedActive = false;
                    providerRaw = after is { } a2 ? a2.Raw : "(no verificable)";
                    failMessage = op.Succeeded
                        ? "TiloPay respondió éxito pero la verificación no confirmó Activo"
                        : op.Message ?? "no se pudo reactivar";
                }
            }

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();
            var subscription = await LoadTrackedSubscriptionAsync(tenantId, cancellationToken);

            AddOperationAudit(
                PlatformAuditActions.SubscriptionReactivateRequested,
                tenantId, actorUserId, actorEmail, subscriberId,
                "Solicitud de reactivación.",
                nowUtc);

            if (!verifiedActive)
            {
                if (subscription is not null)
                {
                    PersistProviderStatus(subscription, providerRaw, nowUtc);
                    subscription.FechaUltimaActualizacionUtc = nowUtc;
                }

                AddOperationAudit(
                    PlatformAuditActions.SubscriptionReactivateFailedManualReview,
                    tenantId, actorUserId, actorEmail, subscriberId,
                    $"No se pudo reactivar de forma segura. {failMessage}. Estado provider {Sanitize(providerRaw)}.",
                    nowUtc);
                await _db.SaveChangesAsync(cancellationToken);
                return ProviderSubscriptionActionResult.Fail(
                    blockedDeleted
                        ? "El suscriptor está eliminado en TiloPay y no puede reactivarse. Inicia una suscripción nueva."
                        : "TiloPay no confirmó la reactivación del suscriptor. El caso quedó en revisión.");
            }

            AddOperationAudit(
                alreadyActive
                    ? PlatformAuditActions.SubscriptionReactivateAlreadyProviderActive
                    : PlatformAuditActions.SubscriptionProviderReactivateVerified,
                tenantId, actorUserId, actorEmail, subscriberId,
                alreadyActive ? "El suscriptor ya estaba activo en TiloPay (idempotente)." : "Reactivación VERIFICADA en TiloPay (status 1).",
                nowUtc);

            if (subscription is not null)
            {
                subscription.Estado = EstadoSuscripcion.Activa;
                subscription.ProviderPausedAtUtc = null;
                subscription.CancelAtPeriodEnd = false;
                subscription.CancellationRequestedAtUtc = null;
                subscription.CancellationRequestedByUserId = null;
                subscription.CancellationReason = null;
                subscription.ProviderCancelledAtUtc = null;
                subscription.CancellationEffectiveAtUtc = null;
                subscription.FechaCancelacionUtc = null;
                PersistProviderStatus(subscription, providerRaw, nowUtc);
                subscription.FechaUltimaActualizacionUtc = nowUtc;
                subscription.MotivoEstado = "Suscripción reactivada y verificada en TiloPay.";
            }

            await _db.SaveChangesAsync(cancellationToken);
            InvalidateAccess(tenantId);

            return ProviderSubscriptionActionResult.Ok("Suscripción reactivada y verificada en TiloPay.");
        }

        public async Task<ProviderSubscriptionActionResult> ReactivateRenewalAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled)
            {
                return ProviderSubscriptionActionResult.Fail("La integración de TiloPay Repeat Admin está deshabilitada.");
            }

            // Guarda de contexto (solo lectura, antes del HTTP): debe ser una cancelación de
            // renovación AÚN vigente. Fuera de ese caso NO se reactiva a ciegas un suscriptor Delete.
            var guard = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.ProviderSubscriptionId != null)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .Select(s => new
                {
                    s.ProviderSubscriptionId,
                    s.TilopayRecurringPlanId,
                    s.CancelAtPeriodEnd,
                    s.FechaFin,
                    s.ProviderExpiresAtUtc,
                    s.CancellationEffectiveAtUtc
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (guard is null || string.IsNullOrWhiteSpace(guard.ProviderSubscriptionId))
            {
                return ProviderSubscriptionActionResult.Fail("La suscripción no tiene un id_suscriptor de TiloPay registrado.");
            }

            if (!guard.CancelAtPeriodEnd)
            {
                return ProviderSubscriptionActionResult.Fail("La suscripción no tiene una cancelación de renovación pendiente para reactivar.");
            }

            var effectiveEndUtc = guard.CancellationEffectiveAtUtc
                ?? SubscriptionEffectiveDates.GetEffectiveEndUtc(guard.FechaFin, guard.ProviderExpiresAtUtc);

            if (effectiveEndUtc is not { } effectiveEnd || effectiveEnd <= GetUtcNow())
            {
                // Período ya vencido: la reactivación de renovación no aplica; hay que suscribirse de nuevo.
                return ProviderSubscriptionActionResult.Fail("El período ya venció. Para continuar, iniciá una nueva suscripción.");
            }

            var subscriberId = guard.ProviderSubscriptionId!;

            // Reactivar el MISMO suscriptor (que está Delete a propósito) y verificar Active.
            var before = await VerifyProviderStateAsync(guard.TilopayRecurringPlanId, subscriberId, cancellationToken);

            bool verifiedActive;
            var alreadyActive = false;
            string? providerRaw;
            string? failMessage = null;

            if (before is { } bActive && bActive.State == ProviderSubscriberState.Active)
            {
                verifiedActive = true;
                alreadyActive = true;
                providerRaw = bActive.Raw;
            }
            else
            {
                var op = await ReactivateWithFallbackAsync(subscriberId, tenantId, cancellationToken);
                var after = await VerifyProviderStateAsync(guard.TilopayRecurringPlanId, subscriberId, cancellationToken);
                if (after is { } a && a.State == ProviderSubscriberState.Active)
                {
                    verifiedActive = true;
                    providerRaw = a.Raw;
                }
                else
                {
                    verifiedActive = false;
                    providerRaw = after is { } a2 ? a2.Raw : "(no verificable)";
                    failMessage = op.Succeeded
                        ? "TiloPay respondió éxito pero la verificación no confirmó Active"
                        : op.Message ?? "no se pudo reactivar la renovación";
                }
            }

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();
            var subscription = await LoadTrackedSubscriptionAsync(tenantId, cancellationToken);

            AddOperationAudit(
                PlatformAuditActions.SubscriptionRenewalReactivationRequested,
                tenantId, actorUserId, actorEmail, subscriberId,
                "Solicitud de reactivación de renovación cancelada (aún vigente).",
                nowUtc);

            if (!verifiedActive)
            {
                if (subscription is not null)
                {
                    PersistProviderStatus(subscription, providerRaw, nowUtc);
                    subscription.FechaUltimaActualizacionUtc = nowUtc;
                }

                AddOperationAudit(
                    PlatformAuditActions.SubscriptionRenewalReactivationFailedManualReview,
                    tenantId, actorUserId, actorEmail, subscriberId,
                    $"No se pudo reactivar la renovación. {failMessage}. Estado provider {Sanitize(providerRaw)}. NO se limpió la cancelación.",
                    nowUtc);
                await _db.SaveChangesAsync(cancellationToken);
                return ProviderSubscriptionActionResult.Fail(
                    "No pudimos completar la reactivación automáticamente. Soporte fue notificado.");
            }

            AddOperationAudit(
                PlatformAuditActions.SubscriptionRenewalReactivationVerified,
                tenantId, actorUserId, actorEmail, subscriberId,
                alreadyActive
                    ? "El suscriptor ya estaba activo en TiloPay: renovación reactivada (idempotente)."
                    : "Renovación reactivada y VERIFICADA en TiloPay (status 1).",
                nowUtc);

            if (subscription is not null)
            {
                // Se limpia la cancelación programada; la vigencia (FechaFin/expire) se mantiene.
                subscription.CancelAtPeriodEnd = false;
                subscription.CancellationRequestedAtUtc = null;
                subscription.CancellationRequestedByUserId = null;
                subscription.CancellationReason = null;
                subscription.ProviderCancelledAtUtc = null;
                subscription.CancellationEffectiveAtUtc = null;
                subscription.FechaCancelacionUtc = null;
                subscription.ProviderPausedAtUtc = null;
                subscription.Estado = EstadoSuscripcion.Activa;
                PersistProviderStatus(subscription, providerRaw, nowUtc);
                subscription.FechaUltimaActualizacionUtc = nowUtc;
                subscription.MotivoEstado = "Renovación reactivada por el cliente: la suscripción continúa normalmente.";
            }

            await _db.SaveChangesAsync(cancellationToken);
            InvalidateAccess(tenantId);

            return ProviderSubscriptionActionResult.Ok("Tu renovación fue reactivada. Tu suscripción continuará normalmente.");
        }

        public async Task<ProviderSubscriptionActionResult> SyncProviderStatusAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled)
            {
                return ProviderSubscriptionActionResult.Fail("La integración de TiloPay Repeat Admin está deshabilitada.");
            }

            var context = await GetSubscriberContextAsync(tenantId, cancellationToken);
            if (context is not { } ctx || string.IsNullOrWhiteSpace(ctx.SubscriberId))
            {
                return ProviderSubscriptionActionResult.Fail("La suscripción no tiene un id_suscriptor de TiloPay registrado.");
            }

            var subscriberId = ctx.SubscriberId!;

            // SOLO lectura contra TiloPay, FUERA de cualquier transacción.
            var observed = await VerifyProviderStateAsync(ctx.PlanId, subscriberId, cancellationToken);
            if (observed is not { } snapshot)
            {
                // Sin plan para verificar o error de red: no se persiste nada; se informa de forma segura.
                return ProviderSubscriptionActionResult.Fail(
                    "No pudimos consultar el estado del suscriptor en TiloPay. Intentá de nuevo en unos minutos.");
            }

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();
            var subscription = await LoadTrackedSubscriptionAsync(tenantId, cancellationToken);
            if (subscription is null)
            {
                return ProviderSubscriptionActionResult.Fail("No se encontró la suscripción para sincronizar.");
            }

            PersistProviderStatus(subscription, snapshot.Raw, nowUtc);
            subscription.FechaUltimaActualizacionUtc = nowUtc;

            // NUNCA se toca Estado ni el acceso: solo las banderas informativas del proveedor.
            switch (snapshot.State)
            {
                case ProviderSubscriberState.Paused:
                    subscription.ProviderPausedAtUtc ??= nowUtc;
                    break;
                case ProviderSubscriberState.Inactive:
                    subscription.ProviderCancelledAtUtc ??= nowUtc;
                    subscription.ProviderPausedAtUtc = null; // una baja no es una pausa
                    break;
                case ProviderSubscriberState.Active:
                    subscription.ProviderPausedAtUtc = null; // ya no está pausado
                    break;
                    // Unknown: se guarda solo el raw + timestamp, sin tocar banderas.
            }

            AddOperationAudit(
                PlatformAuditActions.SubscriptionProviderStatusSynced,
                tenantId, actorUserId, actorEmail, subscriberId,
                $"Estado del proveedor sincronizado manualmente. Provider {Sanitize(snapshot.Raw)} (clasificado {snapshot.State}).",
                nowUtc);

            await _db.SaveChangesAsync(cancellationToken);

            var label = snapshot.State switch
            {
                ProviderSubscriberState.Active => "Activo",
                ProviderSubscriberState.Paused => "Pausado",
                ProviderSubscriberState.Inactive => "Eliminado / inactivo",
                _ => "Desconocido"
            };
            return ProviderSubscriptionActionResult.Ok($"Estado del proveedor actualizado: {label}.");
        }

        /// <summary>Resultado de asegurar la baja del suscriptor con verificación obligatoria.</summary>
        private sealed record ProviderTerminationOutcome(bool Inactive, bool AlreadyInactive, string? RawStatus, string? Message);

        /// <summary>
        /// Da de baja el suscriptor y VERIFICA. Si ya estaba inactivo, idempotente sin re-llamar
        /// delete. Un 200 sin verificación NUNCA cuenta como baja (mismo criterio que el flujo de
        /// cambio de plan). Todo el HTTP ocurre aquí, antes de abrir el scope/transacción del tenant.
        /// </summary>
        private async Task<ProviderTerminationOutcome> EnsureProviderInactiveAsync(
            string subscriberId,
            int? planId,
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            var before = await VerifyProviderStateAsync(planId, subscriberId, cancellationToken);
            if (before is { } b && b.State == ProviderSubscriberState.Inactive)
            {
                return new ProviderTerminationOutcome(true, true, b.Raw, "el suscriptor ya estaba inactivo");
            }

            var op = await DeleteWithFallbackAsync(subscriberId, tenantId, cancellationToken);
            var after = await VerifyProviderStateAsync(planId, subscriberId, cancellationToken);

            if (after is not { } a)
            {
                return new ProviderTerminationOutcome(
                    false, false, "(no verificable)",
                    op.Succeeded
                        ? "TiloPay aceptó la baja pero la verificación no se pudo completar"
                        : op.Message ?? "baja fallida");
            }

            if (a.State == ProviderSubscriberState.Inactive)
            {
                return new ProviderTerminationOutcome(true, false, a.Raw, "baja verificada");
            }

            return new ProviderTerminationOutcome(
                false, false, a.Raw,
                op.Succeeded
                    ? "TiloPay respondió éxito pero el suscriptor sigue activo según getSuscriptorRepeat"
                    : op.Message ?? "el suscriptor sigue activo");
        }

        /// <summary>
        /// Verifica el estado del suscriptor contra getSuscriptorRepeat. Ausente ⇒ Inactive (no
        /// cobrable). Sin plan o error de red ⇒ null (no verificable): el caller decide, nunca asume.
        /// </summary>
        private async Task<(ProviderSubscriberState State, string? Raw)?> VerifyProviderStateAsync(
            int? planId,
            string subscriberId,
            CancellationToken cancellationToken)
        {
            if (planId is not { } resolvedPlanId)
            {
                _logger.LogWarning(
                    "Sin TilopayRecurringPlanId no se puede verificar el suscriptor. Suffix {Suffix}.",
                    SensitiveDataMasker.MaskReference(subscriberId));
                return null;
            }

            try
            {
                var subscribers = await _adminService.GetSuscriptorRepeatAsync(resolvedPlanId, cancellationToken);
                var match = subscribers.FirstOrDefault(s =>
                    string.Equals(s.SubscriberId, subscriberId, StringComparison.OrdinalIgnoreCase));

                return match is null
                    ? (ProviderSubscriberState.Inactive, "(ausente)")
                    : (ProviderSubscriberStatusRules.Classify(match.Status), match.Status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "No se pudo verificar el estado del suscriptor en TiloPay. Suffix {Suffix}.",
                    SensitiveDataMasker.MaskReference(subscriberId));
                return null;
            }
        }

        private async Task<TilopayAdminOperationResult> DeleteWithFallbackAsync(string subscriberId, Guid tenantId, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _adminService.DeleteSubscriberAsync(subscriberId, cancellationToken);
                if (!result.Succeeded)
                {
                    // Fallback documentado: editSuscriptorRepeat estado 4 = Eliminado.
                    result = await _adminService.EditSubscriberStatusAsync(subscriberId, TilopaySubscriberStatus.Deleted, cancellationToken);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción dando de baja el suscriptor en TiloPay. TenantId {TenantId}.", tenantId);
                return TilopayAdminOperationResult.Fail("Excepción al dar de baja el suscriptor.");
            }
        }

        private async Task<TilopayAdminOperationResult> PauseWithFallbackAsync(string subscriberId, Guid tenantId, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _adminService.PauseSubscriberAsync(subscriberId, cancellationToken);
                if (!result.Succeeded)
                {
                    // Fallback documentado: editSuscriptorRepeat estado 3 = Pausado.
                    result = await _adminService.EditSubscriberStatusAsync(subscriberId, TilopaySubscriberStatus.Paused, cancellationToken);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción pausando el suscriptor en TiloPay. TenantId {TenantId}.", tenantId);
                return TilopayAdminOperationResult.Fail("Excepción al pausar el suscriptor.");
            }
        }

        private async Task<TilopayAdminOperationResult> ReactivateWithFallbackAsync(string subscriberId, Guid tenantId, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _adminService.ReactivateSubscriberAsync(subscriberId, cancellationToken);
                if (!result.Succeeded)
                {
                    // Fallback documentado: editSuscriptorRepeat estado 1 = Activo.
                    result = await _adminService.EditSubscriberStatusAsync(subscriberId, TilopaySubscriberStatus.Active, cancellationToken);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción reactivando el suscriptor en TiloPay. TenantId {TenantId}.", tenantId);
                return TilopayAdminOperationResult.Fail("Excepción al reactivar el suscriptor.");
            }
        }

        private async Task<(string? SubscriberId, int? PlanId)?> GetSubscriberContextAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            var row = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.ProviderSubscriptionId != null)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .Select(s => new { s.ProviderSubscriptionId, s.TilopayRecurringPlanId })
                .FirstOrDefaultAsync(cancellationToken);

            return row is null ? null : (row.ProviderSubscriptionId, row.TilopayRecurringPlanId);
        }

        private Task<Suscripcion?> LoadTrackedSubscriptionAsync(Guid tenantId, CancellationToken cancellationToken) =>
            _db.Suscripciones
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId && s.ProviderSubscriptionId != null)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .FirstOrDefaultAsync(cancellationToken);

        private void InvalidateAccess(Guid tenantId) => _accessCache?.Invalidate(tenantId);

        private static string Sanitize(string? raw) => ProviderSubscriberStatusRules.Sanitize(raw);

        /// <summary>
        /// Guarda el snapshot del status del proveedor (raw LITERAL truncado + timestamp) SIEMPRE que
        /// se consultó a TiloPay, incluso si el resultado dejó el caso en revisión manual. Así el
        /// diagnóstico nunca pierde el valor observado (p.ej. "Pause By Commerce").
        /// </summary>
        private static void PersistProviderStatus(Suscripcion subscription, string? rawStatus, DateTime nowUtc)
        {
            subscription.ProviderStatusRaw = Trunc(rawStatus, 40);
            subscription.ProviderStatusLastSyncedUtc = nowUtc;
        }

        private static string FormatDate(DateTime? value) =>
            value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "el fin del período";

        private static string? Trunc(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);

        private void AddOperationAudit(
            string action,
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            string subscriberId,
            string reason,
            DateTime nowUtc)
        {
            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = string.IsNullOrWhiteSpace(actorUserId) ? "system" : actorUserId,
                ActorEmail = string.IsNullOrWhiteSpace(actorEmail) ? "system" : actorEmail,
                Action = action,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = SensitiveDataMasker.MaskReference(subscriberId),
                TenantId = tenantId,
                Reason = reason.Length <= 500 ? reason : reason[..500],
                CreatedAtUtc = nowUtc
            });
        }

        private DateTime GetUtcNow() => _clock.NowOffset().UtcDateTime;
    }
}
