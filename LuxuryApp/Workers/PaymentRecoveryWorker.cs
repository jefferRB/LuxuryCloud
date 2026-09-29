using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Platform;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Workers
{
    /// <summary>
    /// Worker de recuperación de pago: cierra las gracias vencidas de los incidentes de pago fallido.
    /// Con <c>AutoSuspendAfterGrace=false</c> (default en producción inicial) NO corta acceso: solo
    /// marca GraceExpired y deja rastro dry-run para observar. Con true, suspende por impago.
    ///
    /// Cero HTTP a TiloPay. El correo de recuperación (Resend) se envía FUERA de transacción por el
    /// servicio de notificación, respetando <c>SendEmailNotifications</c> (dry-run si es false).
    /// Idempotente. Gateado por <c>BillingPaymentRecovery:Enabled</c>.
    /// </summary>
    public sealed class PaymentRecoveryWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptionsMonitor<BillingPaymentRecoveryOptions> _options;
        private readonly IWorkerHeartbeatService _heartbeatService;
        private readonly ILogger<PaymentRecoveryWorker> _logger;

        public PaymentRecoveryWorker(
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<BillingPaymentRecoveryOptions> options,
            IWorkerHeartbeatService heartbeatService,
            ILogger<PaymentRecoveryWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _heartbeatService = heartbeatService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "PaymentRecoveryWorker iniciado. Enabled {Enabled}. AutoSuspend {AutoSuspend}. IntervalMinutes {IntervalMinutes}.",
                _options.CurrentValue.Enabled,
                _options.CurrentValue.AutoSuspendAfterGrace,
                _options.CurrentValue.WorkerIntervalMinutes);

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
                    if (_options.CurrentValue.Enabled)
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var recovery = scope.ServiceProvider.GetRequiredService<IPaymentRecoveryService>();

                        // 1) Cierre local de gracias vencidas (marca GraceExpired y suspende solo con AutoSuspend).
                        var processed = await recovery.RunGraceExpirationPassAsync(stoppingToken);
                        if (processed > 0)
                        {
                            _logger.LogInformation(
                                "Pase de recuperación de pago: {Processed} incidente(s) con gracia vencida procesados. AutoSuspend {AutoSuspend}.",
                                processed,
                                _options.CurrentValue.AutoSuspendAfterGrace);
                        }

                        // 2) Notificaciones (inicial / recordatorio / suspensión). El correo va fuera de la
                        // transacción; respeta SendEmailNotifications (dry-run si es false).
                        var notifications = scope.ServiceProvider.GetRequiredService<IPaymentRecoveryNotificationService>();
                        var notified = await notifications.RunPendingNotificationsAsync(stoppingToken);
                        if (notified > 0)
                        {
                            _logger.LogInformation(
                                "Pase de recuperación de pago: {Notified} notificación(es) procesadas. SendEmails {SendEmails}.",
                                notified,
                                _options.CurrentValue.SendEmailNotifications);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error en el pase de recuperación de pago.");
                }

                await _heartbeatService.TryBeatAsync(
                    PlatformWorkerNames.PaymentRecovery,
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

        private TimeSpan GetInterval()
        {
            var minutes = Math.Clamp(_options.CurrentValue.WorkerIntervalMinutes, 5, 120);
            return TimeSpan.FromMinutes(minutes);
        }

        private TimeSpan GetInitialDelay()
        {
            var minutes = Math.Clamp(_options.CurrentValue.WorkerInitialDelayMinutes, 0, 10);
            return TimeSpan.FromMinutes(minutes);
        }
    }
}
