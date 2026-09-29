namespace LuxuryApp.Models.Calendar
{
    public class CalendarUpcomingAppointmentResponse
    {
        public int Id { get; init; }

        public string? NombreCliente { get; init; }

        public string? TelefonoCliente { get; init; }

        public DateTime FechaHoraCita { get; init; }

        public string ServicioNombre { get; init; } = string.Empty;

        public string FuncionarioNombre { get; init; } = string.Empty;

        public string EstadoConfirmacionWhatsApp { get; init; } = string.Empty;

        public DateTime? ConfirmacionWhatsAppEnviadaUtc { get; init; }

        public DateTime? RecordatorioWhatsAppTresHorasEnviadoUtc { get; init; }

        public string WhatsAppStatusDisplay { get; init; } = string.Empty;
    }
}
