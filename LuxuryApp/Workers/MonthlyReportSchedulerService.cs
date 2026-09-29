using LuxuryApp.Models.Platform;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Reports;
using LuxuryApp.Services.Tenant;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Workers
{
    /// <summary>
    /// Envío automático del Resumen Ejecutivo Mensual. Recorre los tenants activos y delega en
    /// <see cref="IMonthlyReportScheduler"/> la decisión de "¿toca enviar?". No hace nada mientras
    /// el flag global <c>MonthlyReports:SchedulerEnabled</c> esté en false (default), por lo que
    /// es seguro tenerlo registrado siempre.
    /// </summary>
    public sealed class MonthlyReportSchedulerService : BackgroundService
    {
        private readonly TenantExecutionService _tenantExecutionService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IOptionsMonitor<MonthlyReportSchedulerOptions> _options;
        private readonly IWorkerHeartbeatService _heartbeatService;
        private readonly ILogger<MonthlyReportSchedulerService> _logger;

        public MonthlyReportSchedulerService(
            TenantExecutionService tenantExecutionService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IOptionsMonitor<MonthlyReportSchedulerOptions> options,
            IWorkerHeartbeatService heartbeatService,
            ILogger<MonthlyReportSchedulerService> logger)
        {
            _tenantExecutionService = tenantExecutionService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _options = options;
            _heartbeatService = heartbeatService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("MonthlyReportSchedulerService iniciado.");

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
                    _logger.LogError(ex, "Error general en MonthlyReportSchedulerService.");
                }

                await _heartbeatService.TryBeatAsync(
                    PlatformWorkerNames.MonthlyReportScheduler,
                    _options.CurrentValue.SchedulerEnabled ? "ciclo completado" : "disabled",
                    stoppingToken);

                await Task.Delay(GetPollingInterval(), stoppingToken);
            }
        }

        private async Task RunPassAsync(CancellationToken stoppingToken)
        {
            var nowLocal = _businessDateTimeProvider.Now();

            await _tenantExecutionService.RunForEachActiveTenantAsync(
                async (serviceProvider, tenantId, cancellationToken) =>
                {
                    try
                    {
                        var scheduler = serviceProvider.GetRequiredService<IMonthlyReportScheduler>();
                        var outcome = await scheduler.ProcessTenantAsync(tenantId, nowLocal, cancellationToken);

                        if (outcome is MonthlyReportScheduleOutcome.Sent
                            or MonthlyReportScheduleOutcome.PartiallySent
                            or MonthlyReportScheduleOutcome.Failed)
                        {
                            _logger.LogInformation(
                                "Resumen mensual automático para TenantId {TenantId}: {Outcome}.",
                                tenantId,
                                outcome);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Un tenant que falle no debe frenar a los demás.
                        _logger.LogError(ex, "Error procesando el resumen mensual del tenant {TenantId}.", tenantId);
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
