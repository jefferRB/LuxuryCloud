using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Platform
{
    /// <summary>
    /// Fotografía mensual del estado comercial de la plataforma (AD-4). El MRR histórico no
    /// se puede reconstruir retroactivamente (planes y estados cambian): cada mes sin
    /// snapshot es historia perdida. Una fila por mes calendario (índice único por período);
    /// la captura es de solo lectura sobre los datos que consulta. Esta tabla nunca se purga:
    /// es el agregado que sobrevive a las políticas de retención (AD-5).
    /// </summary>
    public class PlatformCommercialSnapshot
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public int PeriodYear { get; set; }

        public int PeriodMonth { get; set; }

        public DateTime CapturedAtUtc { get; set; }

        /// <summary>Ver <see cref="PlatformCommercialSnapshotTriggers"/>.</summary>
        [MaxLength(20)]
        public string TriggerType { get; set; } = PlatformCommercialSnapshotTriggers.Scheduled;

        [MaxLength(256)]
        public string? TriggeredByEmail { get; set; }

        /// <summary>Suscripciones Activas normalizadas a mensual (anual ÷ 12, half-even). Morosa va aparte en DetailJson.</summary>
        public decimal MrrTotal { get; set; }

        public decimal ArrTotal { get; set; }

        public int ActiveSubscriptions { get; set; }

        public int MonthlyCycleCount { get; set; }

        public int AnnualCycleCount { get; set; }

        public int TenantsTotal { get; set; }

        public int TenantsSaludable { get; set; }

        public int TenantsAtencion { get; set; }

        public int TenantsRiesgo { get; set; }

        public int TenantsSinAcceso { get; set; }

        /// <summary>Grants comerciales vigentes (trials/cortesías) al momento de la captura.</summary>
        public int TrialsActivos { get; set; }

        public int TrialsPorVencer7d { get; set; }

        /// <summary>Tenants cuya suscripción pasó a estado terminal durante el mes del período.</summary>
        public int ChurnedTenants { get; set; }

        public decimal ChurnedMrr { get; set; }

        public int NewTenants { get; set; }

        /// <summary>Desglose por plan y por estado de suscripción (JSON). Da profundidad sin más columnas.</summary>
        public string? DetailJson { get; set; }
    }

    public static class PlatformCommercialSnapshotTriggers
    {
        public const string Scheduled = "Scheduled";
        public const string Manual = "Manual";
    }

    /// <summary>Sección "Platform:CommercialSnapshot" de appsettings.</summary>
    public sealed class PlatformCommercialSnapshotOptions
    {
        public const string SectionName = "Platform:CommercialSnapshot";

        public bool Enabled { get; set; }

        public int PollingIntervalMinutes { get; set; } = 60;

        /// <summary>Día del mes (hora de negocio) a partir del cual se captura el cierre del mes anterior.</summary>
        public int CaptureDayOfMonth { get; set; } = 1;
    }
}
