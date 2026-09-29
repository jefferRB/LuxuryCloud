using LuxuryApp.Services.Identity;
using LuxuryApp.Services.PublicImages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Platform
{
    /// <summary>
    /// Healthcheck manual del storage publico R2 (S3-compatible). SOLO plataforma (super admin).
    /// Sube un objeto de diagnostico pequeno a R2 con el modo de compatibilidad v2 y verifica su
    /// lectura publica via PublicBaseUrl. No expone secretos. Es una utilidad de diagnostico; se
    /// puede quitar una vez confirmado el fix en produccion.
    /// </summary>
    [Authorize(Policy = PlatformAuthorizationPolicies.PlatformSuperAdmin)]
    [Route("Platform/StorageDiagnostics")]
    public sealed class PlatformStorageDiagnosticsController : Controller
    {
        private readonly S3CompatiblePublicImageStorageService _s3Storage;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<PlatformStorageDiagnosticsController> _logger;

        public PlatformStorageDiagnosticsController(
            S3CompatiblePublicImageStorageService s3Storage,
            IHttpClientFactory httpClientFactory,
            ILogger<PlatformStorageDiagnosticsController> logger)
        {
            _s3Storage = s3Storage;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        [HttpGet("R2HealthCheck")]
        public async Task<IActionResult> R2HealthCheck(CancellationToken cancellationToken)
        {
            string? publicUrl = null;
            try
            {
                publicUrl = await _s3Storage.UploadHealthCheckObjectAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "R2 healthcheck: fallo la subida de diagnostico.");
                return Json(new
                {
                    uploadOk = false,
                    getOk = false,
                    getStatusCode = (int?)null,
                    publicUrl,
                    error = ex.Message
                });
            }

            int? getStatusCode = null;
            var getOk = false;
            string? getError = null;
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var response = await client.GetAsync(publicUrl, cancellationToken);
                getStatusCode = (int)response.StatusCode;
                getOk = response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                getError = ex.Message;
                _logger.LogWarning(ex, "R2 healthcheck: subio pero fallo el GET publico de {PublicUrl}.", publicUrl);
            }

            return Json(new
            {
                uploadOk = true,
                getOk,
                getStatusCode,
                publicUrl,
                error = getError
            });
        }
    }
}
