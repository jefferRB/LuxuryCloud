using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using LuxuryApp.Models.PublicPages;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.PublicImages
{
    public sealed record PublicAssetQuotaUsage(
        long UsedBytes,
        long MaxBytes)
    {
        public long AvailableBytes => Math.Max(0, MaxBytes - UsedBytes);

        public decimal PercentUsed =>
            MaxBytes <= 0 ? 0 : Math.Round((decimal)UsedBytes * 100 / MaxBytes, 1);
    }

    public interface IPublicAssetQuotaService
    {
        Task<PublicAssetQuotaUsage> GetUsageAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);

        Task EnsureCanUploadAsync(
            Guid tenantId,
            long incomingBytes,
            Guid? replacingAssetId = null,
            CancellationToken cancellationToken = default);
    }

    public sealed class PublicAssetQuotaService : IPublicAssetQuotaService
    {
        private readonly ApplicationDbContext _context;
        private readonly PublicImageOptions _options;

        public PublicAssetQuotaService(
            ApplicationDbContext context,
            IOptions<PublicImageOptions> options)
        {
            _context = context;
            _options = options.Value;
        }

        public async Task<PublicAssetQuotaUsage> GetUsageAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var used = await ActiveAssetsForTenant(tenantId)
                .SumAsync(asset => (long?)asset.SizeBytes, cancellationToken) ?? 0;

            return new PublicAssetQuotaUsage(used, _options.MaxTenantPublicImageBytes);
        }

        public async Task EnsureCanUploadAsync(
            Guid tenantId,
            long incomingBytes,
            Guid? replacingAssetId = null,
            CancellationToken cancellationToken = default)
        {
            if (tenantId == Guid.Empty)
            {
                throw new PublicImageUploadException("No se pudo determinar el negocio actual.");
            }

            if (incomingBytes <= 0)
            {
                throw new PublicImageUploadException("Selecciona una imagen valida.");
            }

            var query = ActiveAssetsForTenant(tenantId);
            if (replacingAssetId.HasValue)
            {
                query = query.Where(asset => asset.Id != replacingAssetId.Value);
            }

            var usedWithoutReplacement =
                await query.SumAsync(asset => (long?)asset.SizeBytes, cancellationToken) ?? 0;

            if (usedWithoutReplacement + incomingBytes > _options.MaxTenantPublicImageBytes)
            {
                throw new PublicImageUploadException(
                    $"La imagen supera la cuota de almacenamiento publico del negocio ({FormatBytes(_options.MaxTenantPublicImageBytes)}).");
            }
        }

        private IQueryable<TenantPublicAsset> ActiveAssetsForTenant(Guid tenantId) =>
            _context.TenantPublicAssets
                .AsNoTracking()
                .Where(asset =>
                    asset.TenantId == tenantId &&
                    asset.IsActive &&
                    asset.DeletedAtUtc == null);

        private static string FormatBytes(long bytes)
        {
            var mb = bytes / 1024m / 1024m;
            return $"{mb:0.#} MB";
        }
    }
}
