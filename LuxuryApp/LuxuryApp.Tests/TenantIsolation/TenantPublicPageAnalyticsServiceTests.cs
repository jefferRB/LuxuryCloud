using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.PublicPages;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.PublicPages;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantPublicPageAnalyticsServiceTests
    {
        [Fact]
        public async Task Redirects_GenerateServerSideTargetsAndAggregateDailyMetrics()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var service = await SeedPublishedPageAsync(context, tenantId, "tenant-metrics");
            context.ChangeTracker.Clear();
            tenantProvider.TenantId = Guid.Empty;

            var analytics = CreateAnalyticsService(context, tenantProvider);
            var redirects = CreateRedirectService(context, analytics);
            var request = CreateRequest("?redirectUrl=https://evil.test/path");

            await analytics.TryTrackAsync(tenantId, "tenant-metrics", TenantPublicPageMetricType.PageView);
            await analytics.TryTrackAsync(tenantId, "tenant-metrics", TenantPublicPageMetricType.PageView);

            var reserve = await redirects.ResolveReserveAsync("tenant-metrics", request);
            var serviceReserve = await redirects.ResolveServiceReserveAsync("tenant-metrics", service.Id, request);
            var whatsApp = await redirects.ResolveWhatsAppAsync("tenant-metrics");
            var maps = await redirects.ResolveMapsAsync("tenant-metrics");
            var waze = await redirects.ResolveWazeAsync("tenant-metrics");

            Assert.Equal("https://public.test/reservar/tenant-metrics", reserve?.Url);
            Assert.Equal($"https://public.test/reservar/tenant-metrics?servicioId={service.Id}", serviceReserve?.Url);
            Assert.Equal("https://wa.me/50688887777", whatsApp?.Url);
            Assert.Equal("https://maps.google.com/?q=tenant", maps?.Url);
            Assert.Equal("https://waze.com/ul?ll=9.9,-84.1&navigate=yes", waze?.Url);

            tenantProvider.TenantId = tenantId;
            var summary = await analytics.GetLast30DaysForCurrentTenantAsync();

            Assert.Equal(2, summary.PageViews);
            Assert.Equal(1, summary.ReserveClicks);
            Assert.Equal(1, summary.ServiceReserveClicks);
            Assert.Equal(1, summary.WhatsAppClicks);
            Assert.Equal(2, summary.MapsClicks);
            Assert.Equal("Corte", Assert.Single(summary.TopServices).ServiceName);

            var pageViewMetric = await context.TenantPublicPageDailyMetrics
                .IgnoreQueryFilters()
                .SingleAsync(metric => metric.MetricType == TenantPublicPageMetricType.PageView);

            Assert.Equal(2, pageViewMetric.Count);
            Assert.DoesNotContain(
                typeof(TenantPublicPageDailyMetric).GetProperties().Select(property => property.Name),
                name => name.Contains("Ip", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("UserAgent", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ServiceRedirect_InvalidOrCrossTenantService_DoesNotPreselect()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedPublishedPageAsync(context, tenantA, "tenant-a");

            tenantProvider.TenantId = tenantB;
            await EnsureTenantAsync(context, tenantB);
            var otherTenantService = await SeedServiceAsync(context, "Otro tenant");

            context.ChangeTracker.Clear();
            tenantProvider.TenantId = Guid.Empty;

            var analytics = CreateAnalyticsService(context, tenantProvider);
            var redirects = CreateRedirectService(context, analytics);
            var target = await redirects.ResolveServiceReserveAsync(
                "tenant-a",
                otherTenantService.Id,
                CreateRequest());

            Assert.Equal("https://public.test/reservar/tenant-a", target?.Url);

            var serviceMetric = await context.TenantPublicPageDailyMetrics
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(metric => metric.MetricType == TenantPublicPageMetricType.ServiceReserveClick);

            Assert.Null(serviceMetric);
        }

        private static ITenantPublicPageAnalyticsService CreateAnalyticsService(
            ApplicationDbContext context,
            TestTenantProvider tenantProvider) =>
            new TenantPublicPageAnalyticsService(
                context,
                tenantProvider,
                NullLogger<TenantPublicPageAnalyticsService>.Instance);

        private static TenantPublicPageRedirectService CreateRedirectService(
            ApplicationDbContext context,
            ITenantPublicPageAnalyticsService analyticsService) =>
            new(
                context,
                new PublicUrlValidationService(),
                analyticsService);

        private static HttpRequest CreateRequest(string? queryString = null)
        {
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("public.test");
            if (!string.IsNullOrWhiteSpace(queryString))
            {
                http.Request.QueryString = new QueryString(queryString);
            }

            return http.Request;
        }

        private static async Task<Servicio> SeedPublishedPageAsync(
            ApplicationDbContext context,
            Guid tenantId,
            string slug)
        {
            await EnsureTenantAsync(context, tenantId);

            context.TenantBookingSettings.Add(new TenantBookingSettings
            {
                PublicBookingEnabled = true,
                PublicBookingSlug = slug
            });

            context.TenantPublicPages.Add(new TenantPublicPage
            {
                IsPublished = true,
                HeroTitle = "Agenda tu cita",
                WhatsAppPhone = "+506 8888-7777",
                GoogleMapsUrl = "https://maps.google.com/?q=tenant",
                WazeUrl = "https://waze.com/ul?ll=9.9,-84.1&navigate=yes",
                ShowWhatsAppButton = true,
                ShowLocation = true,
                ShowServices = true
            });

            var service = await SeedServiceAsync(context, "Corte");
            context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting
            {
                ServicioId = service.Id,
                IsVisibleOnline = true,
                ShowPrice = true
            });

            await context.SaveChangesAsync();
            return service;
        }

        private static async Task EnsureTenantAsync(ApplicationDbContext context, Guid tenantId)
        {
            if (!await context.Tenants.IgnoreQueryFilters().AnyAsync(tenant => tenant.Id == tenantId))
            {
                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = $"Tenant {tenantId:N}", Activo = true });
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();
            }
        }

        private static async Task<Servicio> SeedServiceAsync(ApplicationDbContext context, string name)
        {
            var service = new Servicio
            {
                Nombre = name,
                DuracionMinutos = 30,
                Precio = 5000m,
                Activo = true
            };

            context.Servicios.Add(service);
            await context.SaveChangesAsync();
            return service;
        }
    }
}
