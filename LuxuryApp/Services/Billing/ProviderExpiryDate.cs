using System.Globalization;

namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Convierte el campo <c>expire</c> de TiloPay (una fecha SIN hora, p.ej. "2026-09-15") a un
    /// instante UTC utilizable para decidir acceso y morosidad.
    ///
    /// Decisión documentada: TiloPay factura por día de calendario en Costa Rica, así que un
    /// expire de "2026-09-15" significa "cubierto durante TODO el 15 de septiembre, hora Tica".
    /// Se interpreta como FIN DEL DÍA en América/Costa Rica y se pasa a UTC. Costa Rica es UTC-6
    /// fijo (sin horario de verano), así que el fin del 15/09 Tica = 16/09 05:59:59.999 UTC.
    ///
    /// Tomar el inicio del día (o medianoche UTC) suspendería al cliente hasta 6 horas antes de
    /// tiempo; el sesgo es siempre hacia NO cortar acceso temprano. La UI puede seguir mostrando
    /// "15/09/2026": el instante interno es solo para comparar contra "ahora".
    /// </summary>
    public static class ProviderExpiryDate
    {
        /// <summary>Costa Rica: UTC-6 todo el año, sin DST. Fijar el offset evita depender de la tz del host.</summary>
        private static readonly TimeSpan CostaRicaUtcOffset = TimeSpan.FromHours(-6);

        /// <summary>
        /// Parsea el expire de TiloPay a UTC (fin del día Costa Rica). Acepta "yyyy-MM-dd" y
        /// variantes con hora; ante cualquier cosa no reconocible devuelve null (nunca lanza).
        /// </summary>
        public static DateTime? ParseCostaRicaEndOfDayUtc(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var trimmed = raw.Trim();

            // Caso principal del contrato: fecha pura sin hora.
            if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
            {
                return EndOfCostaRicaDayToUtc(dateOnly);
            }

            // Defensivo: si algún día empieza a mandar hora, respetarla como hora Tica.
            if (DateTime.TryParse(
                    trimmed,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var withTime))
            {
                // Si vino solo fecha (medianoche), tratarlo como fin del día; si trajo hora real, respetarla.
                if (withTime.TimeOfDay == TimeSpan.Zero)
                {
                    return EndOfCostaRicaDayToUtc(DateOnly.FromDateTime(withTime));
                }

                return new DateTimeOffset(withTime, CostaRicaUtcOffset).UtcDateTime;
            }

            return null;
        }

        /// <summary>Fin del día (23:59:59.999) de una fecha Tica, en UTC.</summary>
        public static DateTime EndOfCostaRicaDayToUtc(DateOnly costaRicaDate)
        {
            var endOfDay = costaRicaDate.ToDateTime(new TimeOnly(23, 59, 59, 999));
            return new DateTimeOffset(endOfDay, CostaRicaUtcOffset).UtcDateTime;
        }
    }
}
