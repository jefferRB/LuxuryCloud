using System.Security.Claims;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Filters
{
    /// <summary>
    /// Enrolamiento obligatorio de TOTP para superadmins (S1). Con el enforcement activo,
    /// un superadmin autenticado sin TwoFactorEnabled solo puede llegar a las acciones
    /// marcadas con <see cref="AllowWithoutMfaEnrollmentAttribute"/>; todo lo demás
    /// redirige a /Seguridad/Enrolar. Los usuarios de tenant nunca son gateados.
    /// </summary>
    public sealed class RequireMfaEnrollmentFilter : IAsyncActionFilter
    {
        private readonly ApplicationDbContext _context;
        private readonly IOptionsMonitor<PlatformSecurityOptions> _options;
        private readonly ILogger<RequireMfaEnrollmentFilter> _logger;

        public RequireMfaEnrollmentFilter(
            ApplicationDbContext context,
            IOptionsMonitor<PlatformSecurityOptions> options,
            ILogger<RequireMfaEnrollmentFilter> logger)
        {
            _context = context;
            _options = options;
            _logger = logger;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (await RequiresEnrollmentRedirectAsync(context))
            {
                context.Result = new RedirectToActionResult("Enrolar", "Seguridad", routeValues: null);
                return;
            }

            await next();
        }

        private async Task<bool> RequiresEnrollmentRedirectAsync(ActionExecutingContext context)
        {
            if (!_options.CurrentValue.Mfa.SuperAdminEnforcement)
            {
                return false;
            }

            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true ||
                !user.HasClaim(CustomClaimTypes.PlatformSuperAdmin, bool.TrueString))
            {
                return false;
            }

            if (context.ActionDescriptor.EndpointMetadata.Any(metadata => metadata is AllowWithoutMfaEnrollmentAttribute))
            {
                return false;
            }

            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                // Sin identificador el TenantSessionSecurityValidator ya rechaza la sesión.
                return false;
            }

            var twoFactorEnabled = await _context.Users
                .AsNoTracking()
                .Where(candidate => candidate.Id == userId)
                .Select(candidate => (bool?)candidate.TwoFactorEnabled)
                .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

            if (twoFactorEnabled is null or true)
            {
                return false;
            }

            _logger.LogInformation(
                "Superadmin {UserId} sin TOTP redirigido al enrolamiento obligatorio de MFA.",
                userId);

            return true;
        }
    }
}
