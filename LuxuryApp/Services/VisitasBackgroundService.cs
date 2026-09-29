using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Tenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LuxuryApp.Services
{
    public class VisitasBackgroundService : BackgroundService
    {
        private readonly TenantExecutionService _tenantExecutionService;
        private readonly IWorkerHeartbeatService _heartbeatService;

        public VisitasBackgroundService(
            TenantExecutionService tenantExecutionService,
            IWorkerHeartbeatService heartbeatService)
        {
            _tenantExecutionService = tenantExecutionService;
            _heartbeatService = heartbeatService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _tenantExecutionService.RunForEachActiveTenantAsync(
                    async (serviceProvider, _, cancellationToken) =>
                    {
                        var servicio = serviceProvider.GetRequiredService<VisitasAutomaticasService>();
                        await servicio.ProcesarCitasFinalizadas(cancellationToken);
                    },
                    stoppingToken);

                await _heartbeatService.TryBeatAsync(PlatformWorkerNames.Visitas, "ciclo completado", stoppingToken);

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
