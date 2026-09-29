using LuxuryApp.Models.PublicPages;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PublicAssetQuotaServiceTests
    {
        [Fact]
        public async Task GetUsageAsync_SumsOnlyActiveAssetsForCurrentTenant()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantA, "Tenant A");
            await SeedAssetAsync(context, tenantA, 100, true);
            await SeedAssetAsync(context, tenantA, 300, false);

            tenantProvider.TenantId = tenantB;
            await SeedTenantAsync(context, tenantB, "Tenant B");
            await SeedAssetAsync(context, tenantB, 900, true);

            tenantProvider.TenantId = tenantA;
            var service = CreateQuotaService(context, 1_000);

            var usage = await service.GetUsageAsync(tenantA);

            Assert.Equal(100, usage.UsedBytes);
            Assert.Equal(1_000, usage.MaxBytes);
        }

        [Fact]
        public async Task EnsureCanUploadAsync_AllowsReplacementBySubtractingOldActiveAsset()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantId, "Tenant Quota");
            var existing = await SeedAssetAsync(context, tenantId, 900, true);
            var service = CreateQuotaService(context, 1_000);

            await service.EnsureCanUploadAsync(tenantId, 800, existing.Id);
            await Assert.ThrowsAsync<PublicImageUploadException>(() =>
                service.EnsureCanUploadAsync(tenantId, 800));
        }

        private static PublicAssetQuotaService CreateQuotaService(
            DbContext context,
            long maxBytes) =>
            new(
                (ProyectoIdentity.Datos.ApplicationDbContext)context,
                Options.Create(new PublicImageOptions { MaxTenantPublicImageBytes = maxBytes }));

        private static async Task SeedTenantAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            string name)
        {
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = name, Activo = true });
            await context.SaveChangesAsync();
        }

        private static async Task<TenantPublicAsset> SeedAssetAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            long sizeBytes,
            bool active)
        {
            var asset = new TenantPublicAsset
            {
                AssetType = TenantPublicAssetType.BusinessGallery,
                StorageKey = $"tenants/{tenantId:N}/public-page/gallery/{Guid.NewGuid():N}.webp",
                PublicUrl = $"https://media.test/{Guid.NewGuid():N}.webp",
                ContentType = "image/webp",
                SizeBytes = sizeBytes,
                Width = 100,
                Height = 100,
                IsActive = active,
                DeletedAtUtc = active ? null : DateTime.UtcNow
            };

            context.TenantPublicAssets.Add(asset);
            await context.SaveChangesAsync();
            return asset;
        }
    }
}
