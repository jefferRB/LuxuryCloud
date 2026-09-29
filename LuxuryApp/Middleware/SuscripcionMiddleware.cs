using System.Security.Claims;
using LuxuryApp.Models.Identity;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.SaaS;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

public class SuscripcionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SuscripcionMiddleware> _logger;

    public SuscripcionMiddleware(
        RequestDelegate next,
        ILogger<SuscripcionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(
        HttpContext context,
        ApplicationDbContext db,
        ITenantCommercialAccessResolver commercialAccessResolver)
    {
        try
        {
            var path = (context.Request.Path.Value ?? string.Empty).ToLowerInvariant();

            if (IsPublicPath(path))
            {
                await _next(context);
                return;
            }

            if (context.User?.Identity == null || !context.User.Identity.IsAuthenticated)
            {
                await _next(context);
                return;
            }

            if (string.Equals(
                context.User.FindFirstValue(CustomClaimTypes.PlatformSuperAdmin),
                bool.TrueString,
                StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            var tenantClaim = context.User.FindFirst(CustomClaimTypes.TenantId);
            if (tenantClaim == null || !Guid.TryParse(tenantClaim.Value, out var tenantId) || tenantId == Guid.Empty)
            {
                _logger.LogWarning("TenantId invalido o inexistente.");
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                context.Response.Redirect("/Accounts/Acceso");
                return;
            }

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.User.FindFirstValue(CustomClaimTypes.UserId);

            if (string.IsNullOrWhiteSpace(userId))
            {
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                context.Response.Redirect("/Accounts/Acceso");
                return;
            }

            var user = await db.Users
                .AsNoTracking()
                .Where(currentUser => currentUser.Id == userId)
                .Select(currentUser => new AppUsuario
                {
                    Id = currentUser.Id,
                    TenantId = currentUser.TenantId,
                    IsPlatformSuperAdmin = currentUser.IsPlatformSuperAdmin
                })
                .FirstOrDefaultAsync();

            if (user is null)
            {
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                context.Response.Redirect("/Accounts/Acceso");
                return;
            }

            if (user.IsPlatformSuperAdmin)
            {
                await _next(context);
                return;
            }

            var tenantActivo = await db.Tenants
                .AsNoTracking()
                .AnyAsync(t => t.Id == tenantId && t.Activo);

            if (!tenantActivo)
            {
                _logger.LogWarning("Tenant suspendido o inexistente detectado en middleware. TenantId {TenantId}", tenantId);
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                context.Response.Redirect("/Accounts/Acceso");
                return;
            }

            var access = await commercialAccessResolver.ResolveAsync(tenantId, user);
            if (!access.CanAccessApp)
            {
                context.Response.Redirect(access.HasCommercialHistory
                    ? "/Billing/PlanVencido"
                    : "/Billing/SinSuscripcion");
                return;
            }

            context.Items["TenantId"] = tenantId;
            context.Items["TenantCommercialAccess"] = access;

            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en SuscripcionMiddleware");
            context.Response.Redirect("/Home/Error");
        }
    }

    private static bool IsPublicPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return true;
        }

        return path.StartsWith("/accounts") ||
            path.StartsWith("/home") ||
            path.StartsWith("/privacidad") ||
            path.StartsWith("/eliminacion-datos") ||
            path.StartsWith("/contract") ||
            path.StartsWith("/error") ||
            path.StartsWith("/billing") ||
            path.StartsWith("/platform") ||
            path.StartsWith("/comprobantes") ||
            path.StartsWith("/reservar") ||
            path.StartsWith("/api/webhooks/meta-whatsapp");
    }
}
