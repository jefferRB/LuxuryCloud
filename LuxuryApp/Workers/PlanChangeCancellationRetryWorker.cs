using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Platform;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Workers
{
    /// <summary>
    /// Worker de ALTA frecuencia (default cada 20 min) que reintenta cancelar el suscriptor viejo
    /// de un cambio de plan cuando quedó pendiente. El riesgo de doble cobro recurrente no debe
    /// esperar al pase diario de reconciliación.
    /// DEPENDENCIA DELIBERADA: requiere BillingReconciliation:Enabled=true ADEMÁS de
    /// OldCancellationRetryEnabled=true — Enabled actúa como kill-switch maestro de todos los
    /// jobs automáticos de Billing. Un fallo no tumba el worker. Late en PlatformWorkerHeartbeats.
    ///
    /// El ritmo NO lo decide este worker: el backoff vive POR INTENT en el propio servicio
    /// (ver PlanChangeCancellationBackoff). Este intervalo es solo la frecuencia con la que se
    /// pregunta "¿hay algo elegible?", así que bajarlo no dispara más llamadas a TiloPay.
    /// </summary>
    public sealed class PlanChangeCancellationRetryWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptionsMonitor<BillingReconciliationOptions> _options;
        private readonly IWorkerHeartbeatService _heartbeatService;
        private readonly ILogger<PlanChangeCancellationRetryWorker> _logger;

        public PlanChangeCancellationRetryWorker(
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<BillingReconciliationOptions> options,
            IWorkerHeartbeatService heartbeatService,
            ILogger<PlanChangeCancellationRetryWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _heartbeatService = heartbeatService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "PlanChangeCancellationRetryWorker iniciado. Enabled {Enabled}. IntervalMinutes {IntervalMinutes}.",
                _options.CurrentValue.OldCancellationRetryEnabled,
                _options.CurrentValue.OldCancellationRetryMinutes);

            // Espera inicial breve para no competir con el arranque de la app.
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_options.CurrentValue.Enabled && _options.CurrentValue.OldCancellationRetryEnabled)
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var reconciliation = scope.ServiceProvider.GetRequiredService<IBillingReconciliationService>();
                        var report = await reconciliation.RunOldSubscriberCancellationRetryAsync(stoppingToken);

                        if (report.OldSubscriberCancellationsRetried > 0 ||
                            report.OldCancellationSkippedAutoCancelDisabled > 0 ||
                            report.OldCancellationSkippedNotEligible > 0 ||
                            report.PlanChangesRepaired > 0 ||
                            report.LatePlanChangesApplied > 0 ||
                            report.LatePlanChangesManualReview > 0)
                        {
                            // Los skips por backoff no se anuncian aquí (son el ritmo normal): el
                            // detalle por intent, con nextEligibleUtc, lo escribe el servicio.
                            _logger.LogInformation(
                                "Pase rápido de cambios de plan ejecutado. AplicadosTardios {LateApplied}. TardiosRevisionManual {LateManual}. IntentosReales {Retries}. Completados {Completed}. Reparados {Repaired}. SaltadosAutoCancelOff {SkippedAutoCancel}. SaltadosNoElegibles {SkippedNotEligible}. SaltadosBackoff {SkippedBackoff}. DurationMs {DurationMs}.",
                                report.LatePlanChangesApplied,
                                report.LatePlanChangesManualReview,
                                report.OldSubscriberCancellationsRetried,
                                report.OldSubscriberCancellationsCompleted,
                                report.PlanChangesRepaired,
                                report.OldCancellationSkippedAutoCancelDisabled,
                                report.OldCancellationSkippedNotEligible,
                                report.OldCancellationSkippedBackoff,
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
                    _logger.LogError(ex, "Error en el reintento rápido de cancelación de suscriptor viejo.");
                }

                await _heartbeatService.TryBeatAsync(
                    PlatformWorkerNames.PlanChangeCancellationRetry,
                    _options.CurrentValue.Enabled && _options.CurrentValue.OldCancellationRetryEnabled
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
            var minutes = Math.Clamp(_options.CurrentValue.OldCancellationRetryMinutes, 5, 720);
            return TimeSpan.FromMinutes(minutes);
        }
    }
}
