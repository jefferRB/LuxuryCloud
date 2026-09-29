namespace LuxuryApp.Models.Calendar
{
    public class CalendarOccupiedDateResponse
    {
        public string Fecha { get; init; } = string.Empty;

        public string Hora { get; init; } = string.Empty;

        public int Duracion { get; init; }

        public int FuncionarioId { get; init; }
    }
}
