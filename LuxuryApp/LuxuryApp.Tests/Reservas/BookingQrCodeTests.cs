using LuxuryApp.Controllers.Reservas;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.Common;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Tests.Reservas
{
    /// <summary>
    /// QR del enlace público de reservas: qué codifica (solo la URL canónica), de dónde sale esa
    /// URL y que nadie pueda pedir el QR de otro negocio.
    /// </summary>
    public class BookingQrCodeTests
    {
        // ─────────────────────────── URL canónica ───────────────────────────

        [Fact]
        public void EnlaceCanonico_PrefiereLaBasePublicaConfigurada_NoElHostDelRequest()
        {
            var opciones = new PublicSiteOptions { PublicBaseUrl = "https://app.luxurycloud.app" };

            var url = BookingLinkBuilder.BuildCanonical(opciones, RequestDe("localhost:5001", "http"), "northside-studio");

            Assert.Equal("https://app.luxurycloud.app/reservar/northside-studio", url);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("http://localhost:5001")]
        [InlineData("https://sake-bunt-drove.ngrok-free.dev")]
        public void SinBasePublicaValida_CaeAlHostDelRequest(string baseUrl)
        {
            // En desarrollo y detrás de Nginx el host del request YA es el público
            // (UseForwardedHeaders). Lo que nunca ocurre es quemar una URL en el código.
            var opciones = new PublicSiteOptions { PublicBaseUrl = baseUrl };

            var url = BookingLinkBuilder.BuildCanonical(opciones, RequestDe("mi-negocio.cr", "https"), "northside-studio");

            Assert.Equal("https://mi-negocio.cr/reservar/northside-studio", url);
        }

        [Fact]
        public void SinSlug_NoHayEnlace()
        {
            var opciones = new PublicSiteOptions { PublicBaseUrl = "https://app.luxurycloud.app" };

            Assert.Null(BookingLinkBuilder.BuildCanonical(opciones, RequestDe("app.luxurycloud.app", "https"), null));
            Assert.Null(BookingLinkBuilder.BuildCanonical(opciones, RequestDe("app.luxurycloud.app", "https"), "  "));
        }

        // ─────────────────────────── Generación local ───────────────────────────

        [Fact]
        public void Png_SeGeneraLocalmenteYEsUnPngValido()
        {
            var imagen = new BookingQrCodeService()
                .CreatePng("https://app.luxurycloud.app/reservar/northside-studio", "northside-studio");

            Assert.Equal("image/png", imagen.ContentType);
            Assert.Equal("reservas-northside-studio.png", imagen.NombreArchivo);

            // Firma PNG: 89 50 4E 47 0D 0A 1A 0A.
            Assert.True(imagen.Contenido.Length > 8);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, imagen.Contenido.Take(8).ToArray());
        }

        [Fact]
        public void Svg_EsVectorialYNoLlevaDatosSensibles()
        {
            const string url = "https://app.luxurycloud.app/reservar/northside-studio";
            var tenantId = Guid.NewGuid();

            var imagen = new BookingQrCodeService().CreateSvg(url, "northside-studio");
            var svg = System.Text.Encoding.UTF8.GetString(imagen.Contenido);

            Assert.Equal("image/svg+xml", imagen.ContentType);
            Assert.Equal("reservas-northside-studio.svg", imagen.NombreArchivo);
            Assert.Contains("<svg", svg, StringComparison.OrdinalIgnoreCase);

            // El QR se dibuja a partir de la URL y nada más: ni el TenantId ni ningún token
            // viajan dentro de la imagen.
            Assert.DoesNotContain(tenantId.ToString(), svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TenantId", svg, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NombreDeArchivo_NoArrastraCaracteresRaros()
        {
            var imagen = new BookingQrCodeService()
                .CreatePng("https://app.luxurycloud.app/reservar/x", "../../etc/passwd\"; drop");

            // Se conservan solo letras, dígitos y guiones: nada de rutas ni comillas.
            Assert.Equal("reservas-etcpasswddrop.png", imagen.NombreArchivo);
        }

        // ─────────────────────────── Endpoint ───────────────────────────

        [Fact]
        public async Task QrCode_UsaElSlugDelTenantAutenticado_NoUnoDelQueryString()
        {
            // El controlador NO expone un parámetro de slug: el único slug posible es el del
            // tenant en contexto, así que no hay forma de pedir el QR de otro negocio.
            var metodo = typeof(ReservasController).GetMethod(nameof(ReservasController.QrCode));
            Assert.NotNull(metodo);
            Assert.DoesNotContain(metodo!.GetParameters(), parametro =>
                parametro.Name!.Contains("slug", StringComparison.OrdinalIgnoreCase) ||
                parametro.Name!.Contains("tenant", StringComparison.OrdinalIgnoreCase));

            var controller = BuildController(slug: "northside-studio");
            var resultado = await controller.QrCode(formato: null, CancellationToken.None);

            var archivo = Assert.IsType<FileContentResult>(resultado);
            Assert.Equal("image/png", archivo.ContentType);
            // Vista en pantalla, no descarga: sin nombre de archivo adjunto.
            Assert.True(string.IsNullOrEmpty(archivo.FileDownloadName));
        }

        [Fact]
        public async Task QrCode_ConFormatoSvg_DevuelveSvg()
        {
            var controller = BuildController(slug: "northside-studio");

            var archivo = Assert.IsType<FileContentResult>(
                await controller.QrCode(formato: "svg", CancellationToken.None));

            Assert.Equal("image/svg+xml", archivo.ContentType);
        }

        [Fact]
        public async Task QrCode_ConDescargar_TraeNombreDeArchivo()
        {
            var controller = BuildController(slug: "northside-studio");
            controller.Request.QueryString = new QueryString("?descargar=1");

            var archivo = Assert.IsType<FileContentResult>(
                await controller.QrCode(formato: "png", CancellationToken.None));

            Assert.Equal("reservas-northside-studio.png", archivo.FileDownloadName);
        }

        [Fact]
        public async Task QrCode_SinSlugConfigurado_DevuelveNotFound()
        {
            var controller = BuildController(slug: null);

            Assert.IsType<NotFoundResult>(await controller.QrCode(formato: null, CancellationToken.None));
        }

        // ─────────────────────────── helpers ───────────────────────────

        private static HttpRequest RequestDe(string host, string esquema)
        {
            var context = new DefaultHttpContext();
            context.Request.Scheme = esquema;
            context.Request.Host = new HostString(host);
            return context.Request;
        }

        private static ReservasController BuildController(string? slug)
        {
            var controller = ControllerTestSupport.CreateReservasController(
                new SpyBookingRequestService(),
                new SlugSettingsService(slug),
                new StubCatalogService(),
                publicBaseUrl: "https://app.luxurycloud.app");

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("reservas-user", Guid.NewGuid()));

            return controller;
        }

        private sealed class SlugSettingsService : IBookingSettingsService
        {
            private readonly string? _slug;

            public SlugSettingsService(string? slug) => _slug = slug;

            public Task<BookingSettingsViewModel> BuildSettingsViewModelAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new BookingSettingsViewModel { PublicBookingSlug = _slug });

            public Task SaveSettingsAsync(BookingSettingsViewModel input, string? userId, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<PublicBookingTenantContext?> ResolvePublicBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
                Task.FromResult<PublicBookingTenantContext?>(null);

            public Task<string?> GetCurrentSlugAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(_slug);
        }

        private sealed class StubCatalogService : IBookingCatalogService
        {
            public Task<IReadOnlyList<PublicBookingServiceOption>> GetPublicServicesAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PublicBookingServiceOption>>(Array.Empty<PublicBookingServiceOption>());

            public Task<IReadOnlyList<int>> GetCompatibleFuncionarioIdsAsync(int servicioId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

            public Task<bool> IsServiceVisibleOnlineAsync(int servicioId, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);

            public Task<BookingCatalogViewModel> BuildManagementAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new BookingCatalogViewModel());

            public Task SaveAsync(BookingCatalogSaveInput input, string? userId, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
