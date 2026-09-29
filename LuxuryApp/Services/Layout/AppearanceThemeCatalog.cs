using System.Text.Json;
using System.Text.Json.Serialization;
using LuxuryApp.Models.Layout;

namespace LuxuryApp.Services.Layout
{
    /// <summary>
    /// Fuente unica de los temas visuales del espacio privado. Antes el catalogo estaba duplicado
    /// en cuatro lugares (script anti-parpadeo de _Layout, el mismo script de _FuncionarioLayout,
    /// el registro de site.js y las tarjetas del modal), asi que agregar un tema obligaba a tocar
    /// los cuatro y era facil desincronizarlos. Ahora todos leen de aca.
    ///
    /// Agregar un tema nuevo = una entrada en <see cref="All"/> + su bloque de tokens CSS
    /// (fondo "theme-bg-{Background}" y superficie "surface-{Surface}") en private-layout.css.
    /// No hace falta tocar JavaScript ni las vistas.
    /// </summary>
    public static class AppearanceThemeCatalog
    {
        public const string DefaultThemeId = "classic-marble";

        private static readonly JsonSerializerOptions PresetJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static IReadOnlyList<AppearanceTheme> All { get; } = new[]
        {
            new AppearanceTheme(
                Id: DefaultThemeId,
                Label: "Clasico marmol",
                Description: "Fondo marmol blanco con cards claras y estilo operativo tradicional.",
                Background: "marble",
                Surface: "classic"),
            new AppearanceTheme(
                Id: "futuristic-premium",
                Label: "Futurista premium",
                Description: "Fondo azul/morado con paneles glass, tablas oscuras y estilo SaaS moderno.",
                Background: "futuristic",
                Surface: "glass"),
            new AppearanceTheme(
                Id: "absolute-black",
                Label: "Negro absoluto",
                Description: "Fondo completamente negro, paneles oscuros y maximo contraste para trabajo nocturno.",
                Background: "black",
                Surface: "dark"),
            new AppearanceTheme(
                Id: "clean-white",
                Label: "Blanco limpio",
                Description: "Fondo completamente blanco, paneles claros y una experiencia limpia para espacios iluminados.",
                Background: "white",
                Surface: "light"),
            new AppearanceTheme(
                Id: "pink-elegant",
                Label: "Rosa elegante",
                Description: "Rosa sofisticado con superficies claras, contrastes suaves y una estetica premium.",
                Background: "pink",
                Surface: "rose")
        };

        public static AppearanceTheme Default { get; } = All[0];

        public static AppearanceTheme Resolve(string? id) =>
            All.FirstOrDefault(theme =>
                string.Equals(theme.Id, id, StringComparison.Ordinal)) ?? Default;

        public static bool TryResolve(string? id, out AppearanceTheme theme)
        {
            theme = All.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal)) ?? Default;

            return All.Any(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        }

        /// <summary>
        /// Mapa {id: {label, background, surface}} que consumen el script anti-parpadeo y site.js.
        /// Todos los valores son constantes de compilacion: no hay entrada de usuario aca.
        /// </summary>
        public static string PresetsJson { get; } = JsonSerializer.Serialize(
            All.ToDictionary(
                theme => theme.Id,
                theme => new AppearanceThemePreset(theme.Label, theme.Background, theme.Surface),
                StringComparer.Ordinal),
            PresetJsonOptions);

        private sealed record AppearanceThemePreset(string Label, string Background, string Surface);
    }
}
