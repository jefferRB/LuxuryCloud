namespace LuxuryApp.Services.Reports
{
    /// <summary>
    /// Configuración global del envío automático del resumen mensual (sección "MonthlyReports").
    /// <para>
    /// <see cref="SchedulerEnabled"/> es el interruptor maestro: mientras esté en <c>false</c>
    /// (default) el background service no envía NADA, sin importar la configuración por tenant.
    /// Esto evita envíos masivos accidentales. Para activarlo en producción, poner
    /// <c>"MonthlyReports:SchedulerEnabled": true</c> en appsettings.Production.json.
    /// </para>
    /// </summary>
    public sealed class MonthlyReportSchedulerOptions
    {
        public const string SectionName = "MonthlyReports";

        /// <summary>Interruptor maestro del envío automático. Default false (seguro).</summary>
        public bool SchedulerEnabled { get; set; }

        /// <summary>Cada cuántos minutos revisa el scheduler si toca enviar. Default 15.</summary>
        public int PollingIntervalMinutes { get; set; } = 15;
    }
}
