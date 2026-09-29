using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Platform;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Workers
{
    /// <summary>
    /// Ejecuta el pase de reconciliación de Billing en intervalo configurable (default diario).
    /// Es seguro tenerlo registrado siempre: con BillingReconciliation:Enabled=false queda inerte.
    /// Un pase fallido no tumba el worker; se registra y se reintenta en el siguiente intervalo.
    /// </summary>
    public sealed class BillingReconciliationWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptionsMonitor<BillingReconciliationOptions> _options;
        private readonly IWorkerHeartbeatService _heartbeatService;
        private readonly ILogger<BillingReconciliationWorker> _logger;

        public BillingReconciliationWorker(
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<BillingReconciliationOptions> options,
            IWorkerHeartbeatService heartbeatService,
            ILogger<BillingReconciliationWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _heartbeatService = heartbeatService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "BillingReconciliationWorker iniciado. Enabled {Enabled}. IntervalHours {IntervalHours}.",
                _options.CurrentValue.Enabled,
                _options.CurrentValue.IntervalHours);

            try
            {
                var initialDelay = TimeSpan.FromMinutes(
                    Math.Clamp(_options.CurrentValue.InitialDelayMinutes, 0, 120));
                await Task.Delay(initialDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

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
                    _logger.LogError(ex, "Error general en el pase de reconciliación de Billing.");
                }

                await _heartbeatService.TryBeatAsync(
                    PlatformWorkerNames.BillingReconciliation,
                    _options.CurrentValue.Enabled ? "pase ejecutado" : "disabled",
                    stoppingToken);

                try
                {
                    await Task.Delay(GetInterval(), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task RunPassAsync(CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var reconciliation = scope.ServiceProvider.GetRequiredService<IBillingReconciliationService>();
            await reconciliation.RunAsync(stoppingToken);
        }

        private TimeSpan GetInterval()
        {
            var hours = Math.Clamp(_options.CurrentValue.IntervalHours, 1, 168);
            return TimeSpan.FromHours(hours);
        }
    }
}
