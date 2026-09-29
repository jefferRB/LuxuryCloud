using System.Security.Claims;
using System.Text.Json;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Platform
{
    /// <summary>
    /// Snapshot comercial mensual (AD-4): captura manual auditada y consulta histórica en
    /// JSON. Sin vista propia en esta ola; la pestaña Dinero/Negocio la consumirá después.
    /// </summary>
    [Authorize(Policy = PlatformAuthorizationPolicies.PlatformSuperAdmin)]
    [Route("Platform/CommercialSnapshot")]
    public sealed class PlatformCommercialSnapshotController : Controller
    {
        private readonly IPlatformCommercialSnapshotService _snapshotService;
        private readonly IPlatformAuditService _auditService;

        public PlatformCommercialSnapshotController(
            IPlatformCommercialSnapshotService snapshotService,
            IPlatformAuditService auditService)
        {
            _snapshotService = snapshotService;
            _auditService = auditService;
        }

        /// <summary>Captura (o re-captura) el snapshot del mes en curso a demanda.</summary>
        [HttpPost("capture")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Capture(CancellationToken cancellationToken)
        {
            var nowUtc = DateTime.UtcNow;
            var actorEmail = User.FindFirstValue(ClaimTypes.Email)
                ?? User.FindFirstValue(CustomClaimTypes.UserName)
                ?? User.Identity?.Name;

            var snapshot = await _snapshotService.CaptureAsync(
                nowUtc.Year,
                nowUtc.Month,
                PlatformCommercialSnapshotTriggers.Manual,
                actorEmail,
                cancellationToken);

            await _auditService.TryLogAsync(new PlatformAuditEntry
            {
                Action = PlatformAuditActions.CommercialSnapshotCaptured,
                EntityType = PlatformAuditEntityTypes.CommercialSnapshot,
                EntityId = $"{snapshot.PeriodYear}-{snapshot.PeriodMonth:00}",
                AfterJson = JsonSerializer.Serialize(new
                {
                    snapshot.PeriodYear,
                    snapshot.PeriodMonth,
                    snapshot.MrrTotal,
                    snapshot.ActiveSubscriptions,
                    snapshot.TenantsTotal,
                    snapshot.ChurnedTenants
                })
            }, cancellationToken);

            TempData["PlatformSuccess"] =
                $"Snapshot comercial {snapshot.PeriodYear}-{snapshot.PeriodMonth:00} capturado. MRR: {snapshot.MrrTotal:N2}.";

            return RedirectToAction("Index", "Platform");
        }

        /// <summary>Historia del snapshot para consulta y monitoreo (patrón BillingHealth/json).</summary>
        [HttpGet("json")]
        public async Task<IActionResult> Json(int take = 24, CancellationToken cancellationToken = default) =>
            base.Json(await _snapshotService.GetHistoryAsync(take, cancellationToken));
    }
}
