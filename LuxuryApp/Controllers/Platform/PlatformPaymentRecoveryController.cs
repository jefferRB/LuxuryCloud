using System.Security.Claims;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Platform
{
    /// <summary>
    /// Consola de recuperación de pago (SOLO plataforma / SuperAdmin): lista los incidentes vivos y
    /// permite acciones de soporte (generar enlace de actualización de tarjeta, resolver o ignorar
    /// manualmente). El controller NUNCA llama a TiloPay ni escribe la BD directamente: delega en
    /// <see cref="IPaymentRecoveryService"/> y <see cref="IPaymentMethodUpdateService"/> (verificación,
    /// tenant isolation vía BeginScope, auditoría). La recurrentUrl generada se muestra en la respuesta
    /// y jamás se persiste ni se guarda en cookie/TempData.
    /// </summary>
    [Authorize(Policy = PlatformAuthorizationPolicies.PlatformSuperAdmin)]
    [Route("Platform/PaymentRecovery")]
    public sealed class PlatformPaymentRecoveryController : Controller
    {
        private readonly IPaymentRecoveryService _recoveryService;
        private readonly IPaymentMethodUpdateService _methodUpdateService;
        private readonly UserManager<AppUsuario> _userManager;

        public PlatformPaymentRecoveryController(
            IPaymentRecoveryService recoveryService,
            IPaymentMethodUpdateService methodUpdateService,
            UserManager<AppUsuario> userManager)
        {
            _recoveryService = recoveryService;
            _methodUpdateService = methodUpdateService;
            _userManager = userManager;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var incidents = await _recoveryService.ListConsoleIncidentsAsync(cancellationToken);
            return View(new PaymentRecoveryConsoleViewModel
            {
                Incidents = incidents,
                UnverifiedResolutions = await _recoveryService.ListUnverifiedResolutionsAsync(cancellationToken),
                AdminEnabled = _methodUpdateService.IsEnabled
            });
        }

        /// <summary>
        /// Reabre un cierre automático SIN evidencia de pago. El servicio re-verifica todas las guardas
        /// (sin pago posterior, sin expire avanzado, sin incidente más reciente) y audita antes/después.
        /// Exige escribir REABRIR y un motivo.
        /// </summary>
        [HttpPost("reopen-unverified")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReopenUnverified(Guid incidentId, string? confirm, string? reason, CancellationToken cancellationToken)
        {
            if (!string.Equals(confirm?.Trim(), "REABRIR", StringComparison.Ordinal))
            {
                TempData["PlatformError"] = "Para reabrir el incidente, confirmá escribiendo REABRIR.";
                return RedirectToAction(nameof(Index));
            }

            var (actorId, actorEmail) = await ResolveActorAsync();
            var result = await _recoveryService.ReopenUnverifiedResolutionAsync(incidentId, actorId, actorEmail, reason, cancellationToken);
            TempData[result.Succeeded ? "PlatformSuccess" : "PlatformError"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Genera la recurrentUrl para el tenant (email resuelto server-side) y la muestra en la
        /// misma vista para copiar/enviar. No redirige a TiloPay ni la persiste.
        /// </summary>
        [HttpPost("generate-update-url")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateUpdateUrl(Guid incidentId, CancellationToken cancellationToken)
        {
            if (incidentId == Guid.Empty)
            {
                TempData["PlatformError"] = "Incidente no especificado.";
                return RedirectToAction(nameof(Index));
            }

            var (actorId, actorEmail) = await ResolveActorAsync();
            // Incident-aware: el scope del incidente decide el plan (base vs add-on). Nunca se mezcla
            // el suscriptor base con el del add-on.
            var result = await _methodUpdateService.GenerateUpdateUrlForIncidentAsync(incidentId, actorId, actorEmail, cancellationToken);

            var incidents = await _recoveryService.ListConsoleIncidentsAsync(cancellationToken);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Url))
            {
                TempData["PlatformError"] = result.Message ?? "No se pudo generar el enlace de actualización.";
                return View(nameof(Index), new PaymentRecoveryConsoleViewModel
                {
                    Incidents = incidents,
                    AdminEnabled = _methodUpdateService.IsEnabled
                });
            }

            var tenantName = incidents.FirstOrDefault(i => i.IncidentId == incidentId)?.TenantName;
            // La URL viaja SOLO en el HTML de esta respuesta (nunca a cookie/TempData ni al log).
            return View(nameof(Index), new PaymentRecoveryConsoleViewModel
            {
                Incidents = incidents,
                AdminEnabled = _methodUpdateService.IsEnabled,
                GeneratedUpdateUrl = result.Url,
                GeneratedUrlTenantName = tenantName,
                GeneratedUrlUsedFallback = result.UsedFallbackContract
            });
        }

        [HttpPost("resolve")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Resolve(Guid incidentId, string? confirm, CancellationToken cancellationToken)
        {
            // Confirmación server-side (defensa además del modal): exige la palabra RESOLVER.
            if (!string.Equals(confirm?.Trim(), "RESOLVER", StringComparison.Ordinal))
            {
                TempData["PlatformError"] = "Para cerrar el incidente, confirmá escribiendo RESOLVER.";
                return RedirectToAction(nameof(Index));
            }

            var (actorId, actorEmail) = await ResolveActorAsync();
            var result = await _recoveryService.ResolveManuallyAsync(incidentId, actorId, actorEmail, cancellationToken);
            TempData[result.Succeeded ? "PlatformSuccess" : "PlatformError"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("ignore")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Ignore(Guid incidentId, string? reason, CancellationToken cancellationToken)
        {
            var (actorId, actorEmail) = await ResolveActorAsync();
            var result = await _recoveryService.IgnoreAsync(incidentId, actorId, actorEmail, reason, cancellationToken);
            TempData[result.Succeeded ? "PlatformSuccess" : "PlatformError"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        private async Task<(string ActorId, string ActorEmail)> ResolveActorAsync()
        {
            var actor = await _userManager.GetUserAsync(User);
            var actorId = actor?.Id ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
            var actorEmail = actor?.Email ?? User.Identity?.Name ?? "system";
            return (actorId, actorEmail);
        }
    }
}
