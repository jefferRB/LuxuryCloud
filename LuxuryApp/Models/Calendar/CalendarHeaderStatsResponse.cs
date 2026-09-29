namespace LuxuryApp.Models.Calendar
{
    /// <summary>
    /// KPIs reales del encabezado del calendario para el tenant actual.
    /// CitasHoy: citas (Tipo == "CITA") cuya fecha cae en el día de hoy.
    /// PendientesConfirmar / Confirmadas: citas de hoy en adelante según su estado de confirmación WhatsApp.
    /// </summary>
    public sealed class CalendarHeaderStatsResponse
    {
        public int CitasHoy { get; init; }

        public int PendientesConfirmar { get; init; }

        public int Confirmadas { get; init; }
    }
}
