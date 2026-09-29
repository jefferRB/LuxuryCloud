using System.Security.Claims;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Identity;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Account
{
    /// <inheritdoc cref="IAccountSettingsPageBuilder" />
    public sealed class AccountSettingsPageBuilder : IAccountSettingsPageBuilder
    {
        private readonly UserManager<AppUsuario> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly IOptionsMonitor<PlatformSecurityOptions> _securityOptions;

        public AccountSettingsPageBuilder(
            UserManager<AppUsuario> userManager,
            ApplicationDbContext context,
            IOptionsMonitor<PlatformSecurityOptions> securityOptions)
        {
            _userManager = userManager;
            _context = context;
            _securityOptions = securityOptions;
        }

        public async Task<AccountSettingsPageViewModel?> BuildAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            var user = await _userManager.GetUserAsync(principal);
            if (user is null)
            {
                return null;
            }

            var page = new AccountSettingsPageViewModel
            {
                Profile = new CuentaViewModel
                {
                    Email = user.Email ?? string.Empty,
                    Name = user.Name ?? string.Empty,
                    PhoneNumber = user.PhoneNumber
                },
                Security = new AccountSecurityViewModel
                {
                    Email = user.Email ?? string.Empty,
                    // TwoFactorEnabled ya viene en la entidad cargada: no vale una consulta extra
                    // sólo para releer la misma columna.
                    TwoFactorEnabled = user.TwoFactorEnabled,
                    PuedeDeshabilitarTwoFactor = MfaEnrollmentPolicy.PuedeDeshabilitar(
                        user,
                        _securityOptions.CurrentValue)
                }
            };

            // La sección fiscal es del dueño del negocio. Para el resto ni se consulta ni se
            // construye: lo que no está en el modelo no puede llegar al HTML.
            if (principal.IsInRole(AppRoles.Administrador) && user.TenantId != Guid.Empty)
            {
                page.Fiscal = await LoadFiscalAsync(user.TenantId, cancellationToken);
            }

            return page;
        }

        /// <summary>
        /// Misma lectura que hacía <c>ConfiguracionFiscalController.Index</c>: el tenant del
        /// usuario autenticado, proyectado a las dos columnas que la pantalla edita.
        /// </summary>
        private async Task<ConfiguracionFiscalViewModel> LoadFiscalAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
            => await _context.Tenants
                   .AsNoTracking()
                   .Where(tenant => tenant.Id == tenantId)
                   .Select(tenant => new ConfiguracionFiscalViewModel
                   {
                       PreciosIncluyenIva = tenant.PreciosIncluyenIva,
                       TarifaIvaPorDefecto = tenant.TarifaIvaPorDefecto
                   })
                   .FirstOrDefaultAsync(cancellationToken)
               ?? new ConfiguracionFiscalViewModel();
    }
}
