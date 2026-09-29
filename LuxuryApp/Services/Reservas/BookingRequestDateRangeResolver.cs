using LuxuryApp.Models.Reservas;

namespace LuxuryApp.Services.Reservas
{
    /// <summary>
    /// Aritmética de tiempo del panel de solicitudes. Función pura: recibe el "ahora" del negocio
    /// (con su offset) y devuelve los límites UTC de la consulta.
    ///
    /// <para>
    /// Regla que no se negocia: "hoy", "esta semana" y "este mes" son conceptos de la HORA LOCAL
    /// del negocio. Se resuelven en local y solo al final se convierten a UTC, que es como se
    /// persiste <see cref="BookingRequest.CreatedAtUtc"/>. Calcularlos directamente en UTC
    /// desplazaría el corte del día 6 horas (Costa Rica).
    /// </para>
    /// </summary>
    public static class BookingRequestDateRangeResolver
    {
        /// <summary>Límites <c>[StartUtc, EndUtc)</c> del rango, resueltos en hora local del negocio.</summary>
        public static BookingRequestUtcRange Resolve(BookingRequestDateRange range, DateTimeOffset businessNow)
        {
            var hoyLocal = businessNow.DateTime.Date;

            var inicioLocal = range switch
            {
                BookingRequestDateRange.Today => hoyLocal,
                BookingRequestDateRange.ThisMonth => new DateTime(hoyLocal.Year, hoyLocal.Month, 1),
                _ => StartOfWeek(hoyLocal)
            };

            var finLocal = range switch
            {
                BookingRequestDateRange.Today => inicioLocal.AddDays(1),
                BookingRequestDateRange.ThisMonth => inicioLocal.AddMonths(1),
                _ => inicioLocal.AddDays(7)
            };

            var offset = businessNow.Offset;
            return new BookingRequestUtcRange(ToUtc(inicioLocal, offset), ToUtc(finLocal, offset));
        }

        /// <summary>
        /// UTC persistido → hora local del negocio, para mostrarlo ("Recibida 26 ago, 10:18").
        /// No se resta un offset fijo: el offset lo provee el reloj del negocio.
        /// </summary>
        public static DateTime ToBusinessLocal(DateTime utc, TimeSpan businessOffset) =>
            DateTime.SpecifyKind(utc + businessOffset, DateTimeKind.Unspecified);

        /// <summary>La semana del negocio arranca el lunes.</summary>
        private static DateTime StartOfWeek(DateTime fechaLocal)
        {
            var diasDesdeLunes = ((int)fechaLocal.DayOfWeek + 6) % 7;
            return fechaLocal.AddDays(-diasDesdeLunes);
        }

        private static DateTime ToUtc(DateTime local, TimeSpan businessOffset) =>
            DateTime.SpecifyKind(local - businessOffset, DateTimeKind.Utc);
    }
}
