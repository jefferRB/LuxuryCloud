using LuxuryApp.Models.Platform;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Tenant;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Workers
{
    /// <summary>
    /// Cierre automático de estados de cuenta de inversionistas. Recorre los tenants activos y
    /// delega en <see cref="IInvestorStatementScheduler"/> la decisión de qué períodos cerrar.
    ///
    /// <para>
    /// No hace nada mientras el flag global <c>InvestorStatements:SchedulerEnabled</c> esté en
    /// false (default), así que es seguro tenerlo registrado siempre. Cada tenant además tiene que
    /// haber activado la generación automática en su política.
    /// </para>
    /// </summary>
    public sealed class InvestorStatementGenerationWorker : BackgroundService
    {
        private readonly TenantExecutionService _tenantExecutionService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IOptionsMonitor<InvestorStatementSchedulerOptions> _options;
        private readonly IWorkerHeartbeatService _heartbeatService;
        private readonly ILogger<InvestorStatementGenerationWorker> _logger;

        public InvestorStatementGenerationWorker(
            TenantExecutionService tenantExecutionService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IOptionsMonitor<InvestorStatementSchedulerOptions> options,
            IWorkerHeartbeatService heartbeatService,
            ILogger<InvestorStatementGenerationWorker> logger)
        {
            _tenantExecutionService = tenantExecutionService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _options = options;
            _heartbeatService = heartbeatService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("InvestorStatementGenerationWorker iniciado.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_options.CurrentValue.SchedulerEnabled)
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
                    _logger.LogError(ex, "Error general en InvestorStatementGenerationWorker.");
                }

                await _heartbeatService.TryBeatAsync(
                    PlatformWorkerNames.InvestorStatementGeneration,
                    _options.CurrentValue.SchedulerEnabled ? "ciclo completado" : "disabled",
                    stoppingToken);

                await Task.Delay(GetPollingInterval(), stoppingToken);
            }
        }

        private async Task RunPassAsync(CancellationToken stoppingToken)
        {
            // Hora LOCAL del negocio: el cierre de un período se mide en su calendario, no en UTC.
            var nowLocal = _businessDateTimeProvider.Now();

            await _tenantExecutionService.RunForEachActiveTenantAsync(
                async (serviceProvider, tenantId, cancellationToken) =>
                {
                    try
                    {
                        var scheduler = serviceProvider.GetRequiredService<IInvestorStatementScheduler>();
                        var resultado = await scheduler.ProcessTenantAsync(tenantId, nowLocal, cancellationToken);

                        if (resultado.Generados > 0 || resultado.Fallidos > 0 || resultado.EnviosFallidos > 0)
                        {
                            _logger.LogInformation(
                                "Cierre automático de inversionistas del tenant {TenantId}: {Outcome}. " +
                                "Generados {Generados}, enviados {Enviados}, envíos fallidos {EnviosFallidos}, errores {Fallidos}.",
                                tenantId,
                                resultado.Outcome,
                                resultado.Generados,
                                resultado.Enviados,
                                resultado.EnviosFallidos,
                                resultado.Fallidos);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Un tenant que falle no debe frenar a los demás.
                        _logger.LogError(
                            ex,
                            "Error cerrando estados de cuenta de inversionistas del tenant {TenantId}.",
                            tenantId);
                    }
                },
                stoppingToken);
        }

        private TimeSpan GetPollingInterval()
        {
            var minutes = Math.Clamp(_options.CurrentValue.PollingIntervalMinutes, 1, 720);
            return TimeSpan.FromMinutes(minutes);
        }
    }
}
