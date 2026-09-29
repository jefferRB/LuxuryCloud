using System.Text.RegularExpressions;
using LuxuryApp.Models.PublicPages;

namespace LuxuryApp.Services.PublicImages
{
    public static partial class PublicImageStorageKeyBuilder
    {
        private const string OutputExtension = ".webp";

        public static string Build(
            Guid tenantId,
            TenantPublicAssetType assetType,
            int? serviceId = null)
        {
            if (tenantId == Guid.Empty)
            {
                throw new ArgumentException("TenantId requerido.", nameof(tenantId));
            }

            var suffix = $"{Guid.NewGuid():N}{OutputExtension}";
            return assetType switch
            {
                TenantPublicAssetType.Logo =>
                    $"tenants/{tenantId:N}/public-page/logo/{suffix}",
                TenantPublicAssetType.Cover =>
                    $"tenants/{tenantId:N}/public-page/cover/{suffix}",
                TenantPublicAssetType.Location =>
                    $"tenants/{tenantId:N}/public-page/location/{suffix}",
                TenantPublicAssetType.BusinessGallery =>
                    $"tenants/{tenantId:N}/public-page/gallery/{suffix}",
                TenantPublicAssetType.ServiceMain when serviceId.HasValue =>
                    $"tenants/{tenantId:N}/services/{serviceId.Value}/main/{suffix}",
                TenantPublicAssetType.ServiceGallery when serviceId.HasValue =>
                    $"tenants/{tenantId:N}/services/{serviceId.Value}/gallery/{suffix}",
                _ => throw new ArgumentException("Tipo de asset o servicio invalido.", nameof(assetType))
            };
        }

        public static bool IsValidStorageKey(string? storageKey)
        {
            if (string.IsNullOrWhiteSpace(storageKey))
            {
                return false;
            }

            var normalized = storageKey.Replace('\\', '/');
            return normalized.Length <= 500 &&
                   normalized == storageKey &&
                   !normalized.Contains("..", StringComparison.Ordinal) &&
                   !normalized.StartsWith("/", StringComparison.Ordinal) &&
                   !normalized.Contains("//", StringComparison.Ordinal) &&
                   ValidStorageKeyRegex().IsMatch(normalized);
        }

        [GeneratedRegex(@"^tenants/[a-f0-9]{32}/(public-page/(logo|cover|location|gallery)|services/[0-9]+/(main|gallery))/[a-f0-9]{32}\.webp$", RegexOptions.CultureInvariant)]
        private static partial Regex ValidStorageKeyRegex();
    }
}
