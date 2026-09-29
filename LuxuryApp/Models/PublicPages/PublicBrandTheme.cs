using System.Globalization;
using System.Text.RegularExpressions;

namespace LuxuryApp.Models.PublicPages
{
    /// <summary>
    /// Tokens de color de la pagina publica derivados de UN solo color de marca.
    /// El negocio elige el acento; el contraste del texto, el hover y el tinte claro se calculan
    /// aca (funcion pura, sin estado ni dependencias) para que nadie tenga que elegirlos a mano
    /// y para que el resultado sea testeable.
    /// </summary>
    public sealed record PublicBrandTheme
    {
        /// <summary>Negro de marca por defecto. Un tenant sin color configurado usa este valor.</summary>
        public const string DefaultAccentHex = "#111111";

        private static readonly Regex HexPattern = new(
            "^#[0-9A-Fa-f]{6}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private PublicBrandTheme(
            string accent,
            string accentHover,
            string accentInk,
            string accentSoft,
            string onAccent,
            string accentRgb,
            bool isCustom)
        {
            Accent = accent;
            AccentHover = accentHover;
            AccentInk = accentInk;
            AccentSoft = accentSoft;
            OnAccent = onAccent;
            AccentRgb = accentRgb;
            IsCustom = isCustom;
        }

        /// <summary>Color principal (#RRGGBB).</summary>
        public string Accent { get; }

        /// <summary>Variante para hover de superficies con el acento.</summary>
        public string AccentHover { get; }

        /// <summary>Variante legible como TEXTO sobre fondos claros (contraste >= 4.5:1 con blanco).</summary>
        public string AccentInk { get; }

        /// <summary>Tinte claro del acento, para chips y fondos suaves.</summary>
        public string AccentSoft { get; }

        /// <summary>Blanco o negro, el que mejor contraste da sobre <see cref="Accent"/>.</summary>
        public string OnAccent { get; }

        /// <summary>Componentes "r, g, b" para componer rgba() en CSS.</summary>
        public string AccentRgb { get; }

        /// <summary>False cuando el tenant no configuro color y se esta usando el default.</summary>
        public bool IsCustom { get; }

        private const string White = "#ffffff";
        private const string Black = "#111111";

        /// <summary>
        /// Normaliza un HEX de entrada. Devuelve null si esta vacio o no cumple #RRGGBB, para que
        /// nunca llegue a la hoja de estilos un valor arbitrario de base de datos.
        /// </summary>
        public static string? Normalize(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return null;
            }

            var candidate = hex.Trim();
            if (!candidate.StartsWith('#'))
            {
                candidate = "#" + candidate;
            }

            return HexPattern.IsMatch(candidate) ? candidate.ToLowerInvariant() : null;
        }

        public static bool IsValid(string? hex) => Normalize(hex) is not null;

        /// <summary>
        /// Construye los tokens. Null/vacio/invalido cae al negro por defecto, de modo que un
        /// tenant que nunca configuro color se ve exactamente igual que antes.
        /// </summary>
        public static PublicBrandTheme Resolve(string? hex)
        {
            var normalized = Normalize(hex);
            var accent = normalized ?? DefaultAccentHex;
            var (red, green, blue) = ToRgb(accent);
            var luminance = RelativeLuminance(red, green, blue);

            var onAccent = Contrast(luminance, RelativeLuminance(255, 255, 255)) >=
                           Contrast(luminance, RelativeLuminance(17, 17, 17))
                ? White
                : Black;

            // Un color casi negro no puede oscurecerse mas: ahi el hover aclara.
            var hover = luminance < 0.08d
                ? Lighten(red, green, blue, 0.10d)
                : Darken(red, green, blue, 0.12d);

            return new PublicBrandTheme(
                accent,
                hover,
                ResolveInk(red, green, blue),
                MixWithWhite(red, green, blue, 0.12d),
                onAccent,
                $"{red}, {green}, {blue}",
                normalized is not null);
        }

        /// <summary>
        /// Oscurece el acento hasta que sea legible como texto sobre blanco. Un rosa muy claro
        /// no sirve para leer un precio o un enlace; esta variante si.
        /// </summary>
        private static string ResolveInk(int red, int green, int blue)
        {
            var whiteLuminance = RelativeLuminance(255, 255, 255);
            double r = red, g = green, b = blue;

            for (var step = 0; step < 24; step++)
            {
                if (Contrast(RelativeLuminance((int)r, (int)g, (int)b), whiteLuminance) >= 4.5d)
                {
                    break;
                }

                r *= 0.88d;
                g *= 0.88d;
                b *= 0.88d;
            }

            return ToHex((int)Math.Round(r), (int)Math.Round(g), (int)Math.Round(b));
        }

        private static string Darken(int red, int green, int blue, double amount) =>
            ToHex(
                (int)Math.Round(red * (1 - amount)),
                (int)Math.Round(green * (1 - amount)),
                (int)Math.Round(blue * (1 - amount)));

        private static string Lighten(int red, int green, int blue, double amount) =>
            ToHex(
                (int)Math.Round(red + ((255 - red) * amount)),
                (int)Math.Round(green + ((255 - green) * amount)),
                (int)Math.Round(blue + ((255 - blue) * amount)));

        private static string MixWithWhite(int red, int green, int blue, double accentWeight) =>
            ToHex(
                (int)Math.Round((red * accentWeight) + (255 * (1 - accentWeight))),
                (int)Math.Round((green * accentWeight) + (255 * (1 - accentWeight))),
                (int)Math.Round((blue * accentWeight) + (255 * (1 - accentWeight))));

        private static double Contrast(double firstLuminance, double secondLuminance)
        {
            var lighter = Math.Max(firstLuminance, secondLuminance);
            var darker = Math.Min(firstLuminance, secondLuminance);
            return (lighter + 0.05d) / (darker + 0.05d);
        }

        /// <summary>Luminancia relativa WCAG 2.1.</summary>
        private static double RelativeLuminance(int red, int green, int blue) =>
            (0.2126d * Linearize(red)) +
            (0.7152d * Linearize(green)) +
            (0.0722d * Linearize(blue));

        private static double Linearize(int channel)
        {
            var value = Math.Clamp(channel, 0, 255) / 255d;
            return value <= 0.04045d
                ? value / 12.92d
                : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }

        private static (int Red, int Green, int Blue) ToRgb(string hex) =>
            (int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
             int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
             int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        private static string ToHex(int red, int green, int blue) =>
            string.Format(
                CultureInfo.InvariantCulture,
                "#{0:x2}{1:x2}{2:x2}",
                Math.Clamp(red, 0, 255),
                Math.Clamp(green, 0, 255),
                Math.Clamp(blue, 0, 255));
    }
}
