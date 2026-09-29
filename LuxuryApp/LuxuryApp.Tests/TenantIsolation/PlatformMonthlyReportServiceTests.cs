using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Reports;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Reports;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PlatformMonthlyReportServiceTests
    {
        [Fact]
        public async Task GetOverview_ListsTenants_WithStatusRecipientsAndLastLog_WithoutMixingTenants()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var adminRole = new IdentityRole { Id = Guid.NewGuid().ToString(), Name = "Administrador", NormalizedName = "ADMINISTRADOR" };
            context.Roles.Add(adminRole);
            context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant { Id = tenantA, Nombre = "Salón Alfa" });
            context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant { Id = tenantB, Nombre = "Salón Beta" });
            await context.SaveChangesAsync();

            // Tenant A: activado, 1 admin, 1 log Sent.
            tenantProvider.TenantId = tenantA;
            AddAdmin(context, tenantA, "adminA@alfa.cr", adminRole.Id);
            context.TenantMonthlyReportSettings.Add(new TenantMonthlyReportSettings
            {
                TenantId = tenantA,
                IsEnabled = true,
                SendToAllAdmins = true,
                SendDayOfMonth = 1,
                SendHour = 8,
                CreatedAt = new DateTime(2026, 6, 1),
                UpdatedAt = new DateTime(2026, 6, 1)
            });
            context.TenantMonthlyReportEmailLogs.Add(new TenantMonthlyReportEmailLog
            {
                TenantId = tenantA,
                ReportYear = 2026,
                ReportMonth = 6,
                RecipientEmail = "adminA@alfa.cr",
                Subject = "s",
                Status = MonthlyReportEmailStatus.Sent,
                IsTest = false,
                CreatedAt = new DateTime(2026, 7, 1, 8, 0, 0)
            });
            await context.SaveChangesAsync();

            // Tenant B: desactivado, 1 admin distinto, sin logs.
            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();
            AddAdmin(context, tenantB, "adminB@beta.cr", adminRole.Id);
            context.TenantMonthlyReportSettings.Add(new TenantMonthlyReportSettings
            {
                TenantId = tenantB,
                IsEnabled = false,
                SendToAllAdmins = true,
                CreatedAt = new DateTime(2026, 6, 1),
                UpdatedAt = new DateTime(2026, 6, 1)
            });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = CreateService(context);
            var overview = await service.GetOverviewAsync();

            Assert.Equal(2, overview.Rows.Count);

            var rowA = overview.Rows.Single(r => r.TenantId == tenantA);
            Assert.Equal("Salón Alfa", rowA.BusinessName);
            Assert.True(rowA.IsEnabled);
            Assert.Equal(1, rowA.RecipientCount); // solo el admin de A
            Assert.Equal(MonthlyReportEmailStatus.Sent, rowA.LastStatus);

            var rowB = overview.Rows.Single(r => r.TenantId == tenantB);
            Assert.Equal("Salón Beta", rowB.BusinessName);
            Assert.False(rowB.IsEnabled);
            Assert.Equal(1, rowB.RecipientCount); // solo el admin de B, no el de A
            Assert.Null(rowB.LastStatus);
        }

        [Fact]
        public async Task GetTenantDetail_ReturnsOnlyThatTenantLogs()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant { Id = tenantA, Nombre = "Alfa" });
            context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant { Id = tenantB, Nombre = "Beta" });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA;
            AddLog(context, tenantA, "a@alfa.cr");
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();
            AddLog(context, tenantB, "b@beta.cr");
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = CreateService(context);
            var detail = await service.GetTenantDetailAsync(tenantA);

            Assert.NotNull(detail);
            Assert.All(detail!.Logs, l => Assert.Equal(tenantA, l.TenantId));
            Assert.DoesNotContain(detail.Logs, l => l.RecipientEmail == "b@beta.cr");
        }

        // ─────────────── Soporte ───────────────

        private static IPlatformMonthlyReportService CreateService(ProyectoIdentity.Datos.ApplicationDbContext context)
        {
            var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            var tenantExecution = new TenantExecutionService(
                scopeFactory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantExecutionService>.Instance);

            return new PlatformMonthlyReportService(
                context,
                ControllerTestSupport.CreateMonthlyReportRecipientResolver(context),
                tenantExecution,
                ControllerTestSupport.BusinessDateTimeProvider,
                new StaticOptionsMonitor<MonthlyReportSchedulerOptions>(
                    new MonthlyReportSchedulerOptions { SchedulerEnabled = true }));
        }

        private static void AddAdmin(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            string email,
            string adminRoleId)
        {
            var user = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = true,
                TenantId = tenantId,
                State = true
            };
            context.Users.Add(user);
            context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = adminRoleId });
        }

        private static void AddLog(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            string email) =>
            context.TenantMonthlyReportEmailLogs.Add(new TenantMonthlyReportEmailLog
            {
                TenantId = tenantId,
                ReportYear = 2026,
                ReportMonth = 6,
                RecipientEmail = email,
                Subject = "s",
                Status = MonthlyReportEmailStatus.Sent,
                IsTest = false,
                CreatedAt = new DateTime(2026, 7, 1, 8, 0, 0)
            });
    }
}
