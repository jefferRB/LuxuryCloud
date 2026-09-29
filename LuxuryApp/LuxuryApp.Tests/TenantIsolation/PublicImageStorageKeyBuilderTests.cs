using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.PublicImages;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PublicImageStorageKeyBuilderTests
    {
        [Theory]
        [InlineData(TenantPublicAssetType.Logo, null, "/public-page/logo/")]
        [InlineData(TenantPublicAssetType.Cover, null, "/public-page/cover/")]
        [InlineData(TenantPublicAssetType.Location, null, "/public-page/location/")]
        [InlineData(TenantPublicAssetType.BusinessGallery, null, "/public-page/gallery/")]
        [InlineData(TenantPublicAssetType.ServiceMain, 42, "/services/42/main/")]
        [InlineData(TenantPublicAssetType.ServiceGallery, 42, "/services/42/gallery/")]
        public void Build_CreatesOpaqueSafeWebpKey(
            TenantPublicAssetType assetType,
            int? serviceId,
            string expectedSegment)
        {
            var tenantId = Guid.NewGuid();

            var key = PublicImageStorageKeyBuilder.Build(tenantId, assetType, serviceId);

            Assert.StartsWith($"tenants/{tenantId:N}", key);
            Assert.Contains(expectedSegment, key);
            Assert.EndsWith(".webp", key);
            Assert.True(PublicImageStorageKeyBuilder.IsValidStorageKey(key));
            Assert.DoesNotContain("logo.png", key, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("../tenants/x.webp")]
        [InlineData("/tenants/abc/public-page/logo/file.webp")]
        [InlineData("tenants\\abc\\public-page\\logo\\file.webp")]
        [InlineData("tenants/abc//public-page/logo/file.webp")]
        [InlineData("https://media.test/file.webp")]
        public void IsValidStorageKey_RejectsTraversalAndExternalPaths(string key)
        {
            Assert.False(PublicImageStorageKeyBuilder.IsValidStorageKey(key));
        }
    }
}
