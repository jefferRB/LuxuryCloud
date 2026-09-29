using LuxuryApp.Models.PublicPages;

namespace LuxuryApp.Services.PublicImages
{
    public interface IPublicImageUploadService
    {
        Task<TenantPublicAsset> UploadPublicPageAssetAsync(
            TenantPublicAssetType assetType,
            IFormFile? file,
            string? userId,
            CancellationToken cancellationToken = default,
            PublicImageCropRequest? crop = null);

        Task<TenantPublicAsset> UploadServiceAssetAsync(
            TenantPublicAssetType assetType,
            int serviceId,
            IFormFile? file,
            string? userId,
            CancellationToken cancellationToken = default,
            PublicImageCropRequest? crop = null);

        Task RemovePublicPageSingletonAsync(
            TenantPublicAssetType assetType,
            string? userId,
            CancellationToken cancellationToken = default);

        Task RemoveServiceMainImageAsync(
            int serviceId,
            string? userId,
            CancellationToken cancellationToken = default);

        Task RemoveAssetAsync(
            Guid assetId,
            string? userId,
            CancellationToken cancellationToken = default);
    }
}
