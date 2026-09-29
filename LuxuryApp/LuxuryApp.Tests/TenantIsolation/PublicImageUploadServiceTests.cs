using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.PublicPages;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PublicImageUploadServiceTests
    {
        [Fact]
        public async Task UploadPublicPageAsset_ValidLogo_ReencodesWebpAndUsesOpaqueKey()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantId, "Tenant Upload");

            var storage = new FakePublicImageStorageService();
            var service = CreateUploadService(context, tenantProvider, storage);

            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Logo,
                CreateImageFile("mi-logo.png"),
                "user");

            Assert.Equal(TenantPublicAssetType.Logo, asset.AssetType);
            Assert.EndsWith(".webp", asset.StorageKey);
            Assert.DoesNotContain("mi-logo", asset.StorageKey, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("image/webp", asset.ContentType);
            Assert.Equal($"https://media.test/{asset.StorageKey}", asset.PublicUrl);
            Assert.True(storage.UploadedBytes[asset.StorageKey].Length > 0);
            Assert.Equal((byte)'R', storage.UploadedBytes[asset.StorageKey][0]);
        }

        [Fact]
        public async Task UploadPublicPageAsset_RejectsDangerousDoubleExtension()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantId, "Tenant Upload");

            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            await Assert.ThrowsAsync<PublicImageUploadException>(() =>
                service.UploadPublicPageAssetAsync(
                    TenantPublicAssetType.Logo,
                    CreateImageFile("logo.svg.jpg"),
                    "user"));
        }

        [Fact]
        public async Task UploadPublicPageAsset_ReplacingLogo_InactivatesOldAssetAndDeletesOldStorage()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantId, "Tenant Replace");

            var storage = new FakePublicImageStorageService();
            var service = CreateUploadService(context, tenantProvider, storage);

            var first = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Logo,
                CreateImageFile("logo-one.png"),
                "user");
            var second = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Logo,
                CreateImageFile("logo-two.png"),
                "user");

            var assets = await context.TenantPublicAssets
                .AsNoTracking()
                .OrderBy(asset => asset.CreatedAtUtc)
                .ToListAsync();

            Assert.Equal(2, assets.Count);
            Assert.False(assets.Single(asset => asset.Id == first.Id).IsActive);
            Assert.True(assets.Single(asset => asset.Id == second.Id).IsActive);
            Assert.Contains(first.StorageKey, storage.DeletedKeys);
        }

        [Fact]
        public async Task UploadPublicPageAsset_ValidCrop_AppliesCropBeforeSaving()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantId, "Tenant Crop");

            var storage = new FakePublicImageStorageService();
            var service = CreateUploadService(context, tenantProvider, storage);

            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Logo,
                CreateSplitColorImageFile("crop.png"),
                "user",
                crop: new PublicImageCropRequest
                {
                    CropX = 40,
                    CropY = 0,
                    CropWidth = 40,
                    CropHeight = 40
                });

            using var image = Image.Load<Rgba32>(storage.UploadedBytes[asset.StorageKey]);
            var center = image[image.Width / 2, image.Height / 2];

            Assert.True(center.B > center.R, $"Expected blue-dominant crop, got R:{center.R} B:{center.B}.");
            Assert.Equal(40, asset.Width);
            Assert.Equal(40, asset.Height);
        }

        [Fact]
        public async Task UploadPublicPageAsset_InvalidCrop_NormalizesSafely()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantId, "Tenant Invalid Crop");

            var storage = new FakePublicImageStorageService();
            var service = CreateUploadService(context, tenantProvider, storage);

            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.BusinessGallery,
                CreateImageFile("gallery.png"),
                "user",
                crop: new PublicImageCropRequest
                {
                    CropX = 999,
                    CropY = 999,
                    CropWidth = 100,
                    CropHeight = 100
                });

            Assert.True(asset.Width > 0);
            Assert.True(asset.Height > 0);
            Assert.True(storage.UploadedBytes[asset.StorageKey].Length > 0);
        }

        [Fact]
        public async Task UploadServiceAsset_RejectsServiceFromAnotherTenant()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantB, "Tenant B");
            var serviceFromTenantB = await SeedServiceAsync(context, "Servicio B");

            tenantProvider.TenantId = tenantA;
            await SeedTenantAsync(context, tenantA, "Tenant A");
            var upload = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            await Assert.ThrowsAsync<PublicImageUploadException>(() =>
                upload.UploadServiceAssetAsync(
                    TenantPublicAssetType.ServiceMain,
                    serviceFromTenantB.Id,
                    CreateImageFile("servicio.png"),
                    "user"));
        }

        [Fact]
        public async Task UploadServiceAsset_ReplacingMainImage_InactivatesOldAsset()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantId, "Tenant Service Main");
            var servicio = await SeedServiceAsync(context, "Servicio");

            var storage = new FakePublicImageStorageService();
            var upload = CreateUploadService(context, tenantProvider, storage);

            var first = await upload.UploadServiceAssetAsync(
                TenantPublicAssetType.ServiceMain,
                servicio.Id,
                CreateImageFile("main-one.png"),
                "user");
            var second = await upload.UploadServiceAssetAsync(
                TenantPublicAssetType.ServiceMain,
                servicio.Id,
                CreateImageFile("main-two.png"),
                "user");

            var assets = await context.TenantPublicAssets
                .AsNoTracking()
                .Where(asset => asset.ServicioId == servicio.Id)
                .OrderBy(asset => asset.CreatedAtUtc)
                .ToListAsync();

            Assert.False(assets.Single(asset => asset.Id == first.Id).IsActive);
            Assert.True(assets.Single(asset => asset.Id == second.Id).IsActive);
            Assert.Contains(first.StorageKey, storage.DeletedKeys);
        }

        [Fact]
        public async Task UploadPublicPageAsset_RejectsWhenQuotaExceeded()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantId, "Tenant Quota");

            var upload = CreateUploadService(
                context,
                tenantProvider,
                new FakePublicImageStorageService(),
                new PublicImageOptions { MaxTenantPublicImageBytes = 1 });

            await Assert.ThrowsAsync<PublicImageUploadException>(() =>
                upload.UploadPublicPageAssetAsync(
                    TenantPublicAssetType.BusinessGallery,
                    CreateImageFile("gallery.png"),
                    "user"));
        }

        [Fact]
        public async Task UploadPublicPageAsset_ValidLocation_UsesLocationKeyAndIsSingleton()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantId, "Tenant Location");

            var storage = new FakePublicImageStorageService();
            var service = CreateUploadService(context, tenantProvider, storage);

            var first = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Location,
                CreateImageFile("fachada.png"),
                "user");

            Assert.Equal(TenantPublicAssetType.Location, first.AssetType);
            Assert.Contains("/public-page/location/", first.StorageKey);
            Assert.EndsWith(".webp", first.StorageKey);
            Assert.True(storage.UploadedBytes[first.StorageKey].Length > 0);

            // Reemplazo: la anterior queda inactiva (singleton) y se borra su storage.
            var second = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Location,
                CreateImageFile("interior.png"),
                "user");

            var assets = await context.TenantPublicAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => asset.AssetType == TenantPublicAssetType.Location)
                .ToListAsync();

            Assert.Equal(2, assets.Count);
            Assert.False(assets.Single(asset => asset.Id == first.Id).IsActive);
            Assert.True(assets.Single(asset => asset.Id == second.Id).IsActive);
            Assert.Contains(first.StorageKey, storage.DeletedKeys);
        }

        [Fact]
        public async Task RemovePublicPageSingleton_Location_InactivatesAndDeletesStorage()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await SeedTenantAsync(context, tenantId, "Tenant Location Remove");

            var storage = new FakePublicImageStorageService();
            var service = CreateUploadService(context, tenantProvider, storage);

            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Location,
                CreateImageFile("fachada.png"),
                "user");

            await service.RemovePublicPageSingletonAsync(TenantPublicAssetType.Location, "user");

            var stored = await context.TenantPublicAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == asset.Id);

            Assert.False(stored.IsActive);
            Assert.NotNull(stored.DeletedAtUtc);
            Assert.Contains(asset.StorageKey, storage.DeletedKeys);
        }

        [Fact]
        public async Task RemovePublicPageSingleton_Location_DoesNotAffectOtherTenant()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantB, "Tenant B");
            var storage = new FakePublicImageStorageService();
            var uploadB = CreateUploadService(context, tenantProvider, storage);
            var assetB = await uploadB.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Location,
                CreateImageFile("fachada-b.png"),
                "user");

            // Otro tenant intenta remover: solo afecta su propio singleton (ninguno).
            tenantProvider.TenantId = tenantA;
            await SeedTenantAsync(context, tenantA, "Tenant A");
            var uploadA = CreateUploadService(context, tenantProvider, storage);
            await uploadA.RemovePublicPageSingletonAsync(TenantPublicAssetType.Location, "user");

            var stored = await context.TenantPublicAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == assetB.Id);

            Assert.True(stored.IsActive);
            Assert.DoesNotContain(assetB.StorageKey, storage.DeletedKeys);
        }

        [Fact]
        public async Task UploadServiceAsset_VerticalPhoto_Cover_ProducesThreeFourFrame()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Vertical Servicio");
            using var _ = context;
            using var __ = connection;
            var servicio = await SeedServiceAsync(context, "Servicio");
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            // Foto vertical 9:16 tomada con celular: la tarjeta publica usa marco 3:4.
            var asset = await service.UploadServiceAssetAsync(
                TenantPublicAssetType.ServiceMain,
                servicio.Id,
                CreateImageFile("vertical.png", 900, 1600),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Cover", TargetAspectRatio = 3d / 4d });

            AssertAspect(asset, 3d / 4d);
        }

        [Fact]
        public async Task UploadServiceAsset_TypicalPhonePortrait_NeedsNoCropAtAll()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Foto Vertical Normal");
            using var _ = context;
            using var __ = connection;
            var servicio = await SeedServiceAsync(context, "Servicio");
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            // Foto de iPhone en vertical (3:4 nativo): entra completa, sin recorte visible.
            var asset = await service.UploadServiceAssetAsync(
                TenantPublicAssetType.ServiceMain,
                servicio.Id,
                CreateJpegFile("iphone-vertical.jpg", 3024, 4032),
                "user");

            // Exactamente el marco del perfil: ni deformada ni recortada.
            Assert.Equal(1080, asset.Width);
            Assert.Equal(1440, asset.Height);
        }

        [Fact]
        public async Task UploadServiceAsset_SmallImage_IsNotUpscaledToTheFrame()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Servicio Chico");
            using var _ = context;
            using var __ = connection;
            var servicio = await SeedServiceAsync(context, "Servicio");
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            var asset = await service.UploadServiceAssetAsync(
                TenantPublicAssetType.ServiceMain,
                servicio.Id,
                CreateImageFile("chica.png", 300, 400),
                "user");

            AssertAspect(asset, 3d / 4d);
            Assert.True(asset.Width <= 300, $"No se debe ampliar: {asset.Width}x{asset.Height}.");
        }

        [Fact]
        public async Task UploadServiceAsset_BothModes_ProduceTheSameFrame()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Servicio Modos");
            using var _ = context;
            using var __ = connection;
            var recortado = await SeedServiceAsync(context, "Servicio recortado");
            var conFondo = await SeedServiceAsync(context, "Servicio con fondo");
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            // Misma foto horizontal en los dos modos: cambia como se presenta, NO el marco.
            var cover = await service.UploadServiceAssetAsync(
                TenantPublicAssetType.ServiceMain,
                recortado.Id,
                CreateJpegFile("horizontal-a.jpg", 4032, 3024),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Cover", TargetAspectRatio = 3d / 4d });

            var padded = await service.UploadServiceAssetAsync(
                TenantPublicAssetType.ServiceMain,
                conFondo.Id,
                CreateJpegFile("horizontal-b.jpg", 4032, 3024),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Padded", TargetAspectRatio = 3d / 4d });

            AssertAspect(cover, 3d / 4d);
            AssertAspect(padded, 3d / 4d);
            Assert.Equal(cover.Width, padded.Width);
            Assert.Equal(cover.Height, padded.Height);
        }

        [Fact]
        public async Task UploadServiceAsset_VerticalPhoto_Padded_KeepsWholePhotoInSquareFrame()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Servicio Fondo");
            using var _ = context;
            using var __ = connection;
            var servicio = await SeedServiceAsync(context, "Servicio");
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            var asset = await service.UploadServiceAssetAsync(
                TenantPublicAssetType.ServiceMain,
                servicio.Id,
                CreateImageFile("vertical.png", 900, 1600),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Padded", TargetAspectRatio = 3d / 4d });

            AssertAspect(asset, 3d / 4d);
        }

        [Fact]
        public async Task UploadServiceAsset_AspectNotOfferedByProfile_Throws()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Servicio Aspecto");
            using var _ = context;
            using var __ = connection;
            var servicio = await SeedServiceAsync(context, "Servicio");
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            // 16:9 no es una opcion del perfil de servicio: se rechaza para no romper el marco.
            await Assert.ThrowsAsync<PublicImageUploadException>(() =>
                service.UploadServiceAssetAsync(
                    TenantPublicAssetType.ServiceMain,
                    servicio.Id,
                    CreateImageFile("ancha.png", 1600, 900),
                    "user",
                    crop: new PublicImageCropRequest { FitMode = "Cover", TargetAspectRatio = 16d / 9d }));
        }

        [Fact]
        public async Task UploadPublicPageAsset_VerticalLocation_Original_PreservesAspect()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Vertical Ubicacion");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Location,
                CreateImageFile("ubicacion.png", 600, 900), // 2:3 vertical
                "user",
                crop: new PublicImageCropRequest { FitMode = "Original" });

            AssertAspect(asset, 600d / 900d);
            Assert.True(asset.Width <= 1400 && asset.Height <= 1400);
        }

        [Fact]
        public async Task UploadPublicPageAsset_VerticalGallery_Original_PreservesAspect()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Vertical Galeria");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.BusinessGallery,
                CreateImageFile("galeria.png", 800, 1200),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Original" });

            AssertAspect(asset, 800d / 1200d);
        }

        [Fact]
        public async Task UploadPublicPageAsset_VerticalCover_Padded_ProducesTargetAspect()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Portada Vertical");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            // Portada vertical adaptada a 16:9 con fondo (no se recorta la foto).
            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Cover,
                CreateImageFile("portada.png", 900, 1600),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Padded", TargetAspectRatio = 16d / 9d });

            AssertAspect(asset, 16d / 9d);
            Assert.True(asset.Width <= 1920 && asset.Height <= 1080);
        }

        [Fact]
        public async Task UploadPublicPageAsset_RectangularLogo_Contain_ProducesSquareWithoutHardCrop()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Logo");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            // Logo horizontal 2:1: con Contain queda contenido en un cuadro (no se recorta).
            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Logo,
                CreateImageFile("logo.png", 400, 200),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Contain", TargetAspectRatio = 1d });

            AssertAspect(asset, 1d);
            Assert.True(asset.Width <= 512 && asset.Height <= 512);
        }

        [Fact]
        public async Task UploadPublicPageAsset_Original_Horizontal_PreservesAspect()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Horizontal Original");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.BusinessGallery,
                CreateImageFile("horizontal.png", 1600, 900),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Original" });

            AssertAspect(asset, 1600d / 900d);
        }

        [Fact]
        public async Task UploadPublicPageAsset_AbsurdTargetAspect_Throws()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Aspecto Absurdo");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            await Assert.ThrowsAsync<PublicImageUploadException>(() =>
                service.UploadPublicPageAssetAsync(
                    TenantPublicAssetType.Cover,
                    CreateImageFile("absurdo.png", 900, 1600),
                    "user",
                    crop: new PublicImageCropRequest { FitMode = "Padded", TargetAspectRatio = 10d }));
        }

        [Fact]
        public async Task UploadPublicPageAsset_LargePhonephoto_IsOptimizedInsteadOfRejected()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Foto Grande");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            // 12 MP horizontales: lo que sale de cualquier celular actual.
            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.Cover,
                CreateJpegFile("portada-celular.jpg", 4000, 3000),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Cover", TargetAspectRatio = 16d / 9d });

            var options = new PublicImageOptions();
            Assert.True(asset.Width <= options.CoverMaxWidth, $"Ancho {asset.Width} supera el perfil.");
            Assert.True(asset.Height <= options.CoverMaxHeight, $"Alto {asset.Height} supera el perfil.");
            Assert.True(asset.SizeBytes > 0);
            AssertAspect(asset, 16d / 9d);
        }

        [Fact]
        public async Task UploadPublicPageAsset_SmallImage_IsNotUpscaled()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Sin Upscale");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.BusinessGallery,
                CreateImageFile("chica.png", 240, 300),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Original" });

            Assert.Equal(240, asset.Width);
            Assert.Equal(300, asset.Height);
        }

        [Fact]
        public async Task UploadPublicPageAsset_ExifRotatedPhoto_IsNormalizedBeforeSaving()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Orientacion");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            // Guardada como 80x40 pero con orientacion EXIF 6 (rotar 90): el navegador la muestra
            // vertical, y el archivo almacenado debe quedar igual de vertical.
            var asset = await service.UploadPublicPageAssetAsync(
                TenantPublicAssetType.BusinessGallery,
                CreateRotatedJpegFile("vertical-celular.jpg", 80, 40, orientation: 6),
                "user",
                crop: new PublicImageCropRequest { FitMode = "Original" });

            Assert.True(
                asset.Height > asset.Width,
                $"Se esperaba una imagen vertical tras normalizar la orientacion, se obtuvo {asset.Width}x{asset.Height}.");
        }

        [Fact]
        public async Task UploadPublicPageAsset_ExceedingDecodedPixelLimit_ThrowsFriendlyError()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Limite Pixeles");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(
                context,
                tenantProvider,
                new FakePublicImageStorageService(),
                new PublicImageOptions { MaxNonJpegDecodedPixels = 100 });

            var error = await Assert.ThrowsAsync<PublicImageUploadException>(() =>
                service.UploadPublicPageAssetAsync(
                    TenantPublicAssetType.BusinessGallery,
                    CreateImageFile("bomba.png", 64, 64),
                    "user"));

            Assert.Contains("resolucion", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task UploadPublicPageAsset_CorruptFile_IsRejected()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant Archivo Danado");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            await Assert.ThrowsAsync<PublicImageUploadException>(() =>
                service.UploadPublicPageAssetAsync(
                    TenantPublicAssetType.BusinessGallery,
                    CreateRawFile("texto.png", "image/png", "esto no es una imagen"),
                    "user"));
        }

        [Fact]
        public async Task UploadPublicPageAsset_HeicFile_ExplainsHowToConvertIt()
        {
            var (context, connection, tenantProvider) = await NewTenantContextAsync("Tenant HEIC");
            using var _ = context;
            using var __ = connection;
            var service = CreateUploadService(context, tenantProvider, new FakePublicImageStorageService());

            var error = await Assert.ThrowsAsync<PublicImageUploadException>(() =>
                service.UploadPublicPageAssetAsync(
                    TenantPublicAssetType.BusinessGallery,
                    CreateRawFile("foto.heic", "image/heic", "cualquier contenido"),
                    "user"));

            Assert.Contains("HEIC", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        private static void AssertAspect(TenantPublicAsset asset, double expected)
        {
            Assert.True(asset.Width > 0 && asset.Height > 0);
            var actual = (double)asset.Width / asset.Height;
            Assert.True(
                Math.Abs(actual - expected) <= 0.06,
                $"Aspecto esperado ~{expected:0.###}, obtenido {actual:0.###} ({asset.Width}x{asset.Height}).");
        }

        private static async Task<(ApplicationDbContext Context, System.Data.Common.DbConnection Connection, TestTenantProvider TenantProvider)> NewTenantContextAsync(string name)
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            await SeedTenantAsync(context, tenantId, name);
            return (context, connection, tenantProvider);
        }

        private static PublicImageUploadService CreateUploadService(
            ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            FakePublicImageStorageService storage,
            PublicImageOptions? options = null)
        {
            options ??= new PublicImageOptions();
            return new PublicImageUploadService(
                context,
                tenantProvider,
                storage,
                new PublicAssetQuotaService(context, Options.Create(options)),
                new NoOpUploadedFileSecurityScanner(),
                new PublicImageProfileProvider(Options.Create(options)),
                Options.Create(options),
                NullLogger<PublicImageUploadService>.Instance);
        }

        private static IFormFile CreateImageFile(string fileName) =>
            CreateImageFile(fileName, 32, 32);

        private static IFormFile CreateImageFile(string fileName, int width, int height)
        {
            var stream = new MemoryStream();
            using (var image = new Image<Rgba32>(width, height))
            {
                image.Mutate(context => context.BackgroundColor(Color.Red));
                image.SaveAsPng(stream);
            }

            stream.Position = 0;
            return new FormFile(stream, 0, stream.Length, "file", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/png"
            };
        }

        private static IFormFile CreateJpegFile(string fileName, int width, int height)
        {
            var stream = new MemoryStream();
            using (var image = new Image<Rgba32>(width, height))
            {
                image.Mutate(context => context.BackgroundColor(Color.Teal));
                image.SaveAsJpeg(stream);
            }

            stream.Position = 0;
            return new FormFile(stream, 0, stream.Length, "file", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg"
            };
        }

        private static IFormFile CreateRotatedJpegFile(string fileName, int width, int height, ushort orientation)
        {
            var stream = new MemoryStream();
            using (var image = new Image<Rgba32>(width, height))
            {
                image.Mutate(context => context.BackgroundColor(Color.Orange));
                image.Metadata.ExifProfile = new ExifProfile();
                image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
                image.SaveAsJpeg(stream);
            }

            stream.Position = 0;
            return new FormFile(stream, 0, stream.Length, "file", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg"
            };
        }

        private static IFormFile CreateRawFile(string fileName, string contentType, string content)
        {
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            return new FormFile(stream, 0, stream.Length, "file", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = contentType
            };
        }

        private static IFormFile CreateSplitColorImageFile(string fileName)
        {
            var stream = new MemoryStream();
            using (var image = new Image<Rgba32>(80, 40))
            {
                for (var y = 0; y < image.Height; y++)
                {
                    for (var x = 0; x < image.Width; x++)
                    {
                        image[x, y] = x < 40 ? Color.Red : Color.Blue;
                    }
                }

                image.SaveAsPng(stream);
            }

            stream.Position = 0;
            return new FormFile(stream, 0, stream.Length, "file", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/png"
            };
        }

        private static async Task SeedTenantAsync(
            ApplicationDbContext context,
            Guid tenantId,
            string name)
        {
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = name, Activo = true });
            await context.SaveChangesAsync();
        }

        private static async Task<Servicio> SeedServiceAsync(
            ApplicationDbContext context,
            string name)
        {
            var service = new Servicio
            {
                Nombre = name,
                Precio = 5000m,
                DuracionMinutos = 30,
                Activo = true
            };
            context.Servicios.Add(service);
            await context.SaveChangesAsync();
            return service;
        }

        private sealed class FakePublicImageStorageService : IPublicImageStorageService
        {
            public Dictionary<string, byte[]> UploadedBytes { get; } = new(StringComparer.Ordinal);
            public List<string> DeletedKeys { get; } = new();

            public async Task<PublicImageStoredObject> UploadAsync(
                string storageKey,
                Stream content,
                string contentType,
                CancellationToken cancellationToken = default)
            {
                if (!IsValidStorageKey(storageKey))
                {
                    throw new InvalidOperationException("Invalid key.");
                }

                if (content.CanSeek)
                {
                    content.Position = 0;
                }

                await using var copy = new MemoryStream();
                await content.CopyToAsync(copy, cancellationToken);
                UploadedBytes[storageKey] = copy.ToArray();

                return new PublicImageStoredObject(
                    storageKey,
                    BuildPublicUrl(storageKey),
                    contentType,
                    copy.Length);
            }

            public Task<bool> TryDeleteAsync(
                string storageKey,
                CancellationToken cancellationToken = default)
            {
                DeletedKeys.Add(storageKey);
                return Task.FromResult(true);
            }

            public string BuildPublicUrl(string storageKey) =>
                $"https://media.test/{storageKey}";

            public bool IsValidStorageKey(string storageKey) =>
                PublicImageStorageKeyBuilder.IsValidStorageKey(storageKey);
        }
    }
}
