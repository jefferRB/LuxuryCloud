using LuxuryApp.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace LuxuryApp.Services.Identity
{
    public static class CustomClaimTypes
    {
        public const string TenantId = "tenant_id";
        public const string UserId = "user_id";
        public const string UserName = "user_name";
        public const string PlatformSuperAdmin = "platform_super_admin";
        public const string FuncionarioId = "funcionario_id";
    }

    public class CustomClaimsPrincipalFactory
        : UserClaimsPrincipalFactory<AppUsuario, IdentityRole>
    {
        private readonly ILogger<CustomClaimsPrincipalFactory> _logger;

        public CustomClaimsPrincipalFactory(
            UserManager<AppUsuario> userManager,
            RoleManager<IdentityRole> roleManager,
            IOptions<IdentityOptions> optionsAccessor,
            ILogger<CustomClaimsPrincipalFactory> logger)
            : base(userManager, roleManager, optionsAccessor)
        {
            _logger = logger;
        }

        protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUsuario user)
        {
            var identity = await base.GenerateClaimsAsync(user);

            // Validación defensiva: un usuario sin tenant válido no rompe el login.
            if (user.TenantId == Guid.Empty)
            {
                _logger.LogCritical("Usuario {UserId} sin TenantId asignado", user.Id);

                // No se lanza excepción:
                // sin claim de tenant el usuario queda sin acceso a datos de negocio.
                return identity;
            }

            // Claim principal: TenantId.
            identity.AddClaim(new Claim(
                CustomClaimTypes.TenantId,
                user.TenantId.ToString()
            ));

            // Claims de perfil usados por la UI.
            identity.AddClaim(new Claim(
                CustomClaimTypes.UserId,
                user.Id
            ));

            identity.AddClaim(new Claim(
                CustomClaimTypes.UserName,
                user.UserName ?? string.Empty
            ));

            if (user.IsPlatformSuperAdmin)
            {
                identity.AddClaim(new Claim(
                    CustomClaimTypes.PlatformSuperAdmin,
                    bool.TrueString
                ));
            }

            // Claim de funcionario (portal limitado).
            // Solo presente para cuentas de acceso de funcionario. El portal lo usa
            // como única fuente de verdad del FuncionarioId; nunca se confía en la URL.
            if (user.FuncionarioId.HasValue)
            {
                identity.AddClaim(new Claim(
                    CustomClaimTypes.FuncionarioId,
                    user.FuncionarioId.Value.ToString()
                ));
            }

            return identity;
        }
    }
}
