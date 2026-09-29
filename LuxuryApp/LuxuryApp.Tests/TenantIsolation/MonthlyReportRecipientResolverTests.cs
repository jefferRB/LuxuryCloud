using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Reports;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Identity;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class MonthlyReportRecipientResolverTests
    {
        [Fact]
        public async Task Resolve_IncludesAdminsAndManual_ExcludesFuncionariosInvalidAndDuplicates()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var roles = await SeedRolesAsync(context);
            SeedTenant(context, tenantId);
            SeedUser(context, tenantId, "admin1@negocio.cr", roles.AdminId, confirmed: true);
            SeedUser(context, tenantId, "admin2@negocio.cr", roles.AdminId, confirmed: false);
            SeedUser(context, tenantId, "func@negocio.cr", roles.FuncionarioId, confirmed: true);
            // Cuenta con rol Administrador pero FuncionarioId asignado: también se excluye.
            SeedUser(context, tenantId, "portal@negocio.cr", roles.AdminId, confirmed: true, funcionarioId: 7);
            SeedUser(context, tenantId, "inactivo@negocio.cr", roles.AdminId, confirmed: true, state: false);
            await context.SaveChangesAsync();

            var settings = new TenantMonthlyReportSettings
            {
                TenantId = tenantId,
                SendToAllAdmins = true,
                IncludeManualRecipients = true,
                // extra válido; funcionario (excluir); duplicado de admin1 (excluir); inválido (excluir)
                AdditionalRecipients = "extra@negocio.cr, func@negocio.cr, ADMIN1@negocio.cr, no-sirve"
            };

            var resolver = ControllerTestSupport.CreateMonthlyReportRecipientResolver(context);
            var resolution = await resolver.ResolveAsync(tenantId, settings);

            var included = resolution.Included.Select(r => r.Email).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "admin1@negocio.cr", "admin2@negocio.cr", "extra@negocio.cr" }, included);

            // Nunca funcionarios.
            Assert.DoesNotContain(resolution.Included, r => r.Email.Contains("func@"));

            // Nunca cuentas con FuncionarioId, aunque tengan rol Administrador.
            Assert.DoesNotContain(resolution.Included, r => r.Email == "portal@negocio.cr");

            // Excluidos con motivo.
            Assert.Contains(resolution.Excluded, r => r.Email == "func@negocio.cr" && r.Reason == MonthlyReportExclusionReason.Funcionario);
            Assert.Contains(resolution.Excluded, r => r.Email == "portal@negocio.cr" && r.Reason == MonthlyReportExclusionReason.Funcionario);
            Assert.Contains(resolution.Excluded, r => r.Email == "admin1@negocio.cr" && r.Reason == MonthlyReportExclusionReason.Duplicate);
            Assert.Contains(resolution.Excluded, r => r.Reason == MonthlyReportExclusionReason.InvalidEmail);
            Assert.Contains(resolution.Excluded, r => r.Email == "inactivo@negocio.cr" && r.Reason == MonthlyReportExclusionReason.InactiveUser);
        }

        [Fact]
        public async Task Resolve_RequireConfirmedEmail_ExcludesUnconfirmedAdmins()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var roles = await SeedRolesAsync(context);
            SeedTenant(context, tenantId);
            SeedUser(context, tenantId, "confirmado@negocio.cr", roles.AdminId, confirmed: true);
            SeedUser(context, tenantId, "sinconfirmar@negocio.cr", roles.AdminId, confirmed: false);
            await context.SaveChangesAsync();

            var settings = new TenantMonthlyReportSettings
            {
                TenantId = tenantId,
                SendToAllAdmins = true,
                RequireConfirmedEmail = true
            };

            var resolver = ControllerTestSupport.CreateMonthlyReportRecipientResolver(context);
            var resolution = await resolver.ResolveAsync(tenantId, settings);

            Assert.Contains(resolution.Included, r => r.Email == "confirmado@negocio.cr");
            Assert.DoesNotContain(resolution.Included, r => r.Email == "sinconfirmar@negocio.cr");
            Assert.Contains(resolution.Excluded, r => r.Email == "sinconfirmar@negocio.cr" && r.Reason == MonthlyReportExclusionReason.Unconfirmed);
        }

        [Fact]
        public async Task Resolve_NewAdmin_IsIncludedDynamically()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var roles = await SeedRolesAsync(context);
            SeedTenant(context, tenantId);
            SeedUser(context, tenantId, "admin1@negocio.cr", roles.AdminId, confirmed: true);
            await context.SaveChangesAsync();

            var settings = new TenantMonthlyReportSettings { TenantId = tenantId, SendToAllAdmins = true };
            var resolver = ControllerTestSupport.CreateMonthlyReportRecipientResolver(context);

            var before = await resolver.ResolveAsync(tenantId, settings);
            Assert.Single(before.Included);

            // Un administrador creado DESPUÉS aparece sin tocar la configuración (resolución dinámica).
            SeedUser(context, tenantId, "admin-nuevo@negocio.cr", roles.AdminId, confirmed: true);
            await context.SaveChangesAsync();

            var after = await resolver.ResolveAsync(tenantId, settings);
            Assert.Equal(2, after.Included.Count);
            Assert.Contains(after.Included, r => r.Email == "admin-nuevo@negocio.cr");
        }

        [Fact]
        public async Task Resolve_ManualDisabled_OnlyAdmins()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var roles = await SeedRolesAsync(context);
            SeedTenant(context, tenantId);
            SeedUser(context, tenantId, "admin@negocio.cr", roles.AdminId, confirmed: true);
            await context.SaveChangesAsync();

            var settings = new TenantMonthlyReportSettings
            {
                TenantId = tenantId,
                SendToAllAdmins = true,
                IncludeManualRecipients = false,
                AdditionalRecipients = "manual@negocio.cr"
            };

            var resolver = ControllerTestSupport.CreateMonthlyReportRecipientResolver(context);
            var resolution = await resolver.ResolveAsync(tenantId, settings);

            Assert.Single(resolution.Included);
            Assert.DoesNotContain(resolution.Included, r => r.Email == "manual@negocio.cr");
        }

        [Fact]
        public async Task Resolve_OnlyManual_WhenAdminsDisabled()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var roles = await SeedRolesAsync(context);
            SeedTenant(context, tenantId);
            SeedUser(context, tenantId, "admin@negocio.cr", roles.AdminId, confirmed: true);
            await context.SaveChangesAsync();

            var settings = new TenantMonthlyReportSettings
            {
                TenantId = tenantId,
                SendToAllAdmins = false,
                IncludeManualRecipients = true,
                AdditionalRecipients = "manual@negocio.cr"
            };

            var resolver = ControllerTestSupport.CreateMonthlyReportRecipientResolver(context);
            var resolution = await resolver.ResolveAsync(tenantId, settings);

            Assert.Single(resolution.Included);
            Assert.Equal("manual@negocio.cr", resolution.Included[0].Email);
        }

        // ─────────────── Seeds ───────────────

        private static (string AdminId, string FuncionarioId) SeedRolesSync(
            ProyectoIdentity.Datos.ApplicationDbContext context)
        {
            var admin = new IdentityRole { Id = Guid.NewGuid().ToString(), Name = "Administrador", NormalizedName = "ADMINISTRADOR" };
            var func = new IdentityRole { Id = Guid.NewGuid().ToString(), Name = "Funcionario", NormalizedName = "FUNCIONARIO" };
            context.Roles.AddRange(admin, func);
            return (admin.Id, func.Id);
        }

        private static Task<(string AdminId, string FuncionarioId)> SeedRolesAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context) =>
            Task.FromResult(SeedRolesSync(context));

        private static void SeedTenant(ProyectoIdentity.Datos.ApplicationDbContext context, Guid tenantId) =>
            context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant { Id = tenantId, Nombre = "Negocio Test" });

        private static void SeedUser(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            string email,
            string roleId,
            bool confirmed,
            bool state = true,
            int? funcionarioId = null)
        {
            var user = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = confirmed,
                TenantId = tenantId,
                State = state,
                FuncionarioId = funcionarioId
            };
            context.Users.Add(user);
            context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = roleId });
        }
    }
}
