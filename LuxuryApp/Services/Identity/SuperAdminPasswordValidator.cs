using LuxuryApp.Models.Identity;
using Microsoft.AspNetCore.Identity;

namespace LuxuryApp.Services.Identity
{
    /// <summary>
    /// Las cuentas de plataforma exigen un mínimo mayor que el global (12 vs 8):
    /// su contraseña protege datos de todos los tenants.
    /// </summary>
    public sealed class SuperAdminPasswordValidator : IPasswordValidator<AppUsuario>
    {
        public const int RequiredLength = 12;

        public Task<IdentityResult> ValidateAsync(
            UserManager<AppUsuario> manager,
            AppUsuario user,
            string? password)
        {
            if (user.IsPlatformSuperAdmin
                && (password is null || password.Length < RequiredLength))
            {
                return Task.FromResult(IdentityResult.Failed(new IdentityError
                {
                    Code = "SuperAdminPasswordTooShort",
                    Description = $"La contraseña de una cuenta de plataforma debe tener al menos {RequiredLength} caracteres."
                }));
            }

            return Task.FromResult(IdentityResult.Success);
        }
    }
}
