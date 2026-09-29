using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Platform
{
    /// <summary>
    /// Diagnóstico interno del módulo Billing. SOLO plataforma (super admin): nunca debe
    /// exponerse a tenants porque agrega datos cross-tenant.
    /// </summary>
    [Authorize(Policy = PlatformAuthorizationPolicies.PlatformSuperAdmin)]
    [Route("Platform/BillingHealth")]
    public sealed class PlatformBillingHealthController : Controller
    {
        private readonly IBillingHealthService _healthService;
        private readonly IBillingReconciliationService _reconciliationService;
        private readonly IAddonProviderAuditService? _addonProviderAudit;

        public PlatformBillingHealthController(
            IBillingHealthService healthService,
            IBillingReconciliationService reconciliationService,
            IAddonProviderAuditService? addonProviderAudit = null)
        {
            _healthService = healthService;
            _reconciliationService = reconciliationService;
            _addonProviderAudit = addonProviderAudit;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var snapshot = await _healthService.BuildAsync(cancellationToken);
            return View(snapshot);
        }

        /// <summary>Misma fotografía en JSON para monitoreo externo autenticado.</summary>
        [HttpGet("json")]
        public async Task<IActionResult> Snapshot(CancellationToken cancellationToken)
        {
            var snapshot = await _healthService.BuildAsync(cancellationToken);
            return Json(snapshot);
        }

        /// <summary>
        /// Fuerza un reintento seguro de la cancelación del suscriptor viejo de un cambio de plan.
        /// Idempotente y con la MISMA lógica del worker: ignora el backoff (esa es la razón de ser
        /// del botón) pero nunca las guardas de elegibilidad, así que no puede cancelar un
        /// suscriptor que esté cobrando ni actuar sobre datos incompletos.
        /// Existe para que soporte destrabe un caso sin editar la BD ni entrar a TiloPay a mano.
        /// </summary>
        [HttpPost("retry-old-cancellation/{intentId:guid}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RetryOldProviderCancellation(Guid intentId, CancellationToken cancellationToken)
        {
            var outcome = await _reconciliationService.ForceOldSubscriberCancellationRetryAsync(
                intentId,
                User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "platform",
                User.Identity?.Name ?? "platform",
                cancellationToken);

            TempData[outcome.Status == PlanChangeCancellationRetryStatus.Cancelled ? "Success" : "Error"] =
                outcome.Status switch
                {
                    PlanChangeCancellationRetryStatus.Cancelled =>
                        "Suscriptor viejo cancelado y verificado en TiloPay.",
                    PlanChangeCancellationRetryStatus.AttemptedStillPending =>
                        $"Se intentó cancelar pero el suscriptor viejo sigue pendiente. {outcome.Message}",
                    PlanChangeCancellationRetryStatus.NotPending =>
                        "Ese cambio de plan no tiene cancelación pendiente.",
                    _ => outcome.Message
                };

            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Auditoría SOLO LECTURA del estado real de los add-ons de WhatsApp de un tenant en
        /// TiloPay: cuántos suscriptores pueden cobrarle. NO cancela nada (esa decisión queda en el
        /// flujo verificado de cancelación o en un humano); solo consulta getSuscriptorRepeat y deja
        /// el snapshot + auditoría + incidente si detecta doble cobro.
        ///
        /// Cubre el caso en que el estado local de un tenant se ve sano
        /// mientras TiloPay tiene WA400 y WA800 activos a la vez, y no había forma de verlo sin
        /// entrar al panel del proveedor.
        /// </summary>
        [HttpPost("audit-addon-provider")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AuditAddonProvider(
            [FromForm] Guid tenantId,
            CancellationToken cancellationToken)
        {
            if (tenantId == Guid.Empty)
            {
                TempData["Error"] = "Indicá un TenantId válido para auditar.";
                return RedirectToAction(nameof(Index));
            }

            if (_addonProviderAudit is null || !_addonProviderAudit.IsEnabled)
            {
                TempData["Error"] = "El API admin de TiloPay está deshabilitado: no se puede auditar el proveedor.";
                return RedirectToAction(nameof(Index));
            }

            var audit = await _addonProviderAudit.AuditAsync(
                tenantId,
                customerEmail: null,
                source: "manual",
                auditAction: Models.Platform.PlatformAuditActions.AddonProviderDoubleActiveDetected,
                cancellationToken);

            if (!audit.Executed)
            {
                TempData["Error"] = $"No se pudo auditar el proveedor: {audit.Detail}";
            }
            else if (audit.HasDoubleActive)
            {
                TempData["Error"] =
                    $"CRÍTICO: TiloPay puede cobrar {audit.ChargeableCount} add-ons a este tenant. {audit.Detail}";
            }
            else if (audit.IsInconclusive)
            {
                TempData["Error"] = $"Auditoría NO concluyente (no se declara sano): {audit.Detail}";
            }
            else
            {
                TempData["Success"] =
                    $"Auditoría OK: {audit.ChargeableCount} suscriptor(es) de add-on cobrable(s) en TiloPay. {audit.Detail}";
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
