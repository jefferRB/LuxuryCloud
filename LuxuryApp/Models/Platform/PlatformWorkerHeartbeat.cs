namespace LuxuryApp.Models.Platform
{
    /// <summary>
    /// Último latido de cada worker de fondo. Tabla cross-tenant fuera del RLS
    /// (no implementa <c>ITenantEntity</c>, mismo criterio que <see cref="PlatformAuditLog"/>).
    /// Una fila por worker; el Mission Control compara LastBeatUtc contra la cadencia
    /// esperada para detectar procesos detenidos.
    /// </summary>
    public class PlatformWorkerHeartbeat
    {
        /// <summary>Nombre lógico del worker. Ver <see cref="PlatformWorkerNames"/>.</summary>
        public string WorkerName { get; set; } = string.Empty;

        public DateTime LastBeatUtc { get; set; }

        /// <summary>Resumen corto del último ciclo (ej. "ok", "disabled", "pass completado").</summary>
        public string? LastCycleSummary { get; set; }
    }

    /// <summary>Nombres canónicos de los workers monitoreados.</summary>
    public static class PlatformWorkerNames
    {
        public const string Reminder = "ReminderWorker";
        public const string Visitas = "VisitasBackgroundService";
        public const string BillingReconciliation = "BillingReconciliationWorker";
        public const string PlanChangeCancellationRetry = "PlanChangeCancellationRetryWorker";
        public const string SubscriptionLifecycleFinalization = "SubscriptionLifecycleWorker";
        public const string PaymentRecovery = "PaymentRecoveryWorker";
        public const string PendingTenantExpiration = "PendingTenantExpirationWorker";
        public const string MonthlyReportScheduler = "MonthlyReportSchedulerService";
        public const string CommercialSnapshot = "CommercialSnapshotWorker";
        public const string InvestorStatementGeneration = "InvestorStatementGenerationWorker";
    }
}
