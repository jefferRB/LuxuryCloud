using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Platform;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Workers
{
    /// <summary>
    /// Worker LIVIANO de ciclo de vida: cierra localmente las cancelaciones vencidas
    /// (CancelAtPeriodEnd cuyo período pagado ya terminó → Estado Cancelada). Corre POCO después del
    /// arranque y luego cada <see cref="BillingReconciliationOptions.LifecycleFinalizationMinutes"/>,
    /// para que la BD no quede figurando Activa durante horas tras el vencimiento (el control de
    /// acceso ya lo bloquea por cálculo, pero el Estado local debe cerrarse sin esperar el pase diario).
    ///
    /// Solo local: NO llama a TiloPay (si el proveedor ya está Delete, finalizar el Estado no
    /// requiere HTTP). Idempotente. Gateado por Enabled (kill-switch maestro) + el flag propio.
    /// </summary>
    public sealed class SubscriptionLifecycleWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptionsMonitor<BillingReconciliationOptions> _options;
        private readonly IWorkerHeartbeatService _heartbeatService;
        private readonly ILogger<SubscriptionLifecycleWorker> _logger;

        public SubscriptionLifecycleWorker(
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<BillingReconciliationOptions> options,
            IWorkerHeartbeatService heartbeatService,
            ILogger<SubscriptionLifecycleWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _heartbeatService = heartbeatService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "SubscriptionLifecycleWorker iniciado. Enabled {Enabled}. IntervalMinutes {IntervalMinutes}.",
                _options.CurrentValue.LifecycleFinalizationWorkerEnabled,
                _options.CurrentValue.LifecycleFinalizationMinutes);

            // Espera inicial CORTA (a diferencia del pase diario): finalizar pronto tras un reinicio.
            try
            {
                await Task.Delay(GetInitialDelay(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_options.CurrentValue.Enabled && _options.CurrentValue.LifecycleFinalizationWorkerEnabled)
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var reconciliation = scope.ServiceProvider.GetRequiredService<IBillingReconciliationService>();
                        var report = await reconciliation.RunLifecycleFinalizationAsync(stoppingToken);

                        if (report.CancelAtPeriodEndFinalized > 0)
                        {
                            _logger.LogInformation(
                                "Cierre local de cancelaciones vencidas. Finalizadas {Finalized}. DurationMs {DurationMs}.",
                                report.CancelAtPeriodEndFinalized,
                                report.DurationMs);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error en el cierre local de cancelaciones vencidas.");
                }

                await _heartbeatService.TryBeatAsync(
                    PlatformWorkerNames.SubscriptionLifecycleFinalization,
                    _options.CurrentValue.Enabled && _options.CurrentValue.LifecycleFinalizationWorkerEnabled
                        ? "pase ejecutado"
                        : "disabled",
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

        private TimeSpan GetInterval()
        {
            var minutes = Math.Clamp(_options.CurrentValue.LifecycleFinalizationMinutes, 5, 120);
            return TimeSpan.FromMinutes(minutes);
        }

        private TimeSpan GetInitialDelay()
        {
            var minutes = Math.Clamp(_options.CurrentValue.LifecycleFinalizationInitialDelayMinutes, 0, 10);
            return TimeSpan.FromMinutes(minutes);
        }
    }
}
