using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Platform
{
    [Authorize(Policy = PlatformAuthorizationPolicies.PlatformSuperAdmin)]
    [Route("Platform")]
    public class PlatformTenantController : Controller
    {
        private readonly IPlatformTenantProfileService _profileService;

        public PlatformTenantController(IPlatformTenantProfileService profileService)
        {
            _profileService = profileService;
        }

        [HttpGet("Tenants/{tenantId:guid}/Ficha")]
        public async Task<IActionResult> Ficha(Guid tenantId, CancellationToken cancellationToken = default)
        {
            var ficha = await _profileService.GetFichaAsync(tenantId, cancellationToken);
            if (ficha is null)
                return NotFound();

            return View("~/Views/Platform/TenantFicha.cshtml", ficha);
        }
    }
}
