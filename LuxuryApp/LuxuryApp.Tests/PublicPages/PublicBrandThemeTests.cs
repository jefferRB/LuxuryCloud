using LuxuryApp.Models.PublicPages;

namespace LuxuryApp.Tests.PublicPages
{
    /// <summary>
    /// El negocio elige UN color; el resto de tokens se derivan. Estas pruebas cubren la
    /// promesa visible: cualquier color elegido produce texto legible y el default no cambia.
    /// </summary>
    public class PublicBrandThemeTests
    {
        [Theory]
        [InlineData("#E83E8C", "#e83e8c")]
        [InlineData("e83e8c", "#e83e8c")]
        [InlineData("  #111111  ", "#111111")]
        public void Normalize_ValidHex_ReturnsLowercaseWithHash(string input, string expected)
        {
            Assert.Equal(expected, PublicBrandTheme.Normalize(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("#12345")]
        [InlineData("#1234567")]
        [InlineData("#ZZZZZZ")]
        [InlineData("red")]
        [InlineData("#fff")]
        [InlineData("#111111; background:url(javascript:alert(1))")]
        public void Normalize_InvalidHex_ReturnsNull(string? input)
        {
            Assert.Null(PublicBrandTheme.Normalize(input));
        }

        [Fact]
        public void Resolve_Null_FallsBackToBlackAndIsNotCustom()
        {
            var theme = PublicBrandTheme.Resolve(null);

            Assert.Equal(PublicBrandTheme.DefaultAccentHex, theme.Accent);
            Assert.False(theme.IsCustom);
            Assert.Equal("#ffffff", theme.OnAccent);
        }

        [Fact]
        public void Resolve_InvalidHex_FallsBackToBlackWithoutThrowing()
        {
            var theme = PublicBrandTheme.Resolve("not-a-color");

            Assert.Equal(PublicBrandTheme.DefaultAccentHex, theme.Accent);
            Assert.False(theme.IsCustom);
        }

        [Fact]
        public void Resolve_ValidHex_MarksThemeAsCustom()
        {
            var theme = PublicBrandTheme.Resolve("#E83E8C");

            Assert.True(theme.IsCustom);
            Assert.Equal("#e83e8c", theme.Accent);
            Assert.Equal("232, 62, 140", theme.AccentRgb);
        }

        [Theory]
        [InlineData("#111111")]  // negro por defecto
        [InlineData("#0d7a5f")]  // verde actual del sistema
        [InlineData("#e83e8c")]  // rosa medio
        [InlineData("#ffe4f2")]  // rosa muy claro
        [InlineData("#ffffff")]  // blanco extremo
        [InlineData("#000000")]  // negro extremo
        [InlineData("#f5d90a")]  // amarillo saturado
        public void Resolve_AnyAccent_KeepsTextReadableOnAccentAndOnHover(string hex)
        {
            var theme = PublicBrandTheme.Resolve(hex);

            Assert.True(
                Contrast(theme.OnAccent, theme.Accent) >= 4.5d,
                $"Texto {theme.OnAccent} sobre {theme.Accent} = {Contrast(theme.OnAccent, theme.Accent):0.00}:1");

            Assert.True(
                Contrast(theme.OnAccent, theme.AccentHover) >= 3.5d,
                $"Texto {theme.OnAccent} sobre hover {theme.AccentHover} = {Contrast(theme.OnAccent, theme.AccentHover):0.00}:1");
        }

        [Theory]
        [InlineData("#111111")]
        [InlineData("#e83e8c")]
        [InlineData("#ffe4f2")]
        [InlineData("#f5d90a")]
        public void Resolve_AnyAccent_InkIsReadableAsTextOnLightSurfaces(string hex)
        {
            var theme = PublicBrandTheme.Resolve(hex);

            Assert.True(
                Contrast(theme.AccentInk, "#ffffff") >= 4.5d,
                $"Tinta {theme.AccentInk} sobre blanco = {Contrast(theme.AccentInk, "#ffffff"):0.00}:1");
        }

        [Fact]
        public void Resolve_LightAccent_UsesDarkTextInsteadOfWhite()
        {
            var theme = PublicBrandTheme.Resolve("#ffe4f2");

            Assert.Equal("#111111", theme.OnAccent);
        }

        [Fact]
        public void Resolve_DarkAccent_HoverIsLighterSoTheChangeIsVisible()
        {
            var theme = PublicBrandTheme.Resolve("#111111");

            Assert.NotEqual(theme.Accent, theme.AccentHover);
            Assert.True(Luminance(theme.AccentHover) > Luminance(theme.Accent));
        }

        [Fact]
        public void Resolve_MidAccent_HoverIsDarker()
        {
            var theme = PublicBrandTheme.Resolve("#e83e8c");

            Assert.True(Luminance(theme.AccentHover) < Luminance(theme.Accent));
        }

        [Fact]
        public void Resolve_AccentSoft_IsALightTintUsableAsChipBackground()
        {
            var theme = PublicBrandTheme.Resolve("#0d7a5f");

            Assert.True(Luminance(theme.AccentSoft) > Luminance(theme.Accent));
            Assert.True(Contrast(theme.AccentInk, theme.AccentSoft) >= 4d);
        }

        private static double Contrast(string firstHex, string secondHex)
        {
            var first = Luminance(firstHex);
            var second = Luminance(secondHex);
            return (Math.Max(first, second) + 0.05d) / (Math.Min(first, second) + 0.05d);
        }

        private static double Luminance(string hex)
        {
            var red = Convert.ToInt32(hex.Substring(1, 2), 16);
            var green = Convert.ToInt32(hex.Substring(3, 2), 16);
            var blue = Convert.ToInt32(hex.Substring(5, 2), 16);

            return (0.2126d * Linearize(red)) + (0.7152d * Linearize(green)) + (0.0722d * Linearize(blue));
        }

        private static double Linearize(int channel)
        {
            var value = channel / 255d;
            return value <= 0.04045d ? value / 12.92d : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }
    }
}
