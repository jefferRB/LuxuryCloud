using LuxuryApp.Models.Platform;

namespace LuxuryApp.Services.Platform
{
    /// <summary>
    /// Captura y consulta del snapshot comercial mensual (AD-4). La captura es de solo
    /// lectura respecto a suscripciones, tenants y grants: únicamente escribe (upsert)
    /// en la tabla PlatformCommercialSnapshots.
    /// </summary>
    public interface IPlatformCommercialSnapshotService
    {
        /// <summary>
        /// Calcula y persiste (upsert por PeriodYear+PeriodMonth) el snapshot del período.
        /// Las métricas puntuales (MRR, salud, trials) son al momento de la captura; las de
        /// período (churn, tenants nuevos) se cuentan dentro del mes indicado.
        /// </summary>
        Task<PlatformCommercialSnapshot> CaptureAsync(
            int periodYear,
            int periodMonth,
            string triggerType,
            string? actorEmail,
            CancellationToken cancellationToken = default);

        /// <summary>Indica si el período ya tiene una captura con el trigger dado (idempotencia del worker).</summary>
        Task<bool> HasCaptureAsync(
            int periodYear,
            int periodMonth,
            string triggerType,
            CancellationToken cancellationToken = default);

        /// <summary>Historia para consulta (JSON); ordenada del período más reciente al más viejo.</summary>
        Task<IReadOnlyList<PlatformCommercialSnapshot>> GetHistoryAsync(
            int take = 24,
            CancellationToken cancellationToken = default);
    }
}
