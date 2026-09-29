using System.Security.Claims;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tilopay;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.Platform
{
    /// <summary>
    /// Gestión del suscriptor recurrente de TiloPay (cancelar renovación / cancelar inmediata /
    /// pausar / reactivar) sobre la suscripción de un tenant. SOLO plataforma (super admin).
    ///
    /// El controller NUNCA llama a TiloPay ni duplica lógica: delega TODO en
    /// <see cref="IProviderSubscriptionManager"/> (verificación obligatoria, idempotencia,
    /// auditoría, tenant isolation). La lectura del estado local↔proveedor es cross-tenant a
    /// propósito (SuperAdmin) y por eso este archivo está en la allowlist de EndpointBindingSecurity.
    /// </summary>
    [Authorize(Policy = PlatformAuthorizationPolicies.PlatformSuperAdmin)]
    [Route("Platform/ProviderSubscription")]
    public sealed class PlatformProviderSubscriptionController : Controller
    {
        private readonly IProviderSubscriptionManager _manager;
        private readonly UserManager<AppUsuario> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly ITilopayRepeatAdminService _adminService;

        public PlatformProviderSubscriptionController(
            IProviderSubscriptionManager manager,
            UserManager<AppUsuario> userManager,
            ApplicationDbContext context,
            ITilopayRepeatAdminService adminService)
        {
            _manager = manager;
            _userManager = userManager;
            _context = context;
            _adminService = adminService;
        }

        /// <summary>Vista de estado local↔proveedor + acciones para un tenant.</summary>
        [HttpGet("manage")]
        public async Task<IActionResult> Manage(Guid tenantId, CancellationToken cancellationToken)
        {
            if (tenantId == Guid.Empty)
            {
                return View(new ProviderSubscriptionLifecycleViewModel { AdminEnabled = _adminService.IsEnabled });
            }

            var model = await BuildViewModelAsync(tenantId, cancellationToken);
            return View(model);
        }

        [HttpPost("cancel-renovacion")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> CancelRenovacion(Guid tenantId, CancellationToken cancellationToken) =>
            RunAsync(tenantId, (m, actor, email) => m.RequestCancellationAtPeriodEndAsync(
                tenantId, actor, email, "Cancelación de renovación ejecutada por soporte desde plataforma.", cancellationToken));

        [HttpPost("cancel")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Cancel(Guid tenantId, string? confirm, CancellationToken cancellationToken)
        {
            // Acción peligrosa: exige la palabra CANCELAR escrita (defensa server-side además del modal).
            if (!string.Equals(confirm?.Trim(), "CANCELAR", StringComparison.Ordinal))
            {
                TempData["PlatformError"] = "Para cancelar de inmediato, escribí CANCELAR para confirmar.";
                return Task.FromResult<IActionResult>(RedirectToAction(nameof(Manage), new { tenantId }));
            }

            return RunAsync(tenantId, (m, actor, email) => m.CancelAsync(tenantId, actor, email, cancellationToken));
        }

        [HttpPost("pause")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Pause(Guid tenantId, bool immediate = false, CancellationToken cancellationToken = default) =>
            RunAsync(tenantId, (m, actor, email) => m.PauseAsync(tenantId, actor, email, immediate, cancellationToken));

        [HttpPost("reactivate")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Reactivate(Guid tenantId, CancellationToken cancellationToken) =>
            RunAsync(tenantId, (m, actor, email) => m.ReactivateAsync(tenantId, actor, email, cancellationToken));

        /// <summary>
        /// Sincroniza manualmente el estado del proveedor (getSuscriptorRepeat) para este tenant. No
        /// se hace HTTP automático en cada GET para no volver lenta la pantalla: es un botón explícito.
        /// El controller nunca llama a TiloPay; delega en el servicio (verificación + auditoría).
        /// </summary>
        [HttpPost("sync-provider-status")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> SyncProviderStatus(Guid tenantId, CancellationToken cancellationToken) =>
            RunAsync(tenantId, (m, actor, email) => m.SyncProviderStatusAsync(tenantId, actor, email, cancellationToken));

        /// <summary>
        /// Caso D: reactivar una RENOVACIÓN cancelada aún vigente (suscriptor Delete a propósito).
        /// Distinto de <see cref="Reactivate"/>: la reactivación genérica sobre un Delete fuera de
        /// este contexto sigue bloqueada.
        /// </summary>
        [HttpPost("reactivate-renovacion")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> ReactivateRenovacion(Guid tenantId, CancellationToken cancellationToken) =>
            RunAsync(tenantId, (m, actor, email) => m.ReactivateRenewalAsync(tenantId, actor, email, cancellationToken));

        private async Task<IActionResult> RunAsync(
            Guid tenantId,
            Func<IProviderSubscriptionManager, string, string, Task<ProviderSubscriptionActionResult>> operation)
        {
            if (tenantId == Guid.Empty)
            {
                return BadRequest("tenantId requerido.");
            }

            var actor = await _userManager.GetUserAsync(User);
            var actorId = actor?.Id ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
            var actorEmail = actor?.Email ?? User.Identity?.Name ?? "system";

            var result = await operation(_manager, actorId, actorEmail);

            TempData[result.Succeeded ? "PlatformSuccess" : "PlatformError"] = result.Message;
            return RedirectToAction(nameof(Manage), new { tenantId });
        }

        private async Task<ProviderSubscriptionLifecycleViewModel> BuildViewModelAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            var subscription = await _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(s => s.Plan)
                .Where(s => s.TenantId == tenantId)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .FirstOrDefaultAsync(cancellationToken);

            var tenantName = await _context.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => t.Nombre)
                .FirstOrDefaultAsync(cancellationToken);

            if (subscription is null)
            {
                return new ProviderSubscriptionLifecycleViewModel
                {
                    TenantId = tenantId,
                    TenantName = tenantName,
                    AdminEnabled = _adminService.IsEnabled,
                    HasManageableSubscription = false
                };
            }

            var providerState = ProviderSubscriberStatusRules.Classify(subscription.ProviderStatusRaw);
            var mismatch = await _context.PlatformAuditLogs
                .AsNoTracking()
                .Where(log =>
                    log.Action == PlatformAuditActions.SubscriptionProviderStatusMismatch &&
                    log.EntityId == subscription.Id.ToString())
                .OrderByDescending(log => log.CreatedAtUtc)
                .Select(log => new { log.Reason, log.CreatedAtUtc })
                .FirstOrDefaultAsync(cancellationToken);

            return new ProviderSubscriptionLifecycleViewModel
            {
                TenantId = tenantId,
                TenantName = tenantName,
                AdminEnabled = _adminService.IsEnabled,
                HasManageableSubscription =
                    subscription.Proveedor == PaymentProviderType.Tilopay &&
                    !string.IsNullOrWhiteSpace(subscription.ProviderSubscriptionId) &&
                    subscription.TilopayRecurringPlanId != null,
                PlanCode = subscription.CodigoPlan ?? subscription.Plan?.Codigo,
                PlanName = subscription.Plan?.Nombre,
                LocalStatus = subscription.Estado,
                LocalStatusLabel = subscription.Estado.ToString(),
                CanAccessApp = subscription.Estado is EstadoSuscripcion.Activa or EstadoSuscripcion.Morosa or EstadoSuscripcion.Trial,
                CancelAtPeriodEnd = subscription.CancelAtPeriodEnd,
                CanReactivateRenewal = subscription.CancelAtPeriodEnd &&
                    (subscription.CancellationEffectiveAtUtc
                        ?? SubscriptionEffectiveDates.GetEffectiveEndUtc(subscription.FechaFin, subscription.ProviderExpiresAtUtc))
                        is { } lifecycleEnd && lifecycleEnd > DateTime.UtcNow,
                ProviderSubscriptionIdSuffix = SensitiveDataMasker.MaskReference(subscription.ProviderSubscriptionId),
                ProviderStatusRaw = subscription.ProviderStatusRaw,
                ProviderStateLabel = providerState switch
                {
                    ProviderSubscriberState.Active => "Activo",
                    ProviderSubscriberState.Paused => "Pausado",
                    ProviderSubscriberState.Inactive => "Eliminado / inactivo",
                    _ => "Desconocido / sin sincronizar"
                },
                ProviderIsActive = providerState == ProviderSubscriberState.Active,
                ProviderIsPaused = providerState == ProviderSubscriberState.Paused,
                ProviderIsDeleted = providerState == ProviderSubscriberState.Inactive,
                ProviderPausedAtUtc = subscription.ProviderPausedAtUtc,
                ProviderCancelledAtUtc = subscription.ProviderCancelledAtUtc,
                CancellationRequestedAtUtc = subscription.CancellationRequestedAtUtc,
                CancellationEffectiveAtUtc = subscription.CancellationEffectiveAtUtc,
                CancellationReason = subscription.CancellationReason,
                ProviderStatusLastSyncedUtc = subscription.ProviderStatusLastSyncedUtc,
                FechaFinUtc = subscription.FechaFin,
                EffectiveEndDisplay = SubscriptionDisplayDates.FormatEffective(
                    subscription.FechaFin, subscription.ProviderExpiresAtUtc, subscription.ProviderExpiryRaw),
                RecentMismatchReason = mismatch?.Reason,
                RecentMismatchAtUtc = mismatch?.CreatedAtUtc
            };
        }
    }
}
