using LuxuryApp.Models.Reports;
using LuxuryApp.Services.Common;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PublicSiteUrlTests
    {
        // ─────────────── PublicSiteOptions (lógica pura, sin HttpContext) ───────────────

        [Fact]
        public void ResolveDashboardUrl_ValidBase_ReturnsAbsoluteDashboardUrl()
        {
            var options = new PublicSiteOptions { PublicBaseUrl = "https://www.luxurycloud.app" };
            Assert.Equal("https://www.luxurycloud.app/Dashboard", options.ResolveDashboardUrl());
        }

        [Fact]
        public void ResolveDashboardUrl_TrailingSlash_DoesNotDuplicateSlash()
        {
            var options = new PublicSiteOptions { PublicBaseUrl = "https://www.luxurycloud.app/" };
            Assert.Equal("https://www.luxurycloud.app/Dashboard", options.ResolveDashboardUrl());
        }

        [Fact]
        public void ResolveDashboardUrl_Empty_ReturnsNull()
        {
            Assert.Null(new PublicSiteOptions { PublicBaseUrl = "" }.ResolveDashboardUrl());
            Assert.Null(new PublicSiteOptions { PublicBaseUrl = "   " }.ResolveDashboardUrl());
        }

        [Theory]
        [InlineData("https://abc123.ngrok.io")]
        [InlineData("https://abc123.ngrok-free.app")]
        [InlineData("http://localhost:5000")]
        [InlineData("http://127.0.0.1:5000")]
        [InlineData("https://mi-pc.local")]
        [InlineData("not-a-url")]
        [InlineData("ftp://luxurycloud.app")]
        public void ResolveDashboardUrl_UnsafeOrInvalidBase_ReturnsNull(string value)
        {
            Assert.Null(new PublicSiteOptions { PublicBaseUrl = value }.ResolveDashboardUrl());
            Assert.False(PublicSiteOptions.IsPublicBaseValid(value));
        }

        // ─────────────── Integración: el correo usa la URL pública, sin HttpContext ───────────────

        [Fact]
        public async Task Email_UsesConfiguredPublicUrl_WithoutHttpContext()
        {
            var (service, context, connection) = BuildService("https://www.luxurycloud.app/");
            using var _ = context;
            using var __ = connection;

            var html = await service.RenderEmailHtmlAsync(SampleReport());

            Assert.Contains("https://www.luxurycloud.app/Dashboard", html);
            Assert.Contains("Ver dashboard completo", html);
            // Nunca un host de desarrollo.
            Assert.DoesNotContain("ngrok", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("localhost", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("127.0.0.1", html);
        }

        [Fact]
        public async Task Email_WithNgrokBase_OmitsButton_NeverLeaksHost()
        {
            var (service, context, connection) = BuildService("https://abc123.ngrok.io");
            using var _ = context;
            using var __ = connection;

            var html = await service.RenderEmailHtmlAsync(SampleReport());

            Assert.DoesNotContain("Ver dashboard completo", html);
            Assert.DoesNotContain("ngrok", html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Email_WithoutPublicUrl_OmitsButton()
        {
            var (service, context, connection) = BuildService(baseUrl: "");
            using var _ = context;
            using var __ = connection;

            var html = await service.RenderEmailHtmlAsync(SampleReport());

            Assert.DoesNotContain("Ver dashboard completo", html);
        }

        // ─────────────── Soporte ───────────────

        private static (LuxuryApp.Services.Reports.IMonthlyBusinessReportService, ProyectoIdentity.Datos.ApplicationDbContext, Microsoft.Data.Sqlite.SqliteConnection) BuildService(string baseUrl)
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(
                context, tenantProvider, new FakeMonthlyReportEmailSender(), baseUrl: baseUrl);
            return (service, context, connection);
        }

        private static MonthlyBusinessReportViewModel SampleReport() => new()
        {
            NombreNegocio = "Barbería Luxury",
            Mes = 6,
            Anio = 2026,
            MesNombre = "Junio",
            TieneActividad = true,
            ResumenEjecutivoTexto = "Resumen de prueba."
        };
    }
}
