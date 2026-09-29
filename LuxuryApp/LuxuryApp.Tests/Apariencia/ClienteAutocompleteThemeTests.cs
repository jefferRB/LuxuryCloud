using System.Text.RegularExpressions;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.Apariencia
{
    /// <summary>
    /// El dropdown del autocomplete de clientes vive dentro del modal de "Nueva cita": tiene que
    /// tomar su superficie del tema activo, no de un color fijo. Antes usaba <c>#0f172a</c> y se
    /// veía navy en los cinco temas.
    /// </summary>
    public class ClienteAutocompleteThemeTests
    {
        /// <summary>Propiedades donde un color fijo rompería el tema (el foco del defecto).</summary>
        private static readonly string[] PropiedadesDeSuperficie = ["background", "background-color", "color"];

        /// <summary>
        /// Valores que no son un color propio: heredan el del tema, así que son válidos tal cual.
        /// </summary>
        private static readonly string[] ValoresNeutros = ["transparent", "inherit", "none", "currentcolor", "unset", "initial"];

        [Fact]
        public void LasReglasDelAutocompleteNoFijanColoresDeSuperficie()
        {
            var reglas = LeerReglasDelAutocomplete();

            Assert.NotEmpty(reglas);

            foreach (var (selector, declaracion) in reglas)
            {
                var propiedad = declaracion.Split(':', 2)[0].Trim().ToLowerInvariant();
                if (!PropiedadesDeSuperficie.Contains(propiedad))
                {
                    continue;
                }

                var valor = declaracion.Split(':', 2)[1];

                if (ValoresNeutros.Contains(valor.Trim().ToLowerInvariant()))
                {
                    continue;
                }

                // Un hex suelto solo se acepta como fallback de var(--token, #hex): ahí el color
                // nunca gana mientras el tema defina el token.
                var hexFueraDeVar = Regex.Replace(valor, @"var\([^)]*\)", string.Empty);

                Assert.False(
                    Regex.IsMatch(hexFueraDeVar, "#[0-9a-fA-F]{3,8}"),
                    $"'{selector}' fija un color de superficie sin pasar por un token del tema: {declaracion.Trim()}");

                Assert.True(
                    valor.Contains("var(--private-", StringComparison.Ordinal),
                    $"'{selector}' debe tomar '{propiedad}' de un token --private-* del tema: {declaracion.Trim()}");
            }
        }

        /// <summary>
        /// El navy persistía porque el navegador servía la copia cacheada de calendar.css: es la
        /// única hoja del layout privado que se enlazaba sin el hash de contenido en la URL.
        /// </summary>
        [Fact]
        public void ElLayoutPrivadoVersionaTodasSusHojasDeEstilo()
        {
            var layout = File.ReadAllText(
                TestProjectPaths.ProjectPath("Views", "Shared", "_Layout.cshtml"));

            var enlaces = Regex.Matches(layout, @"<link\s+rel=""stylesheet""\s+href=""~/css/[^""]+""[^>]*>");

            Assert.NotEmpty(enlaces);

            foreach (Match enlace in enlaces)
            {
                Assert.True(
                    enlace.Value.Contains("asp-append-version=\"true\"", StringComparison.Ordinal),
                    $"Sin asp-append-version el navegador sirve la copia vieja: {enlace.Value}");
            }
        }

        private static IReadOnlyList<(string Selector, string Declaracion)> LeerReglasDelAutocomplete()
        {
            var css = File.ReadAllText(
                TestProjectPaths.ProjectPath("wwwroot", "css", "calendar.css"));

            var reglas = new List<(string, string)>();

            foreach (Match bloque in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
            {
                var selector = bloque.Groups[1].Value.Trim();
                if (!selector.Contains(".cliente-autocomplete", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var declaracion in bloque.Groups[2].Value.Split(';'))
                {
                    if (declaracion.Contains(':', StringComparison.Ordinal))
                    {
                        reglas.Add((selector, declaracion));
                    }
                }
            }

            return reglas;
        }
    }
}
