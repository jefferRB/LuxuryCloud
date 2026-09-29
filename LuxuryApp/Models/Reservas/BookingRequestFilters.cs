namespace LuxuryApp.Models.Reservas
{
    /// <summary>
    /// Rango temporal del panel de solicitudes, expresado SIEMPRE en hora local del negocio.
    /// Es un conjunto cerrado: lo que llega por query string se valida contra él (allowlist).
    /// </summary>
    public enum BookingRequestDateRange
    {
        Today,
        ThisWeek,
        ThisMonth
    }

    /// <summary>Pestaña de estado del panel. <see cref="All"/> = todas las solicitudes del rango.</summary>
    public enum BookingRequestStatusFilter
    {
        Pending,
        Confirmed,
        Rejected,
        All
    }

    /// <summary>
    /// Límites UTC semiabiertos <c>[StartUtc, EndUtc)</c> listos para EF Core. Nunca se usa
    /// 23:59:59: el fin es exclusivo para no perder registros en el último segundo del día.
    /// </summary>
    public readonly record struct BookingRequestUtcRange(DateTime StartUtc, DateTime EndUtc);

    /// <summary>
    /// Traducción entre los tokens del query string (contrato público de la pantalla) y los
    /// enums del dominio. Un valor desconocido o manipulado cae al default seguro.
    /// </summary>
    public static class BookingRequestFilters
    {
        public const BookingRequestDateRange DefaultRange = BookingRequestDateRange.ThisWeek;
        public const BookingRequestStatusFilter DefaultStatus = BookingRequestStatusFilter.Pending;

        public static BookingRequestDateRange ParseRange(string? value) =>
            value?.Trim().ToLowerInvariant() switch
            {
                "hoy" or "today" => BookingRequestDateRange.Today,
                "semana" or "thisweek" => BookingRequestDateRange.ThisWeek,
                "mes" or "thismonth" => BookingRequestDateRange.ThisMonth,
                _ => DefaultRange
            };

        public static string ToToken(this BookingRequestDateRange range) => range switch
        {
            BookingRequestDateRange.Today => "hoy",
            BookingRequestDateRange.ThisMonth => "mes",
            _ => "semana"
        };

        public static BookingRequestStatusFilter ParseStatus(string? value) =>
            value?.Trim().ToLowerInvariant() switch
            {
                "confirmed" or "confirmadas" => BookingRequestStatusFilter.Confirmed,
                "rejected" or "rechazadas" => BookingRequestStatusFilter.Rejected,
                "all" or "todas" => BookingRequestStatusFilter.All,
                _ => DefaultStatus
            };

        public static string ToToken(this BookingRequestStatusFilter status) => status switch
        {
            BookingRequestStatusFilter.Confirmed => BookingRequestStates.Confirmed,
            BookingRequestStatusFilter.Rejected => BookingRequestStates.Rejected,
            BookingRequestStatusFilter.All => "all",
            _ => BookingRequestStates.Pending
        };

        /// <summary>Estado tal como se persiste en BD. Null = sin filtro de estado (todas).</summary>
        public static string? ToPersistedState(this BookingRequestStatusFilter status) => status switch
        {
            BookingRequestStatusFilter.Confirmed => BookingRequestStates.Confirmed,
            BookingRequestStatusFilter.Rejected => BookingRequestStates.Rejected,
            BookingRequestStatusFilter.Pending => BookingRequestStates.Pending,
            _ => null
        };
    }
}
