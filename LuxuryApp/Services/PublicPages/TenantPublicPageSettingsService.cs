using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Services.Tenant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.PublicPages
{
    public sealed class TenantPublicPageSettingsService : ITenantPublicPageSettingsService
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantProvider _tenantProvider;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;
        private readonly IPublicUrlValidationService _urlValidationService;
        private readonly IPublicAssetQuotaService _quotaService;
        private readonly ITenantPublicPageAnalyticsService _analyticsService;
        private readonly IBusinessScheduleService _businessScheduleService;
        private readonly PublicImageOptions _imageOptions;

        public TenantPublicPageSettingsService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            ITenantDisplayNameService tenantDisplayNameService,
            IPublicUrlValidationService urlValidationService,
            IPublicAssetQuotaService quotaService,
            ITenantPublicPageAnalyticsService analyticsService,
            IBusinessScheduleService businessScheduleService,
            IOptions<PublicImageOptions> imageOptions)
        {
            _context = context;
            _tenantProvider = tenantProvider;
            _tenantDisplayNameService = tenantDisplayNameService;
            _urlValidationService = urlValidationService;
            _quotaService = quotaService;
            _analyticsService = analyticsService;
            _businessScheduleService = businessScheduleService;
            _imageOptions = imageOptions.Value;
        }

        public async Task<EditTenantPublicPageViewModel> BuildForCurrentTenantAsync(
            HttpRequest? request,
            CancellationToken cancellationToken = default)
        {
            var tenantId = _tenantProvider.GetTenantId();
            var businessName = await _tenantDisplayNameService.GetTenantDisplayNameAsync(
                tenantId,
                cancellationToken);

            var page = await _context.TenantPublicPages
                .FirstOrDefaultAsync(cancellationToken);

            if (page is null)
            {
                page = new TenantPublicPage
                {
                    HeroTitle = businessName,
                    ShowServices = true,
                    ShowPrices = true,
                    ShowLocation = true,
                    ShowWhatsAppButton = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };

                _context.TenantPublicPages.Add(page);
                await _context.SaveChangesAsync(cancellationToken);
            }

            return await MapEditViewModelAsync(page, businessName, request, cancellationToken);
        }

        public async Task<EditTenantPublicPageViewModel> PopulateReadOnlyFieldsAsync(
            EditTenantPublicPageViewModel model,
            HttpRequest? request,
            CancellationToken cancellationToken = default)
        {
            var tenantId = _tenantProvider.GetTenantId();
            model.BusinessName = await _tenantDisplayNameService.GetTenantDisplayNameAsync(
                tenantId,
                cancellationToken);

            var links = await ResolveLinksAsync(request, cancellationToken);
            model.PublicSiteUrl = links.PublicSiteUrl;
            model.BookingUrl = links.BookingUrl;
            model.BookingEnabled = links.BookingEnabled;
            model.CanUsePublicLandingPage = await CanUsePublicLandingPageAsync(tenantId, cancellationToken);
            model.Analytics = await _analyticsService.GetLast30DaysForCurrentTenantAsync(cancellationToken);

            await PopulateAssetFieldsAsync(model, tenantId, cancellationToken);
            return model;
        }

        public async Task SaveForCurrentTenantAsync(
            EditTenantPublicPageViewModel input,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            if (input is null)
            {
                throw new TenantPublicPageValidationException("Los datos de la pagina publica no son validos.");
            }

            var tenantId = _tenantProvider.GetTenantId();
            if (!await CanUsePublicLandingPageAsync(tenantId, cancellationToken))
            {
                throw new TenantPublicPageValidationException(
                    "El tenant actual no puede usar la pagina publica en este momento.");
            }

            var page = await _context.TenantPublicPages
                .FirstOrDefaultAsync(cancellationToken);

            var now = DateTime.UtcNow;
            if (page is null)
            {
                page = new TenantPublicPage
                {
                    CreatedAtUtc = now
                };
                _context.TenantPublicPages.Add(page);
            }

            page.IsPublished = input.IsPublished;
            page.PublicBusinessName = _urlValidationService.NormalizePlainText(
                input.PublicBusinessName,
                120,
                nameof(EditTenantPublicPageViewModel.PublicBusinessName));
            page.HeroTitle = _urlValidationService.NormalizePlainText(
                input.HeroTitle,
                120,
                nameof(EditTenantPublicPageViewModel.HeroTitle));
            page.HeroSubtitle = _urlValidationService.NormalizePlainText(
                input.HeroSubtitle,
                180,
                nameof(EditTenantPublicPageViewModel.HeroSubtitle));
            page.HeroEyebrow = _urlValidationService.NormalizePlainText(
                input.HeroEyebrow,
                80,
                nameof(EditTenantPublicPageViewModel.HeroEyebrow));
            page.Description = _urlValidationService.NormalizePlainText(
                input.Description,
                1500,
                nameof(EditTenantPublicPageViewModel.Description));
            page.LogoUrl = null;
            page.CoverImageUrl = null;
            page.AccentColorHex = NormalizeAccentColor(input.AccentColorHex);
            page.Phone = _urlValidationService.NormalizePhone(
                input.Phone,
                30,
                nameof(EditTenantPublicPageViewModel.Phone));
            page.WhatsAppPhone = _urlValidationService.NormalizeWhatsAppPhone(
                input.WhatsAppPhone,
                nameof(EditTenantPublicPageViewModel.WhatsAppPhone));
            page.Email = _urlValidationService.NormalizeEmail(
                input.Email,
                nameof(EditTenantPublicPageViewModel.Email));
            page.Address = _urlValidationService.NormalizePlainText(
                input.Address,
                300,
                nameof(EditTenantPublicPageViewModel.Address));
            page.BusinessHours = _urlValidationService.NormalizeMultilinePlainText(
                input.BusinessHours,
                500,
                nameof(EditTenantPublicPageViewModel.BusinessHours));
            // Horario estructurado (doble turno por dia). Puede lanzar validacion.
            page.BusinessHoursJson = _businessScheduleService.Serialize(
                _businessScheduleService.BuildFromInputs(input.ScheduleDays));
            page.GoogleMapsUrl = _urlValidationService.NormalizeGoogleMapsUrl(
                input.GoogleMapsUrl,
                nameof(EditTenantPublicPageViewModel.GoogleMapsUrl));
            page.WazeUrl = _urlValidationService.NormalizeWazeUrl(
                input.WazeUrl,
                nameof(EditTenantPublicPageViewModel.WazeUrl));
            page.InstagramUrl = _urlValidationService.NormalizeInstagramUrl(
                input.InstagramUrl,
                nameof(EditTenantPublicPageViewModel.InstagramUrl));
            page.FacebookUrl = _urlValidationService.NormalizeFacebookUrl(
                input.FacebookUrl,
                nameof(EditTenantPublicPageViewModel.FacebookUrl));
            page.TikTokUrl = _urlValidationService.NormalizeTikTokUrl(
                input.TikTokUrl,
                nameof(EditTenantPublicPageViewModel.TikTokUrl));
            page.ShowServices = input.ShowServices;
            page.ShowPrices = input.ShowPrices;
            page.ShowTeam = input.ShowTeam;
            page.ShowLocation = input.ShowLocation;
            page.ShowWhatsAppButton = input.ShowWhatsAppButton;
            page.SeoTitle = _urlValidationService.NormalizePlainText(
                input.SeoTitle,
                70,
                nameof(EditTenantPublicPageViewModel.SeoTitle));
            page.SeoDescription = _urlValidationService.NormalizePlainText(
                input.SeoDescription,
                180,
                nameof(EditTenantPublicPageViewModel.SeoDescription));
            page.UpdatedAtUtc = now;

            await _context.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// Solo se persiste un #RRGGBB valido. Vacio se guarda como NULL (= negro por defecto);
        /// cualquier otra cosa se rechaza en vez de llegar a la hoja de estilos publica.
        /// </summary>
        private static string? NormalizeAccentColor(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return PublicBrandTheme.Normalize(value)
                ?? throw new TenantPublicPageValidationException(
                    "El color principal debe tener el formato #RRGGBB.",
                    nameof(EditTenantPublicPageViewModel.AccentColorHex));
        }

        public async Task<bool> CanUsePublicLandingPageAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            if (tenantId == Guid.Empty)
            {
                return false;
            }

            // Punto de extension para monetizacion por plan. En Fase 2, cualquier tenant
            // activo puede usar la landing publica.
            return await _context.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(tenant => tenant.Id == tenantId && tenant.Activo, cancellationToken);
        }

        private async Task<EditTenantPublicPageViewModel> MapEditViewModelAsync(
            TenantPublicPage page,
            string businessName,
            HttpRequest? request,
            CancellationToken cancellationToken)
        {
            var links = await ResolveLinksAsync(request, cancellationToken);
            var tenantId = _tenantProvider.GetTenantId();

            var model = new EditTenantPublicPageViewModel
            {
                IsPublished = page.IsPublished,
                PublicBusinessName = page.PublicBusinessName,
                HeroTitle = page.HeroTitle,
                HeroSubtitle = page.HeroSubtitle,
                HeroEyebrow = page.HeroEyebrow,
                Description = page.Description,
                AccentColorHex = page.AccentColorHex,
                Phone = page.Phone,
                WhatsAppPhone = page.WhatsAppPhone,
                Email = page.Email,
                Address = page.Address,
                BusinessHours = page.BusinessHours,
                ScheduleDays = _businessScheduleService
                    .BuildInputs(_businessScheduleService.TryDeserialize(page.BusinessHoursJson))
                    .ToList(),
                GoogleMapsUrl = page.GoogleMapsUrl,
                WazeUrl = page.WazeUrl,
                InstagramUrl = page.InstagramUrl,
                FacebookUrl = page.FacebookUrl,
                TikTokUrl = page.TikTokUrl,
                ShowServices = page.ShowServices,
                ShowPrices = page.ShowPrices,
                ShowTeam = page.ShowTeam,
                ShowLocation = page.ShowLocation,
                ShowWhatsAppButton = page.ShowWhatsAppButton,
                SeoTitle = page.SeoTitle,
                SeoDescription = page.SeoDescription,
                BusinessName = businessName,
                PublicSiteUrl = links.PublicSiteUrl,
                BookingUrl = links.BookingUrl,
                BookingEnabled = links.BookingEnabled,
                CanUsePublicLandingPage = await CanUsePublicLandingPageAsync(tenantId, cancellationToken),
                Analytics = await _analyticsService.GetLast30DaysForCurrentTenantAsync(cancellationToken)
            };

            await PopulateAssetFieldsAsync(model, tenantId, cancellationToken);
            return model;
        }

        private async Task PopulateAssetFieldsAsync(
            EditTenantPublicPageViewModel model,
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            var assets = await _context.TenantPublicAssets
                .AsNoTracking()
                .Where(asset => asset.TenantId == tenantId &&
                                asset.IsActive &&
                                asset.DeletedAtUtc == null)
                .OrderBy(asset => asset.SortOrder)
                .ThenBy(asset => asset.CreatedAtUtc)
                .ToListAsync(cancellationToken);

            var usage = await _quotaService.GetUsageAsync(tenantId, cancellationToken);
            model.StorageUsage = new PublicAssetUsageViewModel
            {
                UsedBytes = usage.UsedBytes,
                MaxBytes = usage.MaxBytes
            };
            model.MaxBusinessGalleryImages = _imageOptions.MaxBusinessGalleryImages;
            model.LogoAsset = assets
                .Where(asset => asset.AssetType == TenantPublicAssetType.Logo)
                .Select(MapAdminAsset)
                .FirstOrDefault();
            model.CoverAsset = assets
                .Where(asset => asset.AssetType == TenantPublicAssetType.Cover)
                .Select(MapAdminAsset)
                .FirstOrDefault();
            model.LocationAsset = assets
                .Where(asset => asset.AssetType == TenantPublicAssetType.Location)
                .Select(MapAdminAsset)
                .FirstOrDefault();
            model.BusinessGallery = assets
                .Where(asset => asset.AssetType == TenantPublicAssetType.BusinessGallery)
                .Select(MapAdminAsset)
                .ToList();
            model.ServiceAssets = await BuildServiceAssetSettingsAsync(assets, cancellationToken);
        }

        private async Task<IReadOnlyList<PublicServiceAssetSettingsViewModel>> BuildServiceAssetSettingsAsync(
            IReadOnlyList<TenantPublicAsset> assets,
            CancellationToken cancellationToken)
        {
            var settings = await _context.TenantBookingServiceSettings
                .AsNoTracking()
                .Select(setting => new
                {
                    setting.ServicioId,
                    setting.IsVisibleOnline,
                    setting.PublicName,
                    setting.DisplayOrder
                })
                .ToListAsync(cancellationToken);

            var hasSettings = settings.Count > 0;
            var settingsByServiceId = settings.ToDictionary(setting => setting.ServicioId);

            var services = await _context.Servicios
                .AsNoTracking()
                .Where(service => service.Activo)
                .Select(service => new
                {
                    service.Id,
                    service.Nombre
                })
                .ToListAsync(cancellationToken);

            return services
                .Select(service =>
                {
                    settingsByServiceId.TryGetValue(service.Id, out var setting);
                    var displayName = FirstNonEmpty(setting?.PublicName, service.Nombre);
                    return new
                    {
                        service.Id,
                        Name = displayName,
                        IsVisible = !hasSettings || (setting is not null && setting.IsVisibleOnline),
                        Order = setting?.DisplayOrder ?? 0
                    };
                })
                .Where(item => item.IsVisible)
                .OrderBy(item => item.Order > 0 ? item.Order : int.MaxValue)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(service =>
                {
                    var serviceAssets = assets
                        .Where(asset => asset.ServicioId == service.Id)
                        .ToList();

                    return new PublicServiceAssetSettingsViewModel
                    {
                        ServiceId = service.Id,
                        ServiceName = service.Name,
                        MainImage = serviceAssets
                            .Where(asset => asset.AssetType == TenantPublicAssetType.ServiceMain)
                            .Select(MapAdminAsset)
                            .FirstOrDefault()
                    };
                })
                .ToList();
        }

        private async Task<PublicPageLinks> ResolveLinksAsync(
            HttpRequest? request,
            CancellationToken cancellationToken)
        {
            var booking = await _context.TenantBookingSettings
                .AsNoTracking()
                .Select(settings => new
                {
                    settings.PublicBookingSlug,
                    settings.PublicBookingEnabled
                })
                .FirstOrDefaultAsync(cancellationToken);

            var publicSiteUrl = TenantPublicPageLinkBuilder.Build(request, booking?.PublicBookingSlug);
            var bookingUrl = booking?.PublicBookingEnabled == true
                ? BookingLinkBuilder.Build(request, booking.PublicBookingSlug)
                : null;

            return new PublicPageLinks(
                publicSiteUrl,
                bookingUrl,
                booking?.PublicBookingEnabled == true);
        }

        private static AdminPublicAssetImageViewModel MapAdminAsset(TenantPublicAsset asset) =>
            new()
            {
                Id = asset.Id,
                Url = asset.PublicUrl,
                Width = asset.Width,
                Height = asset.Height,
                SizeBytes = asset.SizeBytes,
                SortOrder = asset.SortOrder
            };

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private sealed record PublicPageLinks(
            string? PublicSiteUrl,
            string? BookingUrl,
            bool BookingEnabled);
    }
}
