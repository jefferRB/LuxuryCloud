namespace LuxuryApp.Models.DataBase
{
    public sealed record ClienteSummaryViewModel
    {
        public int Id { get; init; }
        public string Nombre { get; init; } = string.Empty;
        public string NumeroTelefono { get; init; } = string.Empty;
        public string? CorreoElectronico { get; init; }
        public DateTime? FechaCumpleanos { get; init; }

        /// <summary>
        /// Frecuencia que el sistema usa hoy para este cliente: promedio observado del historial
        /// de citas o, si aún no hay dos días de visita distintos, su frecuencia inicial. Sale
        /// de <c>IClienteVisitMetricsService</c>, igual que en el perfil, para que listado y
        /// detalle no puedan mostrar números distintos.
        /// </summary>
        public int FrecuenciaEfectivaDias { get; init; } = ClienteDefaults.InitialVisitFrequencyDays;

        /// <summary>
        /// Fecha de la última cita ya ocurrida. <c>null</c> si el cliente nunca fue atendido.
        /// No la mueve un cobro.
        /// </summary>
        public DateTime? UltimaVisita { get; init; }
    }
}
