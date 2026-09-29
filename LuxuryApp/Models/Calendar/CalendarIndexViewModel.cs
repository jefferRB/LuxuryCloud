namespace LuxuryApp.Models.Calendar
{
    public sealed class CalendarIndexViewModel
    {
        public bool HasWhatsAppAddon { get; init; }

        public bool TenantWhatsAppEnabled { get; init; }

        public CalendarHeaderStatsResponse Stats { get; init; } = new();

        // Fecha "hoy" del negocio (America/Costa_Rica) en formato yyyy-MM-dd.
        // El JS la usa para decidir cuándo mostrar el botón "Hoy" en la vista de día.
        public string BusinessTodayIso { get; init; } = string.Empty;

        /// <summary>
        /// True si el usuario puede confirmar/rechazar solicitudes de reserva online. Controla
        /// sólo la UI: los endpoints exigen el permiso por su cuenta.
        /// </summary>
        public bool PuedeGestionarReservas { get; init; }
    }
}
