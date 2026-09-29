using LuxuryApp.Controllers.Configuracion;
using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.PublicPages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantPublicPageImageAjaxControllerTests
    {
        [Fact]
        public async Task UploadLogo_AjaxRequest_ReturnsJsonSuccess()
        {
            var controller = CreateController(new FakeUploadService());
            controller.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

            var result = await controller.UploadLogo(
                CreateFormFile(),
                new PublicImageCropRequest { CropX = 0, CropY = 0, CropWidth = 10, CropHeight = 10 },
                CancellationToken.None);

            var json = Assert.IsType<JsonResult>(result);
            Assert.True((bool)GetValue(json.Value!, "success")!);
            Assert.Equal("https://media.test/logo.webp", GetValue(json.Value!, "publicUrl"));
            Assert.Equal("1 MB de 25 MB usados", GetValue(json.Value!, "usageLabel"));
        }

        [Fact]
        public async Task UploadLogo_AjaxRequest_WhenUploadFails_ReturnsJsonError()
        {
            var controller = CreateController(new FakeUploadService
            {
                Error = new PublicImageUploadException("Formato no permitido.")
            });
            controller.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

            var result = await controller.UploadLogo(
                CreateFormFile(),
                new PublicImageCropRequest(),
                CancellationToken.None);

            var json = Assert.IsType<JsonResult>(result);
            Assert.False((bool)GetValue(json.Value!, "success")!);
            Assert.Equal("Formato no permitido.", GetValue(json.Value!, "message"));
        }

        [Fact]
        public async Task UploadLogo_NonAjaxRequest_UsesTraditionalRedirect()
        {
            var controller = CreateController(new FakeUploadService());

            var result = await controller.UploadLogo(
                CreateFormFile(),
                new PublicImageCropRequest(),
                CancellationToken.None);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(PaginaPublicaController.Index), redirect.ActionName);
        }

        private static PaginaPublicaController CreateController(IPublicImageUploadService uploadService)
        {
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("public.test");

            return new PaginaPublicaController(
                new FakeSettingsService(),
                uploadService,
                NullLogger<PaginaPublicaController>.Instance)
            {
                TempData = new TempDataDictionary(http, new FakeTempDataProvider()),
                ControllerContext = new ControllerContext
                {
                    HttpContext = http
                }
            };
        }

        private static IFormFile CreateFormFile()
        {
            var stream = new MemoryStream(new byte[] { 1, 2, 3 });
            return new FormFile(stream, 0, stream.Length, "file", "logo.png")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/png"
            };
        }

        private static object? GetValue(object value, string propertyName) =>
            value.GetType().GetProperty(propertyName)?.GetValue(value);

        private sealed class FakeTempDataProvider : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) =>
                new Dictionary<string, object>();

            public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            {
            }
        }

        private sealed class FakeUploadService : IPublicImageUploadService
        {
            public Exception? Error { get; init; }

            public Task<TenantPublicAsset> UploadPublicPageAssetAsync(
                TenantPublicAssetType assetType,
                IFormFile? file,
                string? userId,
                CancellationToken cancellationToken = default,
                PublicImageCropRequest? crop = null)
            {
                if (Error is not null)
                {
                    throw Error;
                }

                return Task.FromResult(new TenantPublicAsset
                {
                    Id = Guid.NewGuid(),
                    AssetType = assetType,
                    StorageKey = "tenants/test/public-page/logo/test.webp",
                    PublicUrl = "https://media.test/logo.webp",
                    ContentType = "image/webp",
                    SizeBytes = 1234,
                    Width = 512,
                    Height = 512,
                    IsActive = true
                });
            }

            public Task<TenantPublicAsset> UploadServiceAssetAsync(
                TenantPublicAssetType assetType,
                int serviceId,
                IFormFile? file,
                string? userId,
                CancellationToken cancellationToken = default,
                PublicImageCropRequest? crop = null) =>
                UploadPublicPageAssetAsync(assetType, file, userId, cancellationToken, crop);

            public Task RemovePublicPageSingletonAsync(
                TenantPublicAssetType assetType,
                string? userId,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task RemoveServiceMainImageAsync(
                int serviceId,
                string? userId,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task RemoveAssetAsync(
                Guid assetId,
                string? userId,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        private sealed class FakeSettingsService : ITenantPublicPageSettingsService
        {
            public Task<EditTenantPublicPageViewModel> BuildForCurrentTenantAsync(
                HttpRequest? request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new EditTenantPublicPageViewModel
                {
                    BusinessName = "Tenant",
                    StorageUsage = new PublicAssetUsageViewModel
                    {
                        UsedBytes = 1024 * 1024,
                        MaxBytes = 25L * 1024 * 1024
                    }
                });

            public Task<EditTenantPublicPageViewModel> PopulateReadOnlyFieldsAsync(
                EditTenantPublicPageViewModel model,
                HttpRequest? request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(model);

            public Task SaveForCurrentTenantAsync(
                EditTenantPublicPageViewModel input,
                string? userId,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<bool> CanUsePublicLandingPageAsync(
                Guid tenantId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(true);
        }
    }
}
