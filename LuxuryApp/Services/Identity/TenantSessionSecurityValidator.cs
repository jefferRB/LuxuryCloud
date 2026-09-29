using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Identity
{
    public sealed class TenantSessionSecurityValidator
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<TenantSessionSecurityValidator> _logger;

        public TenantSessionSecurityValidator(
            ApplicationDbContext context,
            ILogger<TenantSessionSecurityValidator> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<bool> ValidateAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            if (principal.Identity?.IsAuthenticated != true)
            {
                return true;
            }

            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue(CustomClaimTypes.UserId);

            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning("Sesion rechazada porque no existe NameIdentifier ni user_id en el principal.");
                return false;
            }

            try
            {
                var userState = await _context.Users
                    .AsNoTracking()
                    .Where(user => user.Id == userId)
                    .Select(user => new
                    {
                        user.Id,
                        user.State,
                        user.TenantId,
                        user.LockoutEnd,
                        user.IsPlatformSuperAdmin
                    })
                    .SingleOrDefaultAsync(cancellationToken);

                if (userState is null)
                {
                    _logger.LogWarning("Sesion rechazada porque el usuario {UserId} ya no existe.", userId);
                    return false;
                }

                if (!userState.State)
                {
                    _logger.LogWarning("Sesion rechazada para UserId {UserId} porque el usuario esta deshabilitado.", userId);
                    return false;
                }

                if (userState.TenantId == Guid.Empty)
                {
                    _logger.LogWarning("Sesion rechazada para UserId {UserId} porque el usuario no tiene TenantId valido.", userId);
                    return false;
                }

                if (userState.LockoutEnd.HasValue && userState.LockoutEnd.Value > DateTimeOffset.UtcNow)
                {
                    _logger.LogWarning("Sesion rechazada para UserId {UserId} porque el usuario esta bloqueado.", userId);
                    return false;
                }

                var tenantClaim = principal.FindFirstValue(CustomClaimTypes.TenantId);
                if (!Guid.TryParse(tenantClaim, out var claimTenantId) || claimTenantId == Guid.Empty)
                {
                    _logger.LogWarning(
                        "Sesion rechazada para UserId {UserId} porque el claim tenant_id es invalido.",
                        userId);
                    return false;
                }

                if (userState.TenantId != claimTenantId)
                {
                    _logger.LogWarning(
                        "Sesion rechazada para UserId {UserId} por desalineacion tenant-claim. ClaimTenantId {ClaimTenantId}. UserTenantId {UserTenantId}.",
                        userId,
                        claimTenantId,
                        userState.TenantId);
                    return false;
                }

                var superAdminClaim = principal.FindFirstValue(CustomClaimTypes.PlatformSuperAdmin);
                if (string.Equals(superAdminClaim, bool.TrueString, StringComparison.OrdinalIgnoreCase)
                    && !userState.IsPlatformSuperAdmin)
                {
                    _logger.LogWarning(
                        "Sesion rechazada para UserId {UserId} porque el claim platform_super_admin fue revocado en la base de datos.",
                        userId);
                    return false;
                }

                if (userState.IsPlatformSuperAdmin)
                {
                    return true;
                }

                var tenantIsActive = await _context.Tenants
                    .AsNoTracking()
                    .AnyAsync(tenant => tenant.Id == userState.TenantId && tenant.Activo, cancellationToken);

                if (!tenantIsActive)
                {
                    _logger.LogWarning(
                        "Sesion rechazada para UserId {UserId} porque el tenant {TenantId} esta suspendido o no existe.",
                        userId,
                        userState.TenantId);
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    ex,
                    "Validacion de sesion cancelada de forma esperable para UserId {UserId} porque el request fue abortado.",
                    userId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inesperado validando la sesion del UserId {UserId}.", userId);
                throw;
            }
        }
    }
}
