namespace LuxuryApp.Models.Calendar
{
    public class CalendarServiceOptionResponse
    {
        public int Id { get; init; }

        public string Nombre { get; init; } = string.Empty;

        public int DuracionMinutos { get; init; }

        /// <summary>Precio de catálogo del servicio (para autollenar el monto en cobros). 0 si no tiene.</summary>
        public decimal Precio { get; init; }
    }
}
