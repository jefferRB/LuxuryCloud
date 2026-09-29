using LuxuryApp.Models.Platform;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Platform
{
    public sealed class WorkerHeartbeatService : IWorkerHeartbeatService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<WorkerHeartbeatService> _logger;

        public WorkerHeartbeatService(
            IServiceScopeFactory scopeFactory,
            ILogger<WorkerHeartbeatService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task TryBeatAsync(
            string workerName,
            string? cycleSummary = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workerName))
            {
                return;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var nowUtc = DateTime.UtcNow;
                var summary = Truncate(cycleSummary, 300);

                var heartbeat = await context.PlatformWorkerHeartbeats
                    .FirstOrDefaultAsync(h => h.WorkerName == workerName, cancellationToken);

                if (heartbeat is null)
                {
                    context.PlatformWorkerHeartbeats.Add(new PlatformWorkerHeartbeat
                    {
                        WorkerName = workerName,
                        LastBeatUtc = nowUtc,
                        LastCycleSummary = summary
                    });
                }
                else
                {
                    heartbeat.LastBeatUtc = nowUtc;
                    heartbeat.LastCycleSummary = summary;
                }

                await context.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Apagado de la app: silencioso.
            }
            catch (Exception ex)
            {
                // El latido jamás debe tumbar al worker.
                _logger.LogWarning(ex, "No fue posible registrar el heartbeat del worker {WorkerName}.", workerName);
            }
        }

        public async Task<IReadOnlyList<PlatformWorkerHeartbeat>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await context.PlatformWorkerHeartbeats
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
