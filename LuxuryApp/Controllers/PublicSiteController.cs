using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.PublicPages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Controllers
{
    [AllowAnonymous]
    [EnableRateLimiting("PublicBooking")]
    [Route("sitio")]
    public sealed class PublicSiteController : Controller
    {
        private readonly ITenantPublicPageQueryService _publicPageQueryService;
        private readonly ITenantPublicPageAnalyticsService _analyticsService;
        private readonly ITenantPublicPageRedirectService _redirectService;
        private readonly PublicImageOptions _publicImageOptions;
        private readonly S3StorageOptions _s3StorageOptions;

        public PublicSiteController(
            ITenantPublicPageQueryService publicPageQueryService,
            ITenantPublicPageAnalyticsService analyticsService,
            ITenantPublicPageRedirectService redirectService,
            IOptions<PublicImageOptions> publicImageOptions,
            IOptions<S3StorageOptions> s3StorageOptions)
        {
            _publicPageQueryService = publicPageQueryService;
            _analyticsService = analyticsService;
            _redirectService = redirectService;
            _publicImageOptions = publicImageOptions.Value;
            _s3StorageOptions = s3StorageOptions.Value;
        }

        [HttpGet("{slug}")]
        public async Task<IActionResult> Index(string slug, CancellationToken cancellationToken)
        {
            ApplyPublicSecurityHeaders();

            var model = await _publicPageQueryService.GetBySlugAsync(slug, cancellationToken);
            if (model is null)
            {
                Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                return NotFound();
            }

            await _analyticsService.TryTrackCurrentTenantAsync(
                model.Slug,
                TenantPublicPageMetricType.PageView,
                cancellationToken: cancellationToken);

            return View(model);
        }

        [HttpGet("{slug}/go/reservar")]
        public async Task<IActionResult> GoReserve(string slug, CancellationToken cancellationToken)
        {
            ApplyPublicSecurityHeaders();

            var target = await _redirectService.ResolveReserveAsync(slug, Request, cancellationToken);
            return target is null ? NotFound() : Redirect(target.Url);
        }

        [HttpGet("{slug}/go/servicio/{servicioId:int}/reservar")]
        public async Task<IActionResult> GoServiceReserve(
            string slug,
            int servicioId,
            CancellationToken cancellationToken)
        {
            ApplyPublicSecurityHeaders();

            var target = await _redirectService.ResolveServiceReserveAsync(
                slug,
                servicioId,
                Request,
                cancellationToken);

            return target is null ? NotFound() : Redirect(target.Url);
        }

        [HttpGet("{slug}/go/whatsapp")]
        public async Task<IActionResult> GoWhatsApp(string slug, CancellationToken cancellationToken)
        {
            ApplyPublicSecurityHeaders();

            var target = await _redirectService.ResolveWhatsAppAsync(slug, cancellationToken);
            return target is null ? NotFound() : Redirect(target.Url);
        }

        [HttpGet("{slug}/go/maps")]
        public async Task<IActionResult> GoMaps(string slug, CancellationToken cancellationToken)
        {
            ApplyPublicSecurityHeaders();

            var target = await _redirectService.ResolveMapsAsync(slug, cancellationToken);
            return target is null ? NotFound() : Redirect(target.Url);
        }

        [HttpGet("{slug}/go/waze")]
        public async Task<IActionResult> GoWaze(string slug, CancellationToken cancellationToken)
        {
            ApplyPublicSecurityHeaders();

            var target = await _redirectService.ResolveWazeAsync(slug, cancellationToken);
            return target is null ? NotFound() : Redirect(target.Url);
        }

        private void ApplyPublicSecurityHeaders()
        {
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            Response.Headers["Permissions-Policy"] = "camera=(), microphone=()";
            Response.Headers["Content-Security-Policy"] = BuildContentSecurityPolicy();
        }

        private string BuildContentSecurityPolicy()
        {
            var imageSources = new List<string> { "'self'", "data:" };
            AddOrigin(imageSources, _publicImageOptions.CdnBaseUrl);
            AddOrigin(imageSources, _s3StorageOptions.PublicBaseUrl);

            return string.Join("; ", new[]
            {
                "default-src 'self'",
                $"img-src {string.Join(' ', imageSources.Distinct(StringComparer.OrdinalIgnoreCase))}",
                "script-src 'self'",
                "style-src 'self' 'unsafe-inline'",
                "object-src 'none'",
                "base-uri 'self'",
                "frame-ancestors 'none'"
            });
        }

        private static void AddOrigin(List<string> sources, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                return;
            }

            sources.Add(uri.GetLeftPart(UriPartial.Authority));
        }
    }
}
