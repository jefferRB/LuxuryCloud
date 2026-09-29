namespace LuxuryApp.Services.Reports
{
    /// <summary>Resultado de evaluar un tenant en el scheduler (para logging y pruebas).</summary>
    public enum MonthlyReportScheduleOutcome
    {
        /// <summary>El interruptor global MonthlyReports:SchedulerEnabled está apagado.</summary>
        SchedulerDisabled,

        /// <summary>El tenant no tiene el envío mensual activado.</summary>
        NotEnabled,

        /// <summary>Todavía no corresponde el día/hora configurados.</summary>
        NotDue,

        /// <summary>El periodo (mes anterior) ya fue procesado automáticamente.</summary>
        AlreadyProcessed,

        /// <summary>El tenant no tiene acceso comercial vigente; se reintenta luego.</summary>
        NoAccess,

        /// <summary>Se envió a todos los destinatarios.</summary>
        Sent,

        /// <summary>Se envió a algunos; quedan fallidos para reintento.</summary>
        PartiallySent,

        /// <summary>No había nada nuevo que enviar (ya estaba enviado).</summary>
        Skipped,

        /// <summary>No se pudo enviar (sin destinatarios o error del proveedor); se reintenta.</summary>
        Failed
    }

    /// <summary>
    /// Evalúa y ejecuta el envío automático del resumen mensual para un tenant. Se separa del
    /// <c>BackgroundService</c> para poder probar la lógica de "¿toca enviar?" sin un host.
    /// </summary>
    public interface IMonthlyReportScheduler
    {
        /// <param name="nowLocal">Fecha/hora local del negocio (America/Costa_Rica).</param>
        Task<MonthlyReportScheduleOutcome> ProcessTenantAsync(
            Guid tenantId,
            DateTime nowLocal,
            CancellationToken cancellationToken = default);
    }
}
