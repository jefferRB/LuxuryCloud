using LuxuryApp.Models.Platform;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.WhatsApp;

namespace LuxuryApp.Workers
{
    public class ReminderWorker : BackgroundService
    {
        private readonly TenantExecutionService _tenantExecutionService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IWorkerHeartbeatService _heartbeatService;
        private readonly ILogger<ReminderWorker> _logger;

        public ReminderWorker(
            TenantExecutionService tenantExecutionService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IWorkerHeartbeatService heartbeatService,
            ILogger<ReminderWorker> logger)
        {
            _tenantExecutionService = tenantExecutionService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _heartbeatService = heartbeatService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ReminderWorker iniciado");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var nowCR = _businessDateTimeProvider.Now();

                    await _tenantExecutionService.RunForEachActiveTenantAsync(
                        async (serviceProvider, tenantId, cancellationToken) =>
                        {
                            var settings = serviceProvider.GetRequiredService<ITenantWhatsAppSettingsService>();
                            if (!await settings.IsWhatsAppEnabledForTenantAsync(tenantId, cancellationToken))
                            {
                                return;
                            }

                            var notifications = serviceProvider.GetRequiredService<ICalendarWhatsAppNotificationService>();

                            // Lotes diarios (modo "a hora fija") + recordatorios relativos + envío de pendientes.
                            await notifications.GenerateDailyBatchAsync(cancellationToken);
                            await notifications.ScheduleDueRemindersAsync(cancellationToken);
                            await notifications.ProcessPendingNotificationsAsync(cancellationToken);

                            _logger.LogDebug(
                                "Ciclo WhatsApp completado para TenantId {TenantId} a las {Now:yyyy-MM-dd HH:mm}.",
                                tenantId,
                                nowCR);
                        },
                        stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error general en ReminderWorker");
                }

                await _heartbeatService.TryBeatAsync(PlatformWorkerNames.Reminder, "ciclo completado", stoppingToken);

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
