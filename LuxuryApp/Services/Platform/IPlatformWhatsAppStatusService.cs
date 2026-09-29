using LuxuryApp.Models.Platform;

namespace LuxuryApp.Services.Platform
{
    public interface IPlatformWhatsAppStatusService
    {
        Task<Dictionary<Guid, PlatformWhatsAppAddonState>> GetBatchStatusAsync(
            IReadOnlyList<Guid> tenantIds,
            CancellationToken cancellationToken = default);

        Task<PlatformWhatsAppAddonState> GetSingleStatusAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);
    }
}
