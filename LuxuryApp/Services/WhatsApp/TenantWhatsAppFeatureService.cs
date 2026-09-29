using LuxuryApp.Services.Tenant;

namespace LuxuryApp.Services.WhatsApp
{
    public sealed class TenantWhatsAppFeatureService : ITenantWhatsAppFeatureService
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly ITenantWhatsAppSettingsService _tenantWhatsAppSettingsService;

        public TenantWhatsAppFeatureService(
            ITenantProvider tenantProvider,
            ITenantWhatsAppSettingsService tenantWhatsAppSettingsService)
        {
            _tenantProvider = tenantProvider;
            _tenantWhatsAppSettingsService = tenantWhatsAppSettingsService;
        }

        public async Task<bool> IsWhatsAppEnabledForCurrentTenantAsync(CancellationToken cancellationToken = default)
        {
            if (!_tenantProvider.HasTenant())
            {
                return false;
            }

            return await _tenantWhatsAppSettingsService.IsWhatsAppEnabledForTenantAsync(
                _tenantProvider.GetTenantId(),
                cancellationToken);
        }

        public async Task<bool> HasWhatsAppAddonAsync(CancellationToken cancellationToken = default)
        {
            if (!_tenantProvider.HasTenant())
            {
                return false;
            }

            return await _tenantWhatsAppSettingsService.HasActiveWhatsAppAddonAsync(
                _tenantProvider.GetTenantId(),
                cancellationToken);
        }
    }
}
