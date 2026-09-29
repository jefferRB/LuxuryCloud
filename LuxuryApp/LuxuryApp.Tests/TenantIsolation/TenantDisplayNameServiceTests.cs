using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantDisplayNameServiceTests
    {
        [Fact]
        public async Task GetTenantDisplayNameAsync_ShouldPrioritizeUpdatedAccountDisplayName()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedTenantAsync(context, tenantId, "Jorhanna Diaz");
            await SeedUserAsync(context, tenantId, "  Barberia   jor  ");
            var service = CreateService(context, tenantProvider);

            var displayName = await service.GetTenantDisplayNameAsync(tenantId);

            Assert.Equal("Barberia jor", displayName);
        }

        [Fact]
        public async Task GetTenantDisplayNameAsync_ShouldFallbackToTenantName_WhenDisplayNameIsEmpty()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedTenantAsync(context, tenantId, "Jorhanna Diaz");
            await SeedUserAsync(context, tenantId, "   ");
            var service = CreateService(context, tenantProvider);

            var displayName = await service.GetTenantDisplayNameAsync(tenantId);

            Assert.Equal("Jorhanna Diaz", displayName);
        }

        [Fact]
        public async Task GetTenantDisplayNameAsync_ShouldNotReadAnotherTenantDisplayName()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedTenantAsync(context, tenantA, "Tenant A");
            await SeedUserAsync(context, tenantA, "Barberia A");

            tenantProvider.TenantId = tenantB;
            await SeedTenantAsync(context, tenantB, "Tenant B");
            await SeedUserAsync(context, tenantB, "Barberia B");

            var service = CreateService(context, tenantProvider);

            var displayName = await service.GetTenantDisplayNameAsync(tenantA);

            Assert.Equal("Barberia A", displayName);
        }

        [Fact]
        public async Task GetPublicTenantDisplayNameBySlugAsync_ShouldUseUpdatedDisplayName()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedTenantAsync(context, tenantId, "Jorhanna Diaz");
            await SeedUserAsync(context, tenantId, "Barberia jor");
            await SeedBookingSettingsAsync(context, "barberia-jor");
            var service = CreateService(context, tenantProvider);

            var displayName = await service.GetPublicTenantDisplayNameBySlugAsync("barberia-jor");

            Assert.Equal("Barberia jor", displayName);
        }

        [Fact]
        public async Task GetPublicTenantDisplayNameBySlugAsync_ShouldUseGenericFallback_WhenTenantNameLooksLikeEmail()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedTenantAsync(context, tenantId, "owner@test.local");
            await SeedBookingSettingsAsync(context, "correo-oculto");
            var service = CreateService(context, tenantProvider);

            var displayName = await service.GetPublicTenantDisplayNameBySlugAsync("correo-oculto");

            Assert.Equal(TenantDisplayNameService.DefaultDisplayName, displayName);
        }

        private static TenantDisplayNameService CreateService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TestTenantProvider tenantProvider) =>
            new(context, tenantProvider, new HttpContextAccessor());

        private static async Task SeedTenantAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            string name)
        {
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = name,
                Activo = true
            });

            await context.SaveChangesAsync();
        }

        private static async Task SeedUserAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            string? displayName)
        {
            context.Users.Add(new AppUsuario
            {
                Id = $"user-{Guid.NewGuid():N}",
                TenantId = tenantId,
                UserName = $"owner-{Guid.NewGuid():N}@test.local",
                Email = $"owner-{Guid.NewGuid():N}@test.local",
                Name = displayName,
                State = true
            });

            await context.SaveChangesAsync();
        }

        private static async Task SeedBookingSettingsAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string slug)
        {
            context.TenantBookingSettings.Add(new TenantBookingSettings
            {
                PublicBookingEnabled = true,
                PublicBookingSlug = slug,
                WorkingDaysMask = 0b1111111,
                OpenTime = new TimeOnly(8, 0),
                CloseTime = new TimeOnly(17, 0),
                SlotIntervalMinutes = 30
            });

            await context.SaveChangesAsync();
        }
    }
}
