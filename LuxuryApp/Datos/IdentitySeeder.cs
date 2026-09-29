using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Datos
{
    public class IdentitySeeder
    {
        // Cuenta que recibe acceso de superadmin de plataforma. Se configura por entorno
        // (Platform:SuperAdminEmail / env var Platform__SuperAdminEmail); vacío => no se promueve a nadie.
        private const string PlatformSuperAdminEmailKey = "Platform:SuperAdminEmail";
        private const string InternalPlatformTenantName = "Platform Internal";

        public static async Task SeedRolesAsync(IServiceProvider serviceProvider)
        {
            var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();

            string[] roles =
            {
                Services.Identity.AppRoles.Administrador,
                Services.Identity.AppRoles.Registrado,
                Services.Identity.AppRoles.Funcionario,
                Services.Identity.AppRoles.Asociado
            };

            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                }
            }
        }

        public static async Task SeedPlatformAccessAsync(IServiceProvider serviceProvider)
        {
            var platformSuperAdminEmail = serviceProvider
                .GetRequiredService<IConfiguration>()[PlatformSuperAdminEmailKey];
            if (string.IsNullOrWhiteSpace(platformSuperAdminEmail))
            {
                return;
            }

            var userManager = serviceProvider.GetRequiredService<UserManager<AppUsuario>>();
            var context = serviceProvider.GetRequiredService<ApplicationDbContext>();

            var user = await userManager.FindByEmailAsync(platformSuperAdminEmail.Trim());
            if (user is null)
            {
                return;
            }

            var userChanged = false;
            var hasValidTenant = user.TenantId != Guid.Empty &&
                await context.Tenants.AnyAsync(tenant => tenant.Id == user.TenantId);

            if (!hasValidTenant)
            {
                var internalTenant = await context.Tenants
                    .FirstOrDefaultAsync(tenant => tenant.Nombre == InternalPlatformTenantName);

                if (internalTenant is null)
                {
                    internalTenant = new Tenant
                    {
                        Id = Guid.NewGuid(),
                        Nombre = InternalPlatformTenantName,
                        Activo = true,
                        FechaCreacion = DateTime.UtcNow,
                        CommercialAccessMode = TenantCommercialAccessMode.Internal,
                        CommercialNotes = "Tenant interno bootstrap para acceso de plataforma.",
                        CommercialUpdatedUtc = DateTime.UtcNow
                    };

                    var preferredPlan = await context.Planes
                        .Where(plan => plan.Activo)
                        .OrderByDescending(plan => plan.Nombre == "Full")
                        .ThenByDescending(plan => plan.PrecioMensual)
                        .FirstOrDefaultAsync();

                    if (preferredPlan is not null)
                    {
                        internalTenant.ForcedPlanId = preferredPlan.Id;
                    }

                    context.Tenants.Add(internalTenant);
                    await context.SaveChangesAsync();
                }

                user.TenantId = internalTenant.Id;
                userChanged = true;
            }

            if (!user.IsPlatformSuperAdmin)
            {
                user.IsPlatformSuperAdmin = true;
                userChanged = true;
            }

            if (userChanged)
            {
                await userManager.UpdateAsync(user);
            }
        }
    }
}
