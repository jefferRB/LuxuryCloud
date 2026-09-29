using LuxuryApp.Models.PublicPages;
using Microsoft.AspNetCore.Http;

namespace LuxuryApp.Services.PublicPages
{
    public interface ITenantPublicPageSettingsService
    {
        Task<EditTenantPublicPageViewModel> BuildForCurrentTenantAsync(
            HttpRequest? request,
            CancellationToken cancellationToken = default);

        Task<EditTenantPublicPageViewModel> PopulateReadOnlyFieldsAsync(
            EditTenantPublicPageViewModel model,
            HttpRequest? request,
            CancellationToken cancellationToken = default);

        Task SaveForCurrentTenantAsync(
            EditTenantPublicPageViewModel input,
            string? userId,
            CancellationToken cancellationToken = default);

        Task<bool> CanUsePublicLandingPageAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);
    }
}
