using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Workers
{
    /// <summary>
    /// Worker inerte por defecto: solo soft-expira registros PendingVerification cuando
    /// RegistrationSecurity:ExpirePendingTenantsEnabled=true.
    /// </summary>
    public sealed class PendingTenantExpirationWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptionsMonitor<RegistrationSecurityOptions> _options;
        private readonly IWorkerHeartbeatService _heartbeatService;
        private readonly ILogger<PendingTenantExpirationWorker> _logger;

        public PendingTenantExpirationWorker(
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<RegistrationSecurityOptions> options,
            IWorkerHeartbeatService heartbeatService,
            ILogger<PendingTenantExpirationWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _heartbeatService = heartbeatService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "PendingTenantExpirationWorker iniciado. Enabled {Enabled}. ExpirationDays {ExpirationDays}. IntervalHours {IntervalHours}.",
                _options.CurrentValue.ExpirePendingTenantsEnabled,
                _options.CurrentValue.PendingTenantExpirationDays,
                _options.CurrentValue.PendingTenantExpirationWorkerIntervalHours);

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
                var summary = "disabled";

                try
                {
                    if (_options.CurrentValue.ExpirePendingTenantsEnabled)
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<IPendingTenantExpirationService>();
                        var result = await service.ExpirePendingTenantsAsync(stoppingToken);
                        summary = $"expired={result.ExpiredCount}";
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    summary = "error";
                    _logger.LogError(ex, "Error en expiracion de registros pendientes.");
                }

                await _heartbeatService.TryBeatAsync(
                    PlatformWorkerNames.PendingTenantExpiration,
                    summary,
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

        private TimeSpan GetInitialDelay()
        {
            var minutes = Math.Clamp(
                _options.CurrentValue.PendingTenantExpirationWorkerInitialDelayMinutes,
                0,
                60);
            return TimeSpan.FromMinutes(minutes);
        }

        private TimeSpan GetInterval()
        {
            var hours = Math.Clamp(
                _options.CurrentValue.PendingTenantExpirationWorkerIntervalHours,
                1,
                168);
            return TimeSpan.FromHours(hours);
        }
    }
}
