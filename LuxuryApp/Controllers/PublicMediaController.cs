using LuxuryApp.Services.PublicImages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers
{
    [AllowAnonymous]
    [Route("public-media")]
    public sealed class PublicMediaController : Controller
    {
        private readonly LocalPublicImageStorageService _localStorage;

        public PublicMediaController(LocalPublicImageStorageService localStorage)
        {
            _localStorage = localStorage;
        }

        [HttpGet("{**storageKey}")]
        public IActionResult Get(string? storageKey)
        {
            if (string.IsNullOrWhiteSpace(storageKey) ||
                !_localStorage.TryResolveLocalPath(storageKey, out var absolutePath) ||
                !System.IO.File.Exists(absolutePath))
            {
                return NotFound();
            }

            Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return PhysicalFile(absolutePath, "image/webp", enableRangeProcessing: true);
        }
    }
}
