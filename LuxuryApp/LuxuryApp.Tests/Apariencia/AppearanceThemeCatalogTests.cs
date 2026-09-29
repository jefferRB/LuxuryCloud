using System.Text.Json;
using LuxuryApp.Services.Layout;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.Apariencia
{
    /// <summary>
    /// El catalogo de apariencia es la fuente unica de los temas del espacio privado: lo leen el
    /// script anti-parpadeo (_AppearanceBootstrap), el applier de site.js y las tarjetas del modal.
    /// Se prueba el contrato que hace que agregar un tema sea seguro:
    ///   - los identificadores son estables (se persisten en localStorage del navegador),
    ///   - cada tema resuelve por id y lo desconocido cae al default (fail-safe),
    ///   - cada tema tiene su bloque de tokens CSS (fondo + superficie) realmente definido.
    /// No se testea el aspecto visual: eso no aporta valor y se rompe con cualquier ajuste de color.
    /// </summary>
    public class AppearanceThemeCatalogTests
    {
        private static readonly string PrivateLayoutCssPath =
            TestProjectPaths.ProjectPath("wwwroot", "css", "private-layout.css");

        private static readonly string AppearanceModalPath =
            TestProjectPaths.ProjectPath("Views", "Shared", "_AppearanceModal.cshtml");

        // Los ids viajan a localStorage: renombrar uno le cambia el tema al usuario sin aviso.
        [Theory]
        [InlineData("classic-marble")]
        [InlineData("futuristic-premium")]
        [InlineData("absolute-black")]
        [InlineData("clean-white")]
        [InlineData("pink-elegant")]
        public void Resolve_DevuelveElTema_PorSuIdentificadorEstable(string id)
        {
            var theme = AppearanceThemeCatalog.Resolve(id);

            Assert.Equal(id, theme.Id);
            Assert.True(AppearanceThemeCatalog.TryResolve(id, out _));
        }

        [Fact]
        public void Catalogo_IncluyeRosaElegante_SinRomperLosTemasPrevios()
        {
            var ids = AppearanceThemeCatalog.All.Select(theme => theme.Id).ToList();

            Assert.Equal(
                new[] { "classic-marble", "futuristic-premium", "absolute-black", "clean-white", "pink-elegant" },
                ids);

            var rosa = AppearanceThemeCatalog.Resolve("pink-elegant");
            Assert.Equal("Rosa elegante", rosa.Label);
            Assert.Equal("pink", rosa.Background);
            Assert.Equal("rose", rosa.Surface);
        }

        [Fact]
        public void Resolve_ConIdDesconocidoOVacio_CaeAlTemaPorDefecto()
        {
            Assert.Equal(AppearanceThemeCatalog.DefaultThemeId, AppearanceThemeCatalog.Resolve("no-existe").Id);
            Assert.Equal(AppearanceThemeCatalog.DefaultThemeId, AppearanceThemeCatalog.Resolve(null).Id);
            Assert.Equal(AppearanceThemeCatalog.DefaultThemeId, AppearanceThemeCatalog.Resolve(string.Empty).Id);

            Assert.False(AppearanceThemeCatalog.TryResolve("no-existe", out var fallback));
            Assert.Equal(AppearanceThemeCatalog.DefaultThemeId, fallback.Id);
            Assert.Equal(AppearanceThemeCatalog.DefaultThemeId, AppearanceThemeCatalog.Default.Id);
        }

        [Fact]
        public void Catalogo_NoTieneIdsNiParesFondoSuperficieDuplicados()
        {
            var ids = AppearanceThemeCatalog.All.Select(theme => theme.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

            // El par (fondo, superficie) identifica el tema al migrar navegadores antiguos:
            // si se repitiera, esa migracion seria ambigua.
            var pairs = AppearanceThemeCatalog.All
                .Select(theme => $"{theme.Background}|{theme.Surface}")
                .ToList();
            Assert.Equal(pairs.Count, pairs.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void PresetsJson_ExponeElCatalogoCompleto_ParaElScriptAntiParpadeo()
        {
            using var document = JsonDocument.Parse(AppearanceThemeCatalog.PresetsJson);
            var root = document.RootElement;

            Assert.Equal(AppearanceThemeCatalog.All.Count, root.EnumerateObject().Count());

            foreach (var theme in AppearanceThemeCatalog.All)
            {
                var preset = root.GetProperty(theme.Id);
                Assert.Equal(theme.Label, preset.GetProperty("label").GetString());
                Assert.Equal(theme.Background, preset.GetProperty("background").GetString());
                Assert.Equal(theme.Surface, preset.GetProperty("surface").GetString());
            }
        }

        // Un tema registrado sin tokens CSS se veria como el tema por defecto: el catalogo y la
        // hoja de estilos tienen que moverse juntos. La superficie del tema por defecto es el
        // bloque base "body.private-shell" (no lleva clase propia); las demas si.
        [Fact]
        public void CadaTemaDelCatalogo_TieneSusTokensCssDefinidos()
        {
            var css = File.ReadAllText(PrivateLayoutCssPath);

            Assert.Contains("body.private-shell {", css);

            foreach (var theme in AppearanceThemeCatalog.All)
            {
                Assert.Contains($"theme-bg-{theme.Background}", css);
                Assert.Contains($".private-theme-card-preview-{theme.Background}", css);

                if (theme.Id != AppearanceThemeCatalog.DefaultThemeId)
                {
                    Assert.Contains($"body.private-shell.surface-{theme.Surface}", css);
                    Assert.Contains($"html[data-luxury-surface-theme=\"{theme.Surface}\"]", css);
                }
            }
        }

        // La vista dejo de listar temas a mano: si alguien vuelve a escribirlos, este test avisa.
        [Fact]
        public void ModalDeApariencia_SeGeneraDesdeElCatalogo_YNoListaTemasAMano()
        {
            var markup = File.ReadAllText(AppearanceModalPath);

            Assert.Contains("foreach (var theme in AppearanceThemeCatalog.All)", markup);
            Assert.Contains("data-luxury-appearance-option=\"@theme.Id\"", markup);

            foreach (var theme in AppearanceThemeCatalog.All)
            {
                Assert.DoesNotContain($"data-luxury-appearance-option=\"{theme.Id}\"", markup);
            }
        }
    }
}
