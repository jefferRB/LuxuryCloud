using LuxuryApp.Models.Platform;

namespace LuxuryApp.Services.Platform
{
    public interface IPlatformHealthService
    {
        PlatformTenantHealthViewModel ComputeHealth(
            bool canAccessApp,
            PlatformTenantUsageViewModel usage,
            bool whatsAppEnabled,
            bool hasWhatsAppRecentError,
            bool hasPendingCheckout,
            bool isExpiringSoon);
    }
}
