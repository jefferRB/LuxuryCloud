using LuxuryApp.Models.PublicPages;

namespace LuxuryApp.Services.PublicPages
{
    public interface ITenantPublicPageQueryService
    {
        Task<TenantPublicPageViewModel?> GetBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default);

        Task<bool> CanUsePublicLandingPageAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);
    }
}
