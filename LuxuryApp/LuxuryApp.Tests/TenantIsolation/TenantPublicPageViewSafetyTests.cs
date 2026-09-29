using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantPublicPageViewSafetyTests
    {
        [Fact]
        public void PublicLandingView_UsesSafeResponsiveGalleryAndServiceCardClasses()
        {
            var view = File.ReadAllText(ProjectPath("Views", "PublicSite", "Index.cshtml"));
            var css = File.ReadAllText(ProjectPath("wwwroot", "css", "tenant-public-page.css"));

            Assert.DoesNotContain("Html.Raw", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<script", view, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tpp-site-nav", view);
            Assert.Contains("tpp-nav-toggle", view);
            Assert.Contains("tpp-floating-whatsapp", view);
            Assert.Contains("Model.HeroEyebrow", view);
            Assert.Contains("Model.BusinessHours", view);
            Assert.Contains("Model.WazeActionUrl", view);
            Assert.Contains("var hasGallery = Model.BusinessGallery.Count > 0", view);
            Assert.Contains("public-gallery-grid", view);
            Assert.Contains("data-count=\"@Model.BusinessGallery.Count\"", view);
            Assert.Contains("tpp-gallery-item", view);
            Assert.Contains("loading=\"lazy\"", view);

            Assert.Contains("public-services-grid", view);
            Assert.Contains("public-service-card", view);
            Assert.Contains("public-service-image", view);
            Assert.Contains("public-service-body", view);
            Assert.Contains("public-service-cta", view);
            Assert.Contains("service.ReserveActionUrl ?? service.BookingUrl", view);

            Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", css);
            Assert.Contains("aspect-ratio: 4 / 5", css);
            Assert.Contains("object-fit: cover", css);
            Assert.Contains("object-position: center", css);
            Assert.Contains(".public-service-image img", css);
            Assert.Contains(".tpp-site-nav", css);
            Assert.Contains(".tpp-floating-whatsapp", css);
            Assert.Contains("scroll-behavior: smooth", css);

            // Formatos flexibles: servicios verticales (4/5), logo contenido y galeria con aspecto natural.
            Assert.Contains("object-fit: contain", css);
            Assert.Contains("aspect-ratio:{image.Width}/{image.Height}", view);
        }

        [Fact]
        public void AdminPublicPage_UsesLocalCropperScriptAndNoHtmlRaw()
        {
            var view = File.ReadAllText(ProjectPath("Views", "PaginaPublica", "Index.cshtml"));
            var script = File.ReadAllText(ProjectPath("wwwroot", "js", "tenant-public-image-uploader.js"));

            Assert.DoesNotContain("Html.Raw", view, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("~/js/tenant-public-image-uploader.js", view);
            Assert.Contains("data-public-image-upload", view);
            Assert.Contains("data-crop-aspect", view);
            Assert.Contains("asp-for=\"HeroEyebrow\"", view);
            Assert.Contains("asp-for=\"BusinessHours\"", view);
            Assert.Contains("asp-for=\"WazeUrl\"", view);
            Assert.Contains("CropX", script);
            Assert.Contains("fetch(", script);
            Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ServiceCards_UseAStableSquareFrameThatCannotStretchTheRow()
        {
            var css = File.ReadAllText(ProjectPath("wwwroot", "css", "tenant-public-page.css"));

            var mediaBlock = css[css.IndexOf(".tpp-service-image,", StringComparison.Ordinal)..];
            mediaBlock = mediaBlock[..mediaBlock.IndexOf(".tpp-service-body", StringComparison.Ordinal)];

            // Marco vertical 3:4 fijo y sin tope de altura que rompa la proporcion.
            Assert.Contains("aspect-ratio: 3 / 4", mediaBlock);
            Assert.DoesNotContain("max-height", mediaBlock);
            Assert.Contains("object-fit: cover", mediaBlock);

            // Sin stretch: un servicio sin foto no crece hasta la altura del que si tiene.
            var gridBlock = css[css.IndexOf("grid-template-columns: repeat(auto-fit, minmax(270px, 1fr))", StringComparison.Ordinal)..];
            gridBlock = gridBlock[..gridBlock.IndexOf("}", StringComparison.Ordinal)];
            Assert.Contains("align-items: start", gridBlock);
            Assert.DoesNotContain("align-items: stretch", gridBlock);
        }

        [Fact]
        public void BrandTokens_HaveNeutralDefaultsAndAreInjectedOnlyWhenTenantChoseAColor()
        {
            var css = File.ReadAllText(ProjectPath("wwwroot", "css", "tenant-public-page.css"));
            var view = File.ReadAllText(ProjectPath("Views", "PublicSite", "Index.cshtml"));

            // Defaults: negro para los CTA neutros y verde para el acento historico.
            Assert.Contains("--brand-accent: #111111", css);
            Assert.Contains("--brand-on-accent: #ffffff", css);
            Assert.Contains("--tpp-accent: #0d7a5f", css);
            Assert.Contains("--tpp-accent-ink: #075944", css);

            // Los CTA principales leen los tokens, no colores sueltos.
            Assert.Contains("background: var(--brand-accent)", css);
            Assert.Contains("color: var(--brand-on-accent)", css);

            // Sin color configurado no se emite ningun estilo: la landing queda igual que antes.
            Assert.Contains("@if (Model.Theme.IsCustom)", view);
            Assert.Contains("--brand-accent: @Model.Theme.Accent", view);
        }

        [Fact]
        public void PublicLandingCsp_AllowsOnlyLocalScriptsAndNoFrames()
        {
            var controller = File.ReadAllText(ProjectPath("Controllers", "PublicSiteController.cs"));

            // Solo script local propio ('self'); nunca 'none' (bloquearia el menu) ni inline/eval/externos.
            Assert.Contains("\"script-src 'self'\"", controller);
            Assert.DoesNotContain("\"script-src 'none'\"", controller);
            Assert.DoesNotContain("unsafe-eval", controller, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"frame-ancestors 'none'\"", controller);
            Assert.Contains("\"object-src 'none'\"", controller);
            Assert.DoesNotContain("iframe", controller, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PublicLanding_HasSectionAnchorsFloatingActionsAndLocationImage()
        {
            var view = File.ReadAllText(ProjectPath("Views", "PublicSite", "Index.cshtml"));

            // Anclas de navegacion de una sola pagina.
            Assert.Contains("id=\"inicio\"", view);
            Assert.Contains("id=\"trabajos\"", view);
            Assert.Contains("id=\"servicios\"", view);
            Assert.Contains("id=\"equipo\"", view);
            Assert.Contains("id=\"ubicacion\"", view);
            Assert.Contains("href=\"#inicio\"", view);
            Assert.Contains("href=\"#servicios\"", view);
            Assert.Contains("href=\"#ubicacion\"", view);

            // Boton reservar (usa la accion trackeada o el booking url).
            Assert.Contains("tpp-nav-cta", view);
            Assert.Contains("reserveUrl", view);

            // Burbujas flotantes: WhatsApp + Instagram condicional.
            Assert.Contains("tpp-floating-actions", view);
            Assert.Contains("tpp-floating-instagram", view);
            Assert.Contains("tpp-floating-whatsapp", view);

            // Imagen de ubicacion condicional (sin bloque vacio si no existe).
            Assert.Contains("Model.LocationImage", view);
            Assert.Contains("tpp-location-media", view);
        }

        [Fact]
        public void PublicLayout_LoadsLocalMenuScriptOnly()
        {
            var layout = File.ReadAllText(ProjectPath("Views", "Shared", "_TenantPublicPageLayout.cshtml"));
            var menuScript = File.ReadAllText(ProjectPath("wwwroot", "js", "tenant-public-page.js"));

            Assert.Contains("~/js/tenant-public-page.js", layout);
            // Script local, sin CDN ni recursos externos.
            Assert.DoesNotContain("https://", menuScript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http://", menuScript, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tpp-nav-toggle", menuScript);
        }

        private static string ProjectPath(params string[] parts)
        {
            return TestProjectPaths.ProjectPath(parts);
        }
    }
}
