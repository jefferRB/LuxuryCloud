using LuxuryApp.Models.Platform;

namespace LuxuryApp.Services.Platform
{
    public interface IPlatformMetricsService
    {
        Task<Dictionary<Guid, PlatformTenantUsageViewModel>> GetTenantUsageBatchAsync(
            IReadOnlyList<Guid> tenantIds,
            CancellationToken cancellationToken = default);

        Task<PlatformTenantUsageViewModel> GetTenantUsageAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);
    }
}
