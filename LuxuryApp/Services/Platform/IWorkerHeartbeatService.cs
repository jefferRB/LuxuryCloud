using LuxuryApp.Models.Platform;

namespace LuxuryApp.Services.Platform
{
    /// <summary>
    /// Registro de latidos de los workers de fondo. Singleton: crea su propio scope de EF
    /// por latido para poder inyectarse en hosted services sin capturar DbContext.
    /// </summary>
    public interface IWorkerHeartbeatService
    {
        /// <summary>
        /// Registra el latido del worker. Nunca lanza: un fallo al escribir el latido
        /// no debe afectar el trabajo real del worker.
        /// </summary>
        Task TryBeatAsync(string workerName, string? cycleSummary = null, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PlatformWorkerHeartbeat>> GetAllAsync(CancellationToken cancellationToken = default);
    }
}
