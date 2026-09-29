using LuxuryApp.Models.Platform;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Platform;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Workers
{
    /// <summary>
    /// Captura automática del snapshot comercial mensual (AD-4): a partir del día configurado
    /// (hora de negocio) persiste el cierre del mes anterior si aún no existe una captura
    /// Scheduled de ese período. Inerte con Platform:CommercialSnapshot:Enabled=false; una
    /// captura manual previa del mismo mes no lo detiene (el cierre programado la supersede).
    /// </summary>
    public sealed class CommercialSnapshotWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IOptionsMonitor<PlatformCommercialSnapshotOptions> _options;
        private readonly IWorkerHeartbeatService _heartbeatService;
        private readonly ILogger<CommercialSnapshotWorker> _logger;

        public CommercialSnapshotWorker(
            IServiceScopeFactory scopeFactory,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IOptionsMonitor<PlatformCommercialSnapshotOptions> options,
            IWorkerHeartbeatService heartbeatService,
            ILogger<CommercialSnapshotWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _businessDateTimeProvider = businessDateTimeProvider;
            _options = options;
            _heartbeatService = heartbeatService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "CommercialSnapshotWorker iniciado. Enabled {Enabled}.",
                _options.CurrentValue.Enabled);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_options.CurrentValue.Enabled)
                    {
                        await RunPassAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error general en CommercialSnapshotWorker.");
                }

                await _heartbeatService.TryBeatAsync(
                    PlatformWorkerNames.CommercialSnapshot,
                    _options.CurrentValue.Enabled ? "ciclo completado" : "disabled",
                    stoppingToken);

                try
                {
                    await Task.Delay(GetPollingInterval(), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task RunPassAsync(CancellationToken stoppingToken)
        {
            var nowLocal = _businessDateTimeProvider.Now();
            var captureDay = Math.Clamp(_options.CurrentValue.CaptureDayOfMonth, 1, 28);

            // El "día >= X" (no "==") permite recuperarse si la app estuvo caída el día 1.
            if (nowLocal.Day < captureDay)
            {
                return;
            }

            var previousMonth = new DateTime(nowLocal.Year, nowLocal.Month, 1).AddMonths(-1);

            using var scope = _scopeFactory.CreateScope();
            var snapshotService = scope.ServiceProvider.GetRequiredService<IPlatformCommercialSnapshotService>();

            if (await snapshotService.HasCaptureAsync(
                previousMonth.Year,
                previousMonth.Month,
                PlatformCommercialSnapshotTriggers.Scheduled,
                stoppingToken))
            {
                return;
            }

            var snapshot = await snapshotService.CaptureAsync(
                previousMonth.Year,
                previousMonth.Month,
                PlatformCommercialSnapshotTriggers.Scheduled,
                actorEmail: null,
                stoppingToken);

            _logger.LogInformation(
                "Cierre comercial mensual {Year}-{Month:00} capturado automáticamente. MRR {Mrr}.",
                snapshot.PeriodYear,
                snapshot.PeriodMonth,
                snapshot.MrrTotal);
        }

        private TimeSpan GetPollingInterval()
        {
            var minutes = Math.Clamp(_options.CurrentValue.PollingIntervalMinutes, 5, 720);
            return TimeSpan.FromMinutes(minutes);
        }
    }
}
