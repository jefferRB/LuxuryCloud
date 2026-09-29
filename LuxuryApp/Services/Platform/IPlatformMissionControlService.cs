using LuxuryApp.Models.Platform.MissionControl;

namespace LuxuryApp.Services.Platform
{
    /// <summary>
    /// Fotografía operativa del Mission Control (señales + colas + pulso).
    /// Solo lectura, cross-tenant, exclusiva de la consola de plataforma.
    /// </summary>
    public interface IPlatformMissionControlService
    {
        /// <summary>
        /// Devuelve el snapshot cacheado (~45 s). Con <paramref name="forceRefresh"/> se recalcula.
        /// Cada señal se computa aislada: una señal que falle se reporta como Unknown
        /// sin tumbar el snapshot completo.
        /// </summary>
        Task<MissionControlSnapshotViewModel> GetSnapshotAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default);
    }
}
