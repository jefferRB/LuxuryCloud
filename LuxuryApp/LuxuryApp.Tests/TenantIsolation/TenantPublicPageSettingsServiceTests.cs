using LuxuryApp.Models.PublicPages;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.PublicPages;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantPublicPageSettingsServiceTests
    {
        [Fact]
        public async Task SaveForCurrentTenant_OnlyUpdatesCurrentTenant()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantA, "Tenant A");
            var serviceA = CreateSettingsService(context, tenantProvider, "Tenant A");
            await serviceA.BuildForCurrentTenantAsync(CreateRequest());
            await serviceA.SaveForCurrentTenantAsync(new EditTenantPublicPageViewModel
            {
                HeroTitle = "Landing A",
                HeroEyebrow = "Studio A",
                BusinessHours = "Lun a Vie\n9 a.m. - 6 p.m.",
                IsPublished = true
            }, "user-a");

            tenantProvider.TenantId = tenantB;
            await SeedTenantAsync(context, tenantB, "Tenant B");
            var serviceB = CreateSettingsService(context, tenantProvider, "Tenant B");
            await serviceB.BuildForCurrentTenantAsync(CreateRequest());
            await serviceB.SaveForCurrentTenantAsync(new EditTenantPublicPageViewModel
            {
                HeroTitle = "Landing B",
                IsPublished = true
            }, "user-b");

            var pages = await context.TenantPublicPages
                .IgnoreQueryFilters()
                .AsNoTracking()
                .OrderBy(page => page.HeroTitle)
                .ToListAsync();

            Assert.Equal(2, pages.Count);
            Assert.Contains(pages, page => page.TenantId == tenantA && page.HeroTitle == "Landing A");
            Assert.Contains(pages, page => page.TenantId == tenantA && page.HeroEyebrow == "Studio A");
            Assert.Contains(pages, page => page.TenantId == tenantA && page.BusinessHours != null && page.BusinessHours.Contains("9 a.m.", StringComparison.Ordinal));
            Assert.Contains(pages, page => page.TenantId == tenantB && page.HeroTitle == "Landing B");
        }

        [Fact]
        public void EditViewModel_DoesNotExposeTenantId()
        {
            Assert.Null(typeof(EditTenantPublicPageViewModel).GetProperty("TenantId"));
        }

        [Fact]
        public async Task SaveForCurrentTenant_RejectsInvalidUrls()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantProvider.TenantId, "Tenant URL");

            var service = CreateSettingsService(context, tenantProvider, "Tenant URL");

            var ex = await Assert.ThrowsAsync<TenantPublicPageValidationException>(() =>
                service.SaveForCurrentTenantAsync(new EditTenantPublicPageViewModel
                {
                    InstagramUrl = "javascript:alert(1)"
                }, "user"));

            Assert.Equal(nameof(EditTenantPublicPageViewModel.InstagramUrl), ex.Field);
        }

        [Fact]
        public async Task SaveForCurrentTenant_RejectsInvalidWazeUrl()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantProvider.TenantId, "Tenant Waze");

            var service = CreateSettingsService(context, tenantProvider, "Tenant Waze");

            var ex = await Assert.ThrowsAsync<TenantPublicPageValidationException>(() =>
                service.SaveForCurrentTenantAsync(new EditTenantPublicPageViewModel
                {
                    WazeUrl = "https://evil.example/ul"
                }, "user"));

            Assert.Equal(nameof(EditTenantPublicPageViewModel.WazeUrl), ex.Field);
        }

        [Fact]
        public async Task SaveForCurrentTenant_RejectsHtmlInConfigurableText()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantProvider.TenantId, "Tenant HTML");

            var service = CreateSettingsService(context, tenantProvider, "Tenant HTML");

            var ex = await Assert.ThrowsAsync<TenantPublicPageValidationException>(() =>
                service.SaveForCurrentTenantAsync(new EditTenantPublicPageViewModel
                {
                    BusinessHours = "Lun a Vie\n<strong>promo</strong>"
                }, "user"));

            Assert.Equal(nameof(EditTenantPublicPageViewModel.BusinessHours), ex.Field);
        }

        private static TenantPublicPageSettingsService CreateSettingsService(
            ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            string displayName) =>
            new(
                context,
                tenantProvider,
                new FakeTenantDisplayNameService(displayName),
                new PublicUrlValidationService(),
                new PublicAssetQuotaService(context, Options.Create(new PublicImageOptions())),
                new TenantPublicPageAnalyticsService(
                    context,
                    tenantProvider,
                    NullLogger<TenantPublicPageAnalyticsService>.Instance),
                new BusinessScheduleService(),
                Options.Create(new PublicImageOptions()));

        private static HttpRequest CreateRequest()
        {
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("public.test");
            return http.Request;
        }

        private static async Task SeedTenantAsync(
            ApplicationDbContext context,
            Guid tenantId,
            string name)
        {
            if (await context.Tenants.IgnoreQueryFilters().AnyAsync(tenant => tenant.Id == tenantId))
            {
                return;
            }

            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = name, Activo = true });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        private sealed class FakeTenantDisplayNameService : ITenantDisplayNameService
        {
            private readonly string _displayName;

            public FakeTenantDisplayNameService(string displayName)
            {
                _displayName = displayName;
            }

            public Task<string> GetCurrentTenantDisplayNameAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(_displayName);

            public Task<string> GetTenantDisplayNameAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                Task.FromResult(_displayName);

            public Task<string?> GetPublicTenantDisplayNameBySlugAsync(
                string slug,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>(_displayName);

            public string NormalizeDisplayName(string? value) =>
                string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

            public bool ContainsInvalidDisplayNameCharacters(string? value) => false;
        }
    }
}
