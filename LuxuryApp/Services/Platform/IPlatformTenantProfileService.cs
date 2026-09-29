using LuxuryApp.Models.Platform;

namespace LuxuryApp.Services.Platform
{
    public interface IPlatformTenantProfileService
    {
        Task<PlatformTenantFichaViewModel?> GetFichaAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);
    }
}
