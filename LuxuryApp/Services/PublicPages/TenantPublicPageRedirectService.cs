using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.Reservas;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.PublicPages
{
    public sealed class TenantPublicPageRedirectService : ITenantPublicPageRedirectService
    {
        private readonly ApplicationDbContext _context;
        private readonly IPublicUrlValidationService _urlValidationService;
        private readonly ITenantPublicPageAnalyticsService _analyticsService;

        public TenantPublicPageRedirectService(
            ApplicationDbContext context,
            IPublicUrlValidationService urlValidationService,
            ITenantPublicPageAnalyticsService analyticsService)
        {
            _context = context;
            _urlValidationService = urlValidationService;
            _analyticsService = analyticsService;
        }

        public async Task<PublicPageRedirectTarget?> ResolveReserveAsync(
            string slug,
            HttpRequest? request,
            CancellationToken cancellationToken = default)
        {
            var page = await ResolvePageAsync(slug, cancellationToken);
            if (page is null || !page.BookingEnabled)
            {
                return null;
            }

            var url = BookingLinkBuilder.Build(request, page.Slug);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            await _analyticsService.TryTrackAsync(
                page.TenantId,
                page.Slug,
                TenantPublicPageMetricType.ReserveClick,
                cancellationToken: cancellationToken);

            return new PublicPageRedirectTarget(url);
        }

        public async Task<PublicPageRedirectTarget?> ResolveServiceReserveAsync(
            string slug,
            int servicioId,
            HttpRequest? request,
            CancellationToken cancellationToken = default)
        {
            var page = await ResolvePageAsync(slug, cancellationToken);
            if (page is null || !page.BookingEnabled)
            {
                return null;
            }

            var isVisible = await IsServiceVisibleOnlineAsync(page.TenantId, servicioId, cancellationToken);
            var url = isVisible
                ? BookingLinkBuilder.BuildForService(request, page.Slug, servicioId)
                : BookingLinkBuilder.Build(request, page.Slug);

            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            await _analyticsService.TryTrackAsync(
                page.TenantId,
                page.Slug,
                isVisible ? TenantPublicPageMetricType.ServiceReserveClick : TenantPublicPageMetricType.ReserveClick,
                isVisible ? servicioId : null,
                cancellationToken);

            return new PublicPageRedirectTarget(url);
        }

        public async Task<PublicPageRedirectTarget?> ResolveWhatsAppAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            var page = await ResolvePageAsync(slug, cancellationToken);
            if (page is null || !page.ShowWhatsAppButton)
            {
                return null;
            }

            var url = _urlValidationService.BuildWhatsAppUrl(page.WhatsAppPhone);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            await _analyticsService.TryTrackAsync(
                page.TenantId,
                page.Slug,
                TenantPublicPageMetricType.WhatsAppClick,
                cancellationToken: cancellationToken);

            return new PublicPageRedirectTarget(url);
        }

        public async Task<PublicPageRedirectTarget?> ResolveMapsAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            var page = await ResolvePageAsync(slug, cancellationToken);
            var mapsUrl = NormalizeMapsRedirectUrl(page?.GoogleMapsUrl);
            if (page is null || !page.ShowLocation || string.IsNullOrWhiteSpace(mapsUrl))
            {
                return null;
            }

            await _analyticsService.TryTrackAsync(
                page.TenantId,
                page.Slug,
                TenantPublicPageMetricType.MapsClick,
                cancellationToken: cancellationToken);

            return new PublicPageRedirectTarget(mapsUrl);
        }

        public async Task<PublicPageRedirectTarget?> ResolveWazeAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            var page = await ResolvePageAsync(slug, cancellationToken);
            var wazeUrl = NormalizeWazeRedirectUrl(page?.WazeUrl);
            if (page is null || !page.ShowLocation || string.IsNullOrWhiteSpace(wazeUrl))
            {
                return null;
            }

            await _analyticsService.TryTrackAsync(
                page.TenantId,
                page.Slug,
                TenantPublicPageMetricType.MapsClick,
                cancellationToken: cancellationToken);

            return new PublicPageRedirectTarget(wazeUrl);
        }

        private async Task<PublicRedirectPageRecord?> ResolvePageAsync(
            string slug,
            CancellationToken cancellationToken)
        {
            var normalized = BookingSettingsService.NormalizeSlug(slug);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var page = await (
                from booking in _context.TenantBookingSettings.IgnoreQueryFilters().AsNoTracking()
                join tenant in _context.Tenants.IgnoreQueryFilters().AsNoTracking()
                    on booking.TenantId equals tenant.Id
                join publicPage in _context.TenantPublicPages.IgnoreQueryFilters().AsNoTracking()
                    on booking.TenantId equals publicPage.TenantId
                where booking.PublicBookingSlug == normalized
                select new PublicRedirectPageRecord
                {
                    TenantId = tenant.Id,
                    Slug = booking.PublicBookingSlug!,
                    TenantActive = tenant.Activo,
                    IsPublished = publicPage.IsPublished,
                    BookingEnabled = booking.PublicBookingEnabled,
                    ShowWhatsAppButton = publicPage.ShowWhatsAppButton,
                    ShowLocation = publicPage.ShowLocation,
                    WhatsAppPhone = publicPage.WhatsAppPhone,
                    GoogleMapsUrl = publicPage.GoogleMapsUrl,
                    WazeUrl = publicPage.WazeUrl
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (page is null || !page.TenantActive || !page.IsPublished)
            {
                return null;
            }

            return page;
        }

        private async Task<bool> IsServiceVisibleOnlineAsync(
            Guid tenantId,
            int servicioId,
            CancellationToken cancellationToken)
        {
            if (servicioId <= 0)
            {
                return false;
            }

            var serviceIsActive = await _context.Servicios
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(service =>
                    service.TenantId == tenantId &&
                    service.Id == servicioId &&
                    service.Activo,
                    cancellationToken);

            if (!serviceIsActive)
            {
                return false;
            }

            var hasSettings = await _context.TenantBookingServiceSettings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(setting => setting.TenantId == tenantId, cancellationToken);

            if (!hasSettings)
            {
                return true;
            }

            return await _context.TenantBookingServiceSettings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(setting =>
                    setting.TenantId == tenantId &&
                    setting.ServicioId == servicioId &&
                    setting.IsVisibleOnline,
                    cancellationToken);
        }

        private string? NormalizeMapsRedirectUrl(string? value)
        {
            try
            {
                return _urlValidationService.NormalizeGoogleMapsUrl(value, nameof(PublicRedirectPageRecord.GoogleMapsUrl));
            }
            catch (TenantPublicPageValidationException)
            {
                return null;
            }
        }

        private string? NormalizeWazeRedirectUrl(string? value)
        {
            try
            {
                return _urlValidationService.NormalizeWazeUrl(value, nameof(PublicRedirectPageRecord.WazeUrl));
            }
            catch (TenantPublicPageValidationException)
            {
                return null;
            }
        }

        private sealed class PublicRedirectPageRecord
        {
            public Guid TenantId { get; init; }
            public string Slug { get; init; } = string.Empty;
            public bool TenantActive { get; init; }
            public bool IsPublished { get; init; }
            public bool BookingEnabled { get; init; }
            public bool ShowWhatsAppButton { get; init; }
            public bool ShowLocation { get; init; }
            public string? WhatsAppPhone { get; init; }
            public string? GoogleMapsUrl { get; init; }
            public string? WazeUrl { get; init; }
        }
    }
}
