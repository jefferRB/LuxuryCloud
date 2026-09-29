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
    /// <summary>
    /// Gestiona la cancelación SALIENTE del suscriptor recurrente del ADD-ON de WhatsApp en TiloPay.
    /// Espeja las reglas money-critical del plan base (<see cref="ProviderSubscriptionManager"/>) pero
    /// sobre <see cref="TenantSubscriptionAddon"/>: toda baja se VERIFICA contra getSuscriptorRepeat
    /// (un HTTP 200 nunca basta); si no se puede confirmar, el caso queda PENDIENTE con backoff +
    /// alerta crítica (nunca doble cobro silencioso). NUNCA toca el plan base.
    ///
    /// Cubre dos escenarios distinguidos por el suscriptor objetivo:
    ///  - Strategy B (cambio de paquete WA400→WA800→WA1200 o bajada): el suscriptor pendiente es el
    ///    ANTERIOR (huérfano); el ACTUAL es el nuevo y sigue activo.
    ///  - Cancelación de renovación (cliente / cascada del plan base): el pendiente ES el actual; al
    ///    verificarse la baja se programa CancelAtPeriodEnd (el uso sigue hasta FechaFin).
    /// </summary>
    public interface IAddonSubscriptionManager
    {
        bool IsEnabled { get; }

        /// <summary>
        /// Cancela la RENOVACIÓN del add-on activo del tenant: da de baja el suscriptor ACTUAL en
        /// TiloPay con verificación y, si se confirma, programa CancelAtPeriodEnd (el uso sigue hasta
        /// el fin del período ya pagado). Idempotente. Si no se puede verificar la baja, deja el caso
        /// pendiente + alerta y NO cambia el acceso (revisión manual).
        /// </summary>
        Task<ProviderSubscriptionActionResult> RequestAddonCancellationAtPeriodEndAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            string? reason,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reintenta la cancelación SALIENTE pendiente del suscriptor del add-on (huérfano de un
        /// cambio de paquete, o el actual de una cancelación de renovación). Post-commit del webhook y
        /// desde la reconciliación. El presupuesto/backoff vive en la fila del add-on; solo un intento
        /// REAL contra TiloPay lo mueve. Devuelve si realmente se llamó al proveedor y el resultado.
        /// </summary>
        Task<ProviderCancellationAttemptResult> TryCancelPendingAddonSubscriberAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// CASCADA: el plan base se canceló, así que el add-on NO debe seguir cobrando (regla de
        /// negocio: nunca un add-on vivo sin SaaS). Marca la cancelación de renovación del add-on y
        /// deja su suscriptor pendiente de baja SIEMPRE (aunque el API admin esté apagado, para que la
        /// reconciliación/Mission Control lo alerten), luego intenta la baja verificada best-effort.
        /// Con <paramref name="immediate"/> corta el uso del add-on ya (el base cortó acceso ahora).
        /// No-op si el tenant no tiene add-on activo.
        /// </summary>
        Task ScheduleAddonCancellationForBaseCancellationAsync(
            Guid tenantId,
            string actorUserId,
            string? reason,
            bool immediate,
            CancellationToken cancellationToken = default);
    }

    public sealed class AddonSubscriptionManager : IAddonSubscriptionManager
    {
        // Backoff de reintentos reales contra TiloPay: 5m → 15m → 30m → 1h → 6h → diario (nunca para).
        private static readonly TimeSpan[] RetryBackoff =
        {
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(24)
        };

        private readonly ApplicationDbContext _db;
        private readonly ITilopayRepeatAdminService _adminService;
        private readonly ITenantExecutionContextAccessor _tenantExecutionContextAccessor;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly ILogger<AddonSubscriptionManager> _logger;

        public AddonSubscriptionManager(
            ApplicationDbContext db,
            ITilopayRepeatAdminService adminService,
            ITenantExecutionContextAccessor tenantExecutionContextAccessor,
            IBusinessDateTimeProvider clock,
            ILogger<AddonSubscriptionManager> logger)
        {
            _db = db;
            _adminService = adminService;
            _tenantExecutionContextAccessor = tenantExecutionContextAccessor;
            _clock = clock;
            _logger = logger;
        }

        public bool IsEnabled => _adminService.IsEnabled;

        public async Task<ProviderSubscriptionActionResult> RequestAddonCancellationAtPeriodEndAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled)
            {
                return ProviderSubscriptionActionResult.Fail("La integración de TiloPay Repeat Admin está deshabilitada.");
            }

            // Contexto SOLO lectura (antes del HTTP): suscriptor + plan del add-on actual.
            var ctx = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(a => a.TenantId == tenantId)
                .OrderByDescending(a => a.UpdatedAtUtc)
                .Select(a => new
                {
                    a.Estado,
                    a.ProviderSubscriptionId,
                    a.TilopayRecurringPlanId,
                    a.FechaFin
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (ctx is null ||
                (ctx.Estado != EstadoSuscripcion.Activa &&
                 ctx.Estado != EstadoSuscripcion.Morosa &&
                 ctx.Estado != EstadoSuscripcion.Trial))
            {
                return ProviderSubscriptionActionResult.Fail("No hay un add-on de WhatsApp activo para cancelar.");
            }

            var effectiveEndUtc = ctx.FechaFin;

            // Add-on MANUAL (sin suscriptor recurrente): no hay nada que cancelar en TiloPay; se
            // programa el fin de renovación local (expira solo en FechaFin) sin llamar al proveedor.
            if (string.IsNullOrWhiteSpace(ctx.ProviderSubscriptionId))
            {
                using var manualScope = _tenantExecutionContextAccessor.BeginScope(tenantId);
                var manualNow = GetUtcNow();
                var manualAddon = await LoadTrackedAddonAsync(tenantId, cancellationToken);
                if (manualAddon is not null)
                {
                    ScheduleLocalRenewalCancellation(manualAddon, actorUserId, reason, effectiveEndUtc, manualNow);
                    manualAddon.ProviderCancellation = ProviderCancellationState.NotRequired;
                }

                AddAudit(
                    PlatformAuditActions.AddonCancellationScheduledAtPeriodEnd,
                    tenantId, actorUserId, actorEmail, entityId: manualAddon?.Id.ToString(),
                    $"Cancelación de renovación de add-on MANUAL programada (sin suscriptor recurrente). Uso hasta {FormatDate(effectiveEndUtc)} (UTC).",
                    manualNow);
                await _db.SaveChangesAsync(cancellationToken);
                return ProviderSubscriptionActionResult.Ok(
                    $"Listo. El paquete de WhatsApp seguirá activo hasta {FormatDate(effectiveEndUtc)} (UTC) y no se renovará.");
            }

            var subscriberId = ctx.ProviderSubscriptionId!;

            // HTTP fuera de cualquier transacción: baja + verificación obligatoria.
            var outcome = await CancelAndVerifyAsync(subscriberId, ctx.TilopayRecurringPlanId, tenantId, cancellationToken);

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();
            var addon = await LoadTrackedAddonAsync(tenantId, cancellationToken);

            AddAudit(
                PlatformAuditActions.AddonCancellationRequested,
                tenantId, actorUserId, actorEmail, addon?.Id.ToString(),
                $"Solicitud de cancelación de renovación del add-on. Motivo: {Trunc(reason, 180) ?? "(sin motivo)"}",
                nowUtc);

            if (!outcome.Verified)
            {
                // No se confirmó la baja: NO se cambia el acceso. Queda pendiente + backoff + alerta.
                if (addon is not null)
                {
                    StashPendingCancellation(addon, subscriberId, ctx.TilopayRecurringPlanId, nowUtc, firstAttemptFailed: true);
                    addon.CancellationRequestedByUserId = Trunc(actorUserId, 450);
                    addon.CancellationReason = Trunc(reason, 250);
                    addon.UpdatedAtUtc = nowUtc;
                }

                AddAudit(
                    PlatformAuditActions.AddonProviderCancellationFailedManualReview,
                    tenantId, actorUserId, actorEmail, addon?.Id.ToString(),
                    $"CRÍTICO: no se pudo verificar la baja del suscriptor del add-on. {outcome.Message}. NO se cambió el acceso; se reintentará.",
                    nowUtc);
                await _db.SaveChangesAsync(cancellationToken);
                return ProviderSubscriptionActionResult.Fail(
                    "No pudimos confirmar la cancelación del paquete de WhatsApp. Soporte fue notificado y no se cambió tu acceso.");
            }

            // Baja VERIFICADA: se programa el fin de renovación (el uso sigue hasta FechaFin).
            if (addon is not null)
            {
                ScheduleLocalRenewalCancellation(addon, actorUserId, reason, effectiveEndUtc, nowUtc);
                ClearPendingCancellation(addon);
                addon.ProviderCancellation = ProviderCancellationState.Cancelled;
                addon.ProviderCancellationSubscriptionId = subscriberId;
                addon.ProviderCancelledAtUtc = nowUtc;
                addon.UpdatedAtUtc = nowUtc;
            }

            AddAudit(
                outcome.AlreadyInactive
                    ? PlatformAuditActions.AddonProviderCancellationAlreadyInactive
                    : PlatformAuditActions.AddonProviderCancellationVerified,
                tenantId, actorUserId, actorEmail, addon?.Id.ToString(),
                outcome.AlreadyInactive
                    ? "El suscriptor del add-on ya estaba inactivo en TiloPay (cancelación idempotente)."
                    : $"Baja del suscriptor del add-on VERIFICADA en TiloPay. Uso hasta {FormatDate(effectiveEndUtc)} (UTC); no se renovará.",
                nowUtc);
            await _db.SaveChangesAsync(cancellationToken);

            return ProviderSubscriptionActionResult.Ok(
                $"Listo. El paquete de WhatsApp seguirá activo hasta {FormatDate(effectiveEndUtc)} (UTC) y no se renovará.");
        }

        public async Task<ProviderCancellationAttemptResult> TryCancelPendingAddonSubscriberAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            // Cancelación AUTOMÁTICA por defecto cuando el API admin de TiloPay está habilitado
            // (TilopayRepeatAdmin:Enabled=true). El modo manual NO es el camino principal: es solo el
            // fallback cuando el API está apagado — ahí la fila queda ProviderCancellation=Pending y
            // la reconciliación/Mission Control alertan (NotCalled no gasta presupuesto de reintentos).
            if (!_adminService.IsEnabled)
            {
                return ProviderCancellationAttemptResult.NotCalled(
                    "El API admin de TiloPay está deshabilitado; el suscriptor del add-on queda pendiente para cancelación manual + alerta.");
            }

            var nowUtc = GetUtcNow();

            var pending = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(a =>
                    a.TenantId == tenantId &&
                    a.ProviderCancellation == ProviderCancellationState.PendingManualCancellation &&
                    a.PendingCancellationProviderSubscriptionId != null &&
                    (a.ProviderCancellationNextRetryUtc == null || a.ProviderCancellationNextRetryUtc <= nowUtc))
                .Select(a => new
                {
                    a.Id,
                    a.PendingCancellationProviderSubscriptionId,
                    a.PendingCancellationTilopayRecurringPlanId,
                    a.ProviderSubscriptionId
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (pending is null || string.IsNullOrWhiteSpace(pending.PendingCancellationProviderSubscriptionId))
            {
                return ProviderCancellationAttemptResult.NotCalled(
                    "No hay cancelación de suscriptor de add-on pendiente y elegible.");
            }

            var targetSubscriberId = pending.PendingCancellationProviderSubscriptionId!;
            // ¿El objetivo es el suscriptor ACTUAL? → es una cancelación de renovación (cliente/cascada).
            // Si difiere → es el huérfano de un cambio de paquete (Strategy B): el actual sigue activo.
            var isCurrentSubscriberCancellation = string.Equals(
                targetSubscriberId.Trim(),
                pending.ProviderSubscriptionId?.Trim(),
                StringComparison.OrdinalIgnoreCase);

            var outcome = await CancelAndVerifyAsync(
                targetSubscriberId,
                pending.PendingCancellationTilopayRecurringPlanId,
                tenantId,
                cancellationToken);

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var addon = await LoadTrackedAddonAsync(tenantId, cancellationToken);
            if (addon is null)
            {
                return ProviderCancellationAttemptResult.NotCalled("El add-on desapareció durante la cancelación.");
            }

            if (outcome.Verified)
            {
                ClearPendingCancellation(addon);

                if (isCurrentSubscriberCancellation)
                {
                    // La baja es del suscriptor VIGENTE: el estado de cancelación sí describe esta fila.
                    addon.ProviderCancellation = ProviderCancellationState.Cancelled;
                    addon.ProviderCancellationSubscriptionId = targetSubscriberId;
                    addon.ProviderCancelledAtUtc = nowUtc;
                }
                else
                {
                    // Strategy B: se dio de baja el suscriptor ANTERIOR (huérfano). El add-on ACTIVO no
                    // está cancelado — marcarlo como tal dejaba a la cascada del plan base creyendo que
                    // el suscriptor vigente ya no cobraba. Va a los campos de auditoría del reemplazado.
                    addon.ProviderCancellation = ProviderCancellationState.NotRequired;
                    addon.ProviderCancellationSubscriptionId = null;
                    addon.ProviderCancelledAtUtc = null;
                    addon.PreviousProviderSubscriptionId = targetSubscriberId;
                    addon.PreviousProviderCancelledAtUtc = nowUtc;
                }

                if (isCurrentSubscriberCancellation && !addon.CancelAtPeriodEnd)
                {
                    // Cancelación de renovación confirmada recién ahora: programar el fin local.
                    addon.CancelAtPeriodEnd = true;
                    addon.CancellationEffectiveAtUtc ??= addon.FechaFin;
                }

                addon.UpdatedAtUtc = nowUtc;

                AddAudit(
                    isCurrentSubscriberCancellation
                        ? PlatformAuditActions.AddonProviderCancellationVerified
                        : PlatformAuditActions.AddonUpgradeOldSubscriberCancellationCompleted,
                    tenantId, "system", "system", addon.Id.ToString(),
                    isCurrentSubscriberCancellation
                        ? $"Baja del suscriptor del add-on VERIFICADA en TiloPay. SubSuffix {SensitiveDataMasker.MaskReference(targetSubscriberId)}."
                        : $"Strategy B add-on: suscriptor anterior cancelado y verificado en TiloPay. SubSuffix {SensitiveDataMasker.MaskReference(targetSubscriberId)}.",
                    nowUtc);

                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Cancelación de suscriptor de add-on VERIFICADA. TenantId {TenantId}. AddonId {AddonId}. Huerfano {EsHuerfano}.",
                    tenantId, addon.Id, !isCurrentSubscriberCancellation);

                return new ProviderCancellationAttemptResult
                {
                    ProviderCalled = true,
                    Cancelled = true,
                    Message = outcome.Message
                };
            }

            // Falló/no verificado: consume presupuesto, escala backoff y deja alerta crítica.
            addon.ProviderCancellationAttemptCount += 1;
            addon.ProviderCancellationLastAttemptUtc = nowUtc;
            addon.ProviderCancellationNextRetryUtc = nowUtc.Add(ResolveBackoff(addon.ProviderCancellationAttemptCount));
            addon.UpdatedAtUtc = nowUtc;

            AddAudit(
                isCurrentSubscriberCancellation
                    ? PlatformAuditActions.AddonProviderCancellationFailedManualReview
                    : PlatformAuditActions.AddonUpgradeOldSubscriberCancellationFailed,
                tenantId, "system", "system", addon.Id.ToString(),
                $"CRÍTICO: no se pudo cancelar el suscriptor del add-on {SensitiveDataMasker.MaskReference(targetSubscriberId)} en TiloPay. Riesgo de doble cobro del add-on. Intento {addon.ProviderCancellationAttemptCount}. Detalle: {outcome.Message}",
                nowUtc);

            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogError(
                "Falló la cancelación del suscriptor del add-on. TenantId {TenantId}. AddonId {AddonId}. Intento {Intento}.",
                tenantId, addon.Id, addon.ProviderCancellationAttemptCount);

            return new ProviderCancellationAttemptResult
            {
                ProviderCalled = true,
                Cancelled = false,
                VerificationFailed = outcome.VerificationFailed,
                Message = outcome.Message
            };
        }

        public async Task ScheduleAddonCancellationForBaseCancellationAsync(
            Guid tenantId,
            string actorUserId,
            string? reason,
            bool immediate,
            CancellationToken cancellationToken = default)
        {
            var hasPendingProviderCancellation = false;

            using (var scope = _tenantExecutionContextAccessor.BeginScope(tenantId))
            {
                var nowUtc = GetUtcNow();
                var addon = await LoadTrackedAddonAsync(tenantId, cancellationToken);
                if (addon is null ||
                    (addon.Estado != EstadoSuscripcion.Activa &&
                     addon.Estado != EstadoSuscripcion.Morosa &&
                     addon.Estado != EstadoSuscripcion.Trial))
                {
                    return;
                }

                var effectiveReason = string.IsNullOrWhiteSpace(reason)
                    ? "Cascada: el plan base se canceló; el add-on no debe seguir cobrando."
                    : reason;

                // Deja pendiente la baja del suscriptor recurrente (si lo hay) SIEMPRE, aunque el API
                // admin esté apagado: así la reconciliación/Mission Control lo alertan y reintentan.
                // La baja solo se salta si está confirmada PARA EL SUSCRIPTOR VIGENTE. Comparar solo
                // contra ProviderCancellation==Cancelled dejaba pasar el caso money-critical: tras un
                // cambio de paquete (WA400→WA800) la fila activa quedaba Cancelled por la baja del
                // suscriptor VIEJO, así que la cascada no daba de baja el WA800 y TiloPay seguía
                // cobrándolo para siempre. Filas antiguas (id null) no se consideran cubiertas.
                if (!string.IsNullOrWhiteSpace(addon.ProviderSubscriptionId) &&
                    !IsCurrentSubscriberAlreadyCancelled(addon))
                {
                    addon.PendingCancellationProviderSubscriptionId = addon.ProviderSubscriptionId;
                    addon.PendingCancellationTilopayRecurringPlanId = addon.TilopayRecurringPlanId;
                    addon.ProviderCancellation = ProviderCancellationState.PendingManualCancellation;
                    hasPendingProviderCancellation = true;
                }

                addon.CancelAtPeriodEnd = true;
                addon.CancellationRequestedByUserId = Trunc(actorUserId, 450);
                addon.CancellationReason = Trunc(effectiveReason, 250);

                if (immediate)
                {
                    addon.Estado = EstadoSuscripcion.Cancelada;
                    addon.FechaFin = nowUtc;
                    addon.FechaCancelacionUtc = nowUtc;
                    addon.CancellationEffectiveAtUtc = nowUtc;
                }
                else
                {
                    addon.CancellationEffectiveAtUtc = addon.FechaFin;
                }

                addon.UpdatedAtUtc = nowUtc;

                AddAudit(
                    PlatformAuditActions.AddonCancellationScheduledAtPeriodEnd,
                    tenantId, actorUserId, "system", addon.Id.ToString(),
                    immediate
                        ? "Cascada de cancelación inmediata del plan base: add-on cortado y suscriptor pendiente de baja."
                        : $"Cascada de cancelación del plan base: renovación del add-on cancelada, uso hasta {FormatDate(addon.FechaFin)} (UTC).",
                    nowUtc);

                await _db.SaveChangesAsync(cancellationToken);
            }

            // Intento AUTOMÁTICO de la baja verificada (fuera del scope anterior), por defecto cuando
            // el API está habilitado. Si está apagado, queda pendiente y la reconciliación reintenta.
            if (hasPendingProviderCancellation && _adminService.IsEnabled)
            {
                try
                {
                    await TryCancelPendingAddonSubscriberAsync(tenantId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cascada: no se pudo cancelar el suscriptor del add-on. TenantId {TenantId}.", tenantId);
                }
            }
        }

        // ── Núcleo compartido: baja + verificación obligatoria (un 200 nunca basta) ──

        private sealed record CancelVerifyOutcome(bool Verified, bool AlreadyInactive, bool VerificationFailed, string? Message);

        private async Task<CancelVerifyOutcome> CancelAndVerifyAsync(
            string subscriberId,
            int? planId,
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            // Idempotente: si ya está inactivo, no re-llamar delete.
            var before = await VerifyStillActiveAsync(planId, subscriberId, cancellationToken);
            if (before is { } b && !b.StillActive && b.Verifiable)
            {
                return new CancelVerifyOutcome(true, true, false, "el suscriptor del add-on ya estaba inactivo");
            }

            TilopayAdminOperationResult op;
            try
            {
                op = await _adminService.DeleteSubscriberAsync(subscriberId, cancellationToken);
                if (!op.Succeeded)
                {
                    op = await _adminService.EditSubscriberStatusAsync(
                        subscriberId, TilopaySubscriberStatus.Deleted, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción cancelando suscriptor del add-on. TenantId {TenantId}.", tenantId);
                op = TilopayAdminOperationResult.Fail("Excepción al dar de baja el suscriptor del add-on.");
            }

            var after = await VerifyStillActiveAsync(planId, subscriberId, cancellationToken);

            if (after is not { } a || !a.Verifiable)
            {
                // Sin verificación NO hay baja (aunque el proveedor haya dicho 200): reintento.
                return new CancelVerifyOutcome(
                    false, false, op.Succeeded,
                    op.Succeeded
                        ? "TiloPay aceptó la baja pero la verificación no se pudo completar"
                        : op.Message ?? "baja del add-on fallida");
            }

            if (!a.StillActive)
            {
                return new CancelVerifyOutcome(true, false, false, "baja del add-on verificada");
            }

            return new CancelVerifyOutcome(
                false, false, op.Succeeded,
                op.Succeeded
                    ? "TiloPay respondió éxito pero el suscriptor del add-on sigue activo según getSuscriptorRepeat"
                    : op.Message ?? "el suscriptor del add-on sigue activo");
        }

        /// <summary>
        /// True/false si se pudo VERIFICAR; Verifiable=false cuando no hay plan o falló la consulta
        /// (el caller nunca asume una baja no confirmada). Ausente ⇒ no cobrable (StillActive=false).
        /// </summary>
        private async Task<(bool StillActive, bool Verifiable)?> VerifyStillActiveAsync(
            int? planId,
            string subscriberId,
            CancellationToken cancellationToken)
        {
            if (planId is not { } resolvedPlanId)
            {
                return (true, false);
            }

            try
            {
                var subscribers = await _adminService.GetSuscriptorRepeatAsync(resolvedPlanId, cancellationToken);
                var match = subscribers.FirstOrDefault(s =>
                    string.Equals(s.SubscriberId, subscriberId, StringComparison.OrdinalIgnoreCase));

                return match is null
                    ? (false, true) // ausente: no cobrable
                    : (ProviderSubscriberStatusRules.MayStillCharge(match.Status), true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "No se pudo verificar el suscriptor del add-on en TiloPay. Suffix {Suffix}.",
                    SensitiveDataMasker.MaskReference(subscriberId));
                return (true, false);
            }
        }

        // ── Helpers de estado local ──

        private static void StashPendingCancellation(
            TenantSubscriptionAddon addon,
            string subscriberId,
            int? planId,
            DateTime nowUtc,
            bool firstAttemptFailed)
        {
            addon.PendingCancellationProviderSubscriptionId = subscriberId;
            addon.PendingCancellationTilopayRecurringPlanId = planId;
            addon.ProviderCancellation = ProviderCancellationState.PendingManualCancellation;
            if (firstAttemptFailed)
            {
                addon.ProviderCancellationAttemptCount += 1;
                addon.ProviderCancellationLastAttemptUtc = nowUtc;
                addon.ProviderCancellationNextRetryUtc = nowUtc.Add(ResolveBackoff(addon.ProviderCancellationAttemptCount));
            }
        }

        /// <summary>
        /// True SOLO con evidencia de que el suscriptor VIGENTE del add-on ya está dado de baja.
        /// Un <see cref="ProviderCancellationState.Cancelled"/> sin
        /// <see cref="TenantSubscriptionAddon.ProviderCancellationSubscriptionId"/> que coincida NO
        /// cuenta: pudo quedar de la baja del suscriptor anterior en una transición de paquete.
        /// </summary>
        public static bool IsCurrentSubscriberAlreadyCancelled(TenantSubscriptionAddon addon)
        {
            ArgumentNullException.ThrowIfNull(addon);

            return addon.ProviderCancellation == ProviderCancellationState.Cancelled &&
                   !string.IsNullOrWhiteSpace(addon.ProviderSubscriptionId) &&
                   string.Equals(
                       addon.ProviderCancellationSubscriptionId?.Trim(),
                       addon.ProviderSubscriptionId?.Trim(),
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void ClearPendingCancellation(TenantSubscriptionAddon addon)
        {
            addon.PendingCancellationProviderSubscriptionId = null;
            addon.PendingCancellationTilopayRecurringPlanId = null;
            addon.ProviderCancellationNextRetryUtc = null;
        }

        private static void ScheduleLocalRenewalCancellation(
            TenantSubscriptionAddon addon,
            string actorUserId,
            string? reason,
            DateTime? effectiveEndUtc,
            DateTime nowUtc)
        {
            addon.CancelAtPeriodEnd = true;
            addon.CancellationEffectiveAtUtc = effectiveEndUtc ?? addon.FechaFin;
            addon.CancellationRequestedByUserId = Trunc(actorUserId, 450);
            addon.CancellationReason = Trunc(reason, 250);
            addon.UpdatedAtUtc = nowUtc;
        }

        private static TimeSpan ResolveBackoff(int attemptCount)
        {
            var index = Math.Clamp(attemptCount - 1, 0, RetryBackoff.Length - 1);
            return RetryBackoff[index];
        }

        private Task<TenantSubscriptionAddon?> LoadTrackedAddonAsync(Guid tenantId, CancellationToken cancellationToken) =>
            _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .Where(a => a.TenantId == tenantId)
                .OrderByDescending(a => a.UpdatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

        private void AddAudit(
            string action,
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            string? entityId,
            string reason,
            DateTime nowUtc)
        {
            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = string.IsNullOrWhiteSpace(actorUserId) ? "system" : actorUserId,
                ActorEmail = string.IsNullOrWhiteSpace(actorEmail) ? "system" : actorEmail,
                Action = action,
                EntityType = PlatformAuditEntityTypes.WhatsAppAddon,
                EntityId = entityId,
                TenantId = tenantId,
                Reason = reason.Length <= 500 ? reason : reason[..500],
                CreatedAtUtc = nowUtc
            });
        }

        private static string FormatDate(DateTime? value) =>
            value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "el fin del período";

        private static string? Trunc(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);

        private DateTime GetUtcNow() => _clock.NowOffset().UtcDateTime;
    }
}
