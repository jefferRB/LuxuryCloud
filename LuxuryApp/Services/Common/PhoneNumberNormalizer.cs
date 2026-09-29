namespace LuxuryApp.Services.Common
{
    /// <summary>
    /// Normalización de teléfonos del sistema. Es el MISMO algoritmo que ya usaba WhatsApp para
    /// construir el E.164 con el que se envían los mensajes: se extrajo acá (función pura, sin
    /// dependencias) para que la identificación de clientes y el envío de WhatsApp no puedan
    /// divergir. <c>MetaWhatsAppClient.NormalizePhoneNumber</c> delega en esta clase.
    /// </summary>
    public static class PhoneNumberNormalizer
    {
        /// <summary>Código de país por defecto cuando la configuración no aporta uno válido.</summary>
        public const string FallbackCountryCode = "506";

        /// <summary>
        /// Longitud mínima que debe conservar un número al quitarle el código de país para que la
        /// variante "local" se considere plausible. Evita romper números locales de 8 dígitos que
        /// casualmente empiezan por el código de país (p. ej. 5061xxxx en Costa Rica).
        /// </summary>
        private const int MinLocalDigits = 7;

        private const int MinE164Digits = 8;
        private const int MaxE164Digits = 15;

        public static string ResolveCountryCode(string? configuredCountryCode)
        {
            var digits = DigitsOnly(configuredCountryCode);
            return digits.Length == 0 ? FallbackCountryCode : digits;
        }

        /// <summary>
        /// E.164 (<c>+50688887777</c>) o <c>null</c> si el valor no puede representar un teléfono.
        /// Comportamiento idéntico al que tenía <c>MetaWhatsAppClient</c>.
        /// </summary>
        public static string? ToE164(string? phoneNumber, string? defaultCountryCode)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return null;
            }

            var trimmed = phoneNumber.Trim();
            var digits = DigitsOnly(trimmed);

            if (digits.StartsWith("00", StringComparison.Ordinal))
            {
                digits = digits[2..];
            }

            if (digits.Length == 0)
            {
                return null;
            }

            var countryCode = ResolveCountryCode(defaultCountryCode);

            if (!trimmed.StartsWith("+", StringComparison.Ordinal) &&
                !digits.StartsWith(countryCode, StringComparison.Ordinal))
            {
                digits = countryCode + digits;
            }

            if (digits.Length < MinE164Digits || digits.Length > MaxE164Digits)
            {
                return null;
            }

            return "+" + digits;
        }

        /// <summary>
        /// Claves de comparación para buscar el teléfono en la base. Se devuelven las dos formas en
        /// las que el sistema ha guardado históricamente un número (con y sin código de país),
        /// porque <c>Clientes.NumeroTelefono</c> es texto libre y nunca se normalizó al persistir.
        /// <c>null</c> cuando el valor no es un teléfono utilizable.
        /// </summary>
        public static PhoneMatchKeys? BuildMatchKeys(string? phoneNumber, string? defaultCountryCode)
        {
            var e164 = ToE164(phoneNumber, defaultCountryCode);
            if (e164 is null)
            {
                return null;
            }

            var full = e164[1..];
            var countryCode = ResolveCountryCode(defaultCountryCode);

            var local = full.StartsWith(countryCode, StringComparison.Ordinal) &&
                        full.Length - countryCode.Length >= MinLocalDigits
                ? full[countryCode.Length..]
                : full;

            return new PhoneMatchKeys(e164, full, local);
        }

        /// <summary>
        /// Quita los separadores con los que se escribe un teléfono. Es EXACTAMENTE el mismo juego
        /// de caracteres que el módulo de Clientes ya aplicaba en SQL (<c>REPLACE</c> encadenados),
        /// para que la comparación en base y en memoria den el mismo resultado.
        /// </summary>
        public static string StripSeparators(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value
                    .Replace(" ", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal)
                    .Replace("(", string.Empty, StringComparison.Ordinal)
                    .Replace(")", string.Empty, StringComparison.Ordinal)
                    .Replace("+", string.Empty, StringComparison.Ordinal)
                    .Replace(".", string.Empty, StringComparison.Ordinal)
                    .Replace("/", string.Empty, StringComparison.Ordinal);

        public static string DigitsOnly(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : new string(value.Where(char.IsDigit).ToArray());
    }

    /// <summary>
    /// Formas equivalentes de un mismo teléfono. <paramref name="Full"/> incluye el código de país
    /// y <paramref name="Local"/> no; ambas se comparan porque la base tiene las dos.
    /// </summary>
    public sealed record PhoneMatchKeys(string E164, string Full, string Local)
    {
        public IReadOnlyList<string> ComparisonKeys => Full == Local
            ? new[] { Full }
            : new[] { Full, Local };
    }
}
