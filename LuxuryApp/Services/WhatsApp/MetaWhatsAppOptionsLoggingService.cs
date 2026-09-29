using Microsoft.Extensions.Options;

namespace LuxuryApp.Services.WhatsApp
{
    public sealed class MetaWhatsAppOptionsLoggingService : IHostedService, IDisposable
    {
        private readonly IOptionsMonitor<MetaWhatsAppOptions> _options;
        private readonly ILogger<MetaWhatsAppOptionsLoggingService> _logger;
        private IDisposable? _subscription;

        public MetaWhatsAppOptionsLoggingService(
            IOptionsMonitor<MetaWhatsAppOptions> options,
            ILogger<MetaWhatsAppOptionsLoggingService> logger)
        {
            _options = options;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            MetaWhatsAppDiagnosticsLogger.LogEffectiveConfiguration(_logger, _options.CurrentValue, "startup");
            _subscription = _options.OnChange((options, _) =>
                MetaWhatsAppDiagnosticsLogger.LogEffectiveConfiguration(_logger, options, "options_changed"));

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() => _subscription?.Dispose();
    }
}
