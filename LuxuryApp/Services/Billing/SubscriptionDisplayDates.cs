using System.Globalization;

namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Fecha VISIBLE de una suscripción (la que ve el cliente en Costa Rica), separada de la fecha
    /// de CÁLCULO (UTC, para acceso/morosidad).
    ///
    /// El problema que resuelve: internamente el expire "2026-09-15" de TiloPay se guarda como fin
    /// del día Tico en UTC (2026-09-16 05:59 UTC). Mostrar esa fecha UTC cruda como dd/MM/yyyy da
    /// "16/09", un día de más para el cliente. La regla:
    ///
    /// - Cuando la fecha efectiva viene del PROVEEDOR (su expire ganó el max), se muestra el
    ///   <c>ProviderExpiryRaw</c> ("2026-09-15") tal cual: es la fecha de calendario Tica exacta,
    ///   sin ninguna aritmética de zona horaria que la pueda correr un día.
    /// - Si por alguna razón no hay raw, se convierte el UTC del proveedor a fecha Tica.
    /// - Cuando gana la fecha LOCAL, se muestra tal como se mostraba antes (sin conversión), para
    ///   no alterar el comportamiento de suscripciones sin datos del proveedor.
    ///
    /// Costa Rica es UTC-6 fijo (sin horario de verano), igual que en <see cref="ProviderExpiryDate"/>.
    /// </summary>
    public static class SubscriptionDisplayDates
    {
        private static readonly TimeSpan CostaRicaUtcOffset = TimeSpan.FromHours(-6);

        /// <summary>
        /// Fecha de calendario Tica a mostrar para el fin de período / próximo cobro, dada la fecha
        /// local, el expire del proveedor (UTC) y su valor crudo. Null si no hay ninguna fecha.
        /// </summary>
        public static DateOnly? ResolveEffectiveDisplayDate(
            DateTime? localUtc,
            DateTime? providerExpiresAtUtc,
            string? providerExpiryRaw)
        {
            var effectiveUtc = SubscriptionEffectiveDates.GetEffectiveEndUtc(localUtc, providerExpiresAtUtc);
            if (effectiveUtc is null)
            {
                return null;
            }

            // El proveedor es la fuente de la fecha efectiva cuando su expire ganó (>= local).
            var providerIsSource = providerExpiresAtUtc.HasValue &&
                                   (!localUtc.HasValue || providerExpiresAtUtc.Value >= localUtc.Value);

            if (providerIsSource)
            {
                // La fecha CRUDA del proveedor es la fecha Tica exacta: preferirla evita el corrimiento.
                if (TryParseRawDate(providerExpiryRaw, out var rawDate))
                {
                    return rawDate;
                }

                return ToCostaRicaDate(providerExpiresAtUtc);
            }

            // Gana lo local: mismo comportamiento de siempre (la fecha tal como se guardó).
            return DateOnly.FromDateTime(effectiveUtc.Value);
        }

        /// <summary>Convierte un instante UTC a la fecha de calendario en Costa Rica.</summary>
        public static DateOnly? ToCostaRicaDate(DateTime? utc)
        {
            if (utc is null)
            {
                return null;
            }

            var asUtc = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
            var costaRica = TimeZoneInfo.ConvertTimeFromUtc(
                asUtc,
                TimeZoneInfo.CreateCustomTimeZone("CR", CostaRicaUtcOffset, "Costa Rica", "Costa Rica"));
            return DateOnly.FromDateTime(costaRica);
        }

        /// <summary>Formatea como dd/MM/yyyy, o null si no hay fecha.</summary>
        public static string? Format(DateOnly? date) =>
            date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        /// <summary>Atajo: resuelve la fecha visible y la formatea en un paso.</summary>
        public static string? FormatEffective(
            DateTime? localUtc,
            DateTime? providerExpiresAtUtc,
            string? providerExpiryRaw) =>
            Format(ResolveEffectiveDisplayDate(localUtc, providerExpiresAtUtc, providerExpiryRaw));

        private static bool TryParseRawDate(string? raw, out DateOnly date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            return DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
    }
}
