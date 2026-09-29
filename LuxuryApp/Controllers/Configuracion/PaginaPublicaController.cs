using LuxuryApp.Models.Asociados;
using System.Security.Claims;
using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.PublicPages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Configuracion
{
    [Authorize]
    [RequirePermission(AppPermissions.PublicWebsiteView)]
    [Route("Configuracion/[controller]")]
    public sealed class PaginaPublicaController : Controller
    {
        private readonly ITenantPublicPageSettingsService _settingsService;
        private readonly IPublicImageUploadService _imageUploadService;
        private readonly ILogger<PaginaPublicaController> _logger;

        public PaginaPublicaController(
            ITenantPublicPageSettingsService settingsService,
            IPublicImageUploadService imageUploadService,
            ILogger<PaginaPublicaController> logger)
        {
            _settingsService = settingsService;
            _imageUploadService = imageUploadService;
            _logger = logger;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var model = await _settingsService.BuildForCurrentTenantAsync(Request, cancellationToken);
            return View(model);
        }

        [HttpPost("")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(
            EditTenantPublicPageViewModel model,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return View(await _settingsService.PopulateReadOnlyFieldsAsync(model, Request, cancellationToken));
            }

            try
            {
                await _settingsService.SaveForCurrentTenantAsync(
                    model,
                    User.FindFirstValue(ClaimTypes.NameIdentifier),
                    cancellationToken);

                TempData["PaginaPublicaOk"] = "Pagina publica actualizada.";
                return RedirectToAction(nameof(Index));
            }
            catch (TenantPublicPageValidationException ex)
            {
                ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
                return View(await _settingsService.PopulateReadOnlyFieldsAsync(model, Request, cancellationToken));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al guardar la pagina publica del tenant.");
                ModelState.AddModelError(string.Empty, "No fue posible guardar la pagina publica.");
                return View(await _settingsService.PopulateReadOnlyFieldsAsync(model, Request, cancellationToken));
            }
        }

        [HttpPost("UploadLogo")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> UploadLogo(
            IFormFile? file,
            PublicImageCropRequest crop,
            CancellationToken cancellationToken) =>
            RunImageUploadOperationAsync(
                () => _imageUploadService.UploadPublicPageAssetAsync(
                    TenantPublicAssetType.Logo,
                    file,
                    CurrentUserId,
                    cancellationToken,
                    crop),
                "Logo actualizado.",
                cancellationToken);

        [HttpPost("RemoveLogo")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> RemoveLogo(CancellationToken cancellationToken) =>
            RunImageRemovalOperationAsync(
                () => _imageUploadService.RemovePublicPageSingletonAsync(
                    TenantPublicAssetType.Logo,
                    CurrentUserId,
                    cancellationToken),
                "Logo removido.",
                cancellationToken);

        [HttpPost("UploadCover")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> UploadCover(
            IFormFile? file,
            PublicImageCropRequest crop,
            CancellationToken cancellationToken) =>
            RunImageUploadOperationAsync(
                () => _imageUploadService.UploadPublicPageAssetAsync(
                    TenantPublicAssetType.Cover,
                    file,
                    CurrentUserId,
                    cancellationToken,
                    crop),
                "Portada actualizada.",
                cancellationToken);

        [HttpPost("RemoveCover")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> RemoveCover(CancellationToken cancellationToken) =>
            RunImageRemovalOperationAsync(
                () => _imageUploadService.RemovePublicPageSingletonAsync(
                    TenantPublicAssetType.Cover,
                    CurrentUserId,
                    cancellationToken),
                "Portada removida.",
                cancellationToken);

        [HttpPost("UploadLocationImage")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> UploadLocationImage(
            IFormFile? file,
            PublicImageCropRequest crop,
            CancellationToken cancellationToken) =>
            RunImageUploadOperationAsync(
                () => _imageUploadService.UploadPublicPageAssetAsync(
                    TenantPublicAssetType.Location,
                    file,
                    CurrentUserId,
                    cancellationToken,
                    crop),
                "Imagen de ubicacion actualizada.",
                cancellationToken);

        [HttpPost("RemoveLocationImage")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> RemoveLocationImage(CancellationToken cancellationToken) =>
            RunImageRemovalOperationAsync(
                () => _imageUploadService.RemovePublicPageSingletonAsync(
                    TenantPublicAssetType.Location,
                    CurrentUserId,
                    cancellationToken),
                "Imagen de ubicacion removida.",
                cancellationToken);

        [HttpPost("UploadBusinessGalleryImage")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> UploadBusinessGalleryImage(
            IFormFile? file,
            PublicImageCropRequest crop,
            CancellationToken cancellationToken) =>
            RunImageUploadOperationAsync(
                () => _imageUploadService.UploadPublicPageAssetAsync(
                    TenantPublicAssetType.BusinessGallery,
                    file,
                    CurrentUserId,
                    cancellationToken,
                    crop),
                "Imagen agregada a la galeria.",
                cancellationToken);

        [HttpPost("RemoveBusinessGalleryImage")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> RemoveBusinessGalleryImage(Guid assetId, CancellationToken cancellationToken) =>
            RunImageRemovalOperationAsync(
                () => _imageUploadService.RemoveAssetAsync(
                    assetId,
                    CurrentUserId,
                    cancellationToken),
                "Imagen removida de la galeria.",
                cancellationToken);

        [HttpPost("UploadServiceMainImage")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> UploadServiceMainImage(
            int serviceId,
            IFormFile? file,
            PublicImageCropRequest crop,
            CancellationToken cancellationToken) =>
            RunImageUploadOperationAsync(
                () => _imageUploadService.UploadServiceAssetAsync(
                    TenantPublicAssetType.ServiceMain,
                    serviceId,
                    file,
                    CurrentUserId,
                    cancellationToken,
                    crop),
                "Imagen principal del servicio actualizada.",
                cancellationToken);

        [HttpPost("RemoveServiceMainImage")]
        [RequirePermission(AppPermissions.PublicWebsiteManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> RemoveServiceMainImage(
            int serviceId,
            CancellationToken cancellationToken) =>
            RunImageRemovalOperationAsync(
                () => _imageUploadService.RemoveServiceMainImageAsync(
                    serviceId,
                    CurrentUserId,
                    cancellationToken),
                "Imagen principal del servicio removida.",
                cancellationToken);

        private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

        private async Task<IActionResult> RunImageUploadOperationAsync(
            Func<Task<TenantPublicAsset>> operation,
            string successMessage,
            CancellationToken cancellationToken)
        {
            try
            {
                var asset = await operation();
                if (WantsJson())
                {
                    return Json(await BuildImageJsonResponseAsync(asset, successMessage, cancellationToken));
                }

                TempData["PaginaPublicaOk"] = successMessage;
            }
            catch (PublicImageUploadException ex)
            {
                if (WantsJson())
                {
                    return Json(new { success = false, message = ex.Message });
                }

                TempData["PaginaPublicaError"] = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al procesar imagen publica del tenant.");
                if (WantsJson())
                {
                    return Json(new { success = false, message = "No fue posible procesar la imagen." });
                }

                TempData["PaginaPublicaError"] = "No fue posible procesar la imagen.";
            }

            return RedirectToAction(nameof(Index));
        }

        private async Task<IActionResult> RunImageRemovalOperationAsync(
            Func<Task> operation,
            string successMessage,
            CancellationToken cancellationToken)
        {
            try
            {
                await operation();
                if (WantsJson())
                {
                    return Json(await BuildImageJsonResponseAsync(null, successMessage, cancellationToken));
                }

                TempData["PaginaPublicaOk"] = successMessage;
            }
            catch (PublicImageUploadException ex)
            {
                if (WantsJson())
                {
                    return Json(new { success = false, message = ex.Message });
                }

                TempData["PaginaPublicaError"] = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al procesar imagen publica del tenant.");
                if (WantsJson())
                {
                    return Json(new { success = false, message = "No fue posible procesar la imagen." });
                }

                TempData["PaginaPublicaError"] = "No fue posible procesar la imagen.";
            }

            return RedirectToAction(nameof(Index));
        }

        private async Task<object> BuildImageJsonResponseAsync(
            TenantPublicAsset? asset,
            string message,
            CancellationToken cancellationToken)
        {
            var model = await _settingsService.BuildForCurrentTenantAsync(Request, cancellationToken);
            var usagePercent = Math.Min(model.StorageUsage.PercentUsed, 100);

            return new
            {
                success = true,
                assetId = asset?.Id,
                publicUrl = asset?.PublicUrl,
                width = asset?.Width,
                height = asset?.Height,
                sizeLabel = asset is null ? null : FormatBytes(asset.SizeBytes),
                serviceId = asset?.ServicioId,
                usageLabel = $"{model.StorageUsage.UsedDisplay} de {model.StorageUsage.MaxDisplay} usados",
                usagePercent,
                businessGalleryCount = model.BusinessGallery.Count,
                maxBusinessGalleryImages = model.MaxBusinessGalleryImages,
                message
            };
        }

        private bool WantsJson()
        {
            return string.Equals(
                       Request.Headers["X-Requested-With"],
                       "XMLHttpRequest",
                       StringComparison.OrdinalIgnoreCase) ||
                   Request.Headers.Accept.Any(value =>
                       value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return $"{bytes} B";
            }

            var kb = bytes / 1024m;
            if (kb < 1024)
            {
                return $"{kb:0.#} KB";
            }

            return $"{kb / 1024m:0.#} MB";
        }
    }
}
