using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class LegacyUserStateRepairServiceTests
    {
        [Fact]
        public async Task RepairAsync_ShouldReactivateLegacyUsersWithActiveTenant()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var activeTenantId = Guid.NewGuid();
            var inactiveTenantId = Guid.NewGuid();

            context.Tenants.AddRange(
                new Tenant
                {
                    Id = activeTenantId,
                    Nombre = "Tenant Activo",
                    Activo = true
                },
                new Tenant
                {
                    Id = inactiveTenantId,
                    Nombre = "Tenant Inactivo",
                    Activo = false
                });

            context.Users.AddRange(
                new AppUsuario
                {
                    Id = "user-active",
                    UserName = "active@test.local",
                    NormalizedUserName = "ACTIVE@TEST.LOCAL",
                    Email = "active@test.local",
                    NormalizedEmail = "ACTIVE@TEST.LOCAL",
                    TenantId = activeTenantId,
                    State = false
                },
                new AppUsuario
                {
                    Id = "user-inactive",
                    UserName = "inactive@test.local",
                    NormalizedUserName = "INACTIVE@TEST.LOCAL",
                    Email = "inactive@test.local",
                    NormalizedEmail = "INACTIVE@TEST.LOCAL",
                    TenantId = inactiveTenantId,
                    State = false
                });

            await context.SaveChangesAsync();

            var repairService = new LegacyUserStateRepairService(
                context,
                NullLogger<LegacyUserStateRepairService>.Instance);

            var repaired = await repairService.RepairAsync();

            Assert.Equal(1, repaired);
            Assert.True(context.Users.Single(user => user.Id == "user-active").State);
            Assert.False(context.Users.Single(user => user.Id == "user-inactive").State);
        }
    }
}
