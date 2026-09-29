using LuxuryApp.Services.WhatsApp;

namespace LuxuryApp.Tests.Support
{
    internal sealed class FakeTenantWhatsAppFeatureService : ITenantWhatsAppFeatureService
    {
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Tener el complemento contratado NO es lo mismo que tenerlo encendido. Por defecto siguen
        /// el mismo valor; los tests que necesitan distinguirlos fijan este.
        /// </summary>
        public bool? HasAddon { get; set; }

        public Task<bool> IsWhatsAppEnabledForCurrentTenantAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(IsEnabled);

        public Task<bool> HasWhatsAppAddonAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(HasAddon ?? IsEnabled);
    }
}
