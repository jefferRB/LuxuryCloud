using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Services.Tenant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.PublicPages
{
    public sealed class TenantPublicPageQueryService : ITenantPublicPageQueryService
    {
        private const string ResolvedTenantItemKey = "__resolved_tenant_id";

        private readonly ApplicationDbContext _context;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IPublicUrlValidationService _urlValidationService;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;
        private readonly IBusinessScheduleService _businessScheduleService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public TenantPublicPageQueryService(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            IPublicUrlValidationService urlValidationService,
            ITenantDisplayNameService tenantDisplayNameService,
            IBusinessScheduleService businessScheduleService,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
            _urlValidationService = urlValidationService;
            _tenantDisplayNameService = tenantDisplayNameService;
            _businessScheduleService = businessScheduleService;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task<TenantPublicPageViewModel?> GetBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            var normalized = BookingSettingsService.NormalizeSlug(slug);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var match = await (
                from booking in _context.TenantBookingSettings.IgnoreQueryFilters().AsNoTracking()
                join tenant in _context.Tenants.IgnoreQueryFilters().AsNoTracking()
                    on booking.TenantId equals tenant.Id
                join page in _context.TenantPublicPages.IgnoreQueryFilters().AsNoTracking()
                    on booking.TenantId equals page.TenantId
                where booking.PublicBookingSlug == normalized
                select new PublicPageRecord
                {
                    TenantId = tenant.Id,
                    TenantActive = tenant.Activo,
                    BusinessName = tenant.Nombre,
                    Slug = booking.PublicBookingSlug!,
                    BookingEnabled = booking.PublicBookingEnabled,
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
                    BusinessHoursJson = page.BusinessHoursJson,
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
                    SeoDescription = page.SeoDescription
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (match is null ||
                !match.TenantActive ||
                !match.IsPublished ||
                !await CanUsePublicLandingPageAsync(match.TenantId, cancellationToken))
            {
                return null;
            }

            SetResolvedTenant(match.TenantId);

            var assets = await LoadAssetsAsync(match.TenantId, cancellationToken);
            var request = _httpContextAccessor.HttpContext?.Request;
            var bookingUrl = match.BookingEnabled
                ? BookingLinkBuilder.Build(request, match.Slug)
                : null;
            var reserveActionUrl = match.BookingEnabled
                ? TenantPublicPageLinkBuilder.BuildReserveAction(request, match.Slug)
                : null;

            var services = match.ShowServices
                ? await LoadServicesAsync(
                    match.TenantId,
                    match.ShowPrices,
                    request,
                    match.Slug,
                    bookingUrl,
                    assets,
                    cancellationToken)
                : Array.Empty<PublicServiceCardViewModel>();

            var team = match.ShowTeam
                ? await LoadTeamAsync(match.TenantId, cancellationToken)
                : Array.Empty<PublicTeamMemberViewModel>();

            // Nombre del negocio: campo publico editable -> nombre visible de plataforma.
            var businessName = FirstNonEmpty(match.PublicBusinessName);
            if (string.IsNullOrEmpty(businessName))
            {
                businessName = await _tenantDisplayNameService.GetTenantDisplayNameAsync(
                    match.TenantId,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(businessName))
            {
                businessName = match.BusinessName;
            }

            var schedule = _businessScheduleService.BuildStatus(
                _businessScheduleService.TryDeserialize(match.BusinessHoursJson),
                _businessDateTimeProvider.Now());

            var heroTitle = FirstNonEmpty(match.HeroTitle, businessName);
            var description = NormalizeForDisplay(match.Description);
            var seoDescription = FirstNonEmpty(match.SeoDescription, description, match.HeroSubtitle, businessName);
            var whatsAppUrl = match.ShowWhatsAppButton
                ? _urlValidationService.BuildWhatsAppUrl(match.WhatsAppPhone)
                : null;
            var googleMapsUrl = NormalizeForDisplay(match.GoogleMapsUrl);
            var wazeUrl = NormalizeForDisplay(match.WazeUrl);
            var logo = assets
                .Where(asset => asset.AssetType == TenantPublicAssetType.Logo)
                .Select(asset => MapPublicAsset(asset, $"Logo de {businessName}"))
                .FirstOrDefault();
            var cover = assets
                .Where(asset => asset.AssetType == TenantPublicAssetType.Cover)
                .Select(asset => MapPublicAsset(asset, $"Portada de {businessName}"))
                .FirstOrDefault();
            var locationImage = assets
                .Where(asset => asset.AssetType == TenantPublicAssetType.Location)
                .Select(asset => MapPublicAsset(asset, $"Ubicacion de {businessName}"))
                .FirstOrDefault();

            return new TenantPublicPageViewModel
            {
                Slug = match.Slug,
                BusinessName = businessName,
                HeroTitle = heroTitle,
                HeroSubtitle = NormalizeForDisplay(match.HeroSubtitle),
                HeroEyebrow = NormalizeForDisplay(match.HeroEyebrow),
                Description = description,
                LogoImage = logo,
                CoverImage = cover,
                Theme = PublicBrandTheme.Resolve(match.AccentColorHex),
                Phone = NormalizeForDisplay(match.Phone),
                WhatsAppPhone = NormalizeForDisplay(match.WhatsAppPhone),
                WhatsAppUrl = whatsAppUrl,
                WhatsAppActionUrl = !string.IsNullOrWhiteSpace(whatsAppUrl)
                    ? TenantPublicPageLinkBuilder.BuildWhatsAppAction(request, match.Slug)
                    : null,
                Email = NormalizeForDisplay(match.Email),
                Address = NormalizeForDisplay(match.Address),
                BusinessHours = NormalizeForDisplay(match.BusinessHours),
                Schedule = schedule,
                LocationImage = locationImage,
                GoogleMapsUrl = googleMapsUrl,
                WazeUrl = wazeUrl,
                MapsActionUrl = match.ShowLocation && !string.IsNullOrWhiteSpace(googleMapsUrl)
                    ? TenantPublicPageLinkBuilder.BuildMapsAction(request, match.Slug)
                    : null,
                WazeActionUrl = match.ShowLocation && !string.IsNullOrWhiteSpace(wazeUrl)
                    ? TenantPublicPageLinkBuilder.BuildWazeAction(request, match.Slug)
                    : null,
                InstagramUrl = NormalizeForDisplay(match.InstagramUrl),
                FacebookUrl = NormalizeForDisplay(match.FacebookUrl),
                TikTokUrl = NormalizeForDisplay(match.TikTokUrl),
                BookingUrl = bookingUrl,
                ReserveActionUrl = reserveActionUrl,
                PublicSiteUrl = TenantPublicPageLinkBuilder.Build(request, match.Slug),
                BookingEnabled = match.BookingEnabled,
                ShowLocation = match.ShowLocation,
                SeoTitle = FirstNonEmpty(match.SeoTitle, heroTitle, businessName),
                SeoDescription = seoDescription,
                BusinessGallery = assets
                    .Where(asset => asset.AssetType == TenantPublicAssetType.BusinessGallery)
                    .Select((asset, index) => MapPublicAsset(asset, $"{businessName} - imagen {index + 1}"))
                    .ToList(),
                Services = services,
                TeamMembers = team
            };
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
                .AnyAsync(t => t.Id == tenantId && t.Activo, cancellationToken);
        }

        private async Task<IReadOnlyList<PublicAssetRecord>> LoadAssetsAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            return await _context.TenantPublicAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => asset.TenantId == tenantId &&
                                asset.IsActive &&
                                asset.DeletedAtUtc == null)
                .OrderBy(asset => asset.SortOrder)
                .ThenBy(asset => asset.CreatedAtUtc)
                .Select(asset => new PublicAssetRecord
                {
                    AssetType = asset.AssetType,
                    ServicioId = asset.ServicioId,
                    Url = asset.PublicUrl,
                    Width = asset.Width,
                    Height = asset.Height,
                    SortOrder = asset.SortOrder,
                    CreatedAtUtc = asset.CreatedAtUtc
                })
                .ToListAsync(cancellationToken);
        }

        private async Task<IReadOnlyList<PublicServiceCardViewModel>> LoadServicesAsync(
            Guid tenantId,
            bool showPrices,
            HttpRequest? request,
            string slug,
            string? bookingUrl,
            IReadOnlyList<PublicAssetRecord> assets,
            CancellationToken cancellationToken)
        {
            var settings = await _context.TenantBookingServiceSettings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(setting => setting.TenantId == tenantId)
                .Select(setting => new
                {
                    setting.ServicioId,
                    setting.IsVisibleOnline,
                    setting.PublicName,
                    setting.PublicDescription,
                    setting.DisplayOrder,
                    setting.ShowPrice
                })
                .ToListAsync(cancellationToken);

            var hasSettings = settings.Count > 0;
            var settingsByServiceId = settings.ToDictionary(setting => setting.ServicioId);

            var services = await _context.Servicios
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(service => service.TenantId == tenantId && service.Activo)
                .Select(service => new
                {
                    service.Id,
                    service.Nombre,
                    service.DuracionMinutos,
                    service.Precio
                })
                .ToListAsync(cancellationToken);

            return services
                .Select(service =>
                {
                    settingsByServiceId.TryGetValue(service.Id, out var setting);
                    return new
                    {
                        Service = service,
                        Setting = setting,
                        IsVisible = !hasSettings || (setting is not null && setting.IsVisibleOnline)
                    };
                })
                .Where(item => item.IsVisible)
                .Select(item =>
                {
                    var publicName = FirstNonEmpty(item.Setting?.PublicName, item.Service.Nombre);
                    var canShowPrice = showPrices && (!hasSettings || item.Setting?.ShowPrice == true);
                    var serviceAssets = assets
                        .Where(asset => asset.ServicioId == item.Service.Id)
                        .ToList();

                    return new
                    {
                        Card = new PublicServiceCardViewModel
                        {
                            ServiceId = item.Service.Id,
                            Name = publicName,
                            Description = NormalizeForDisplay(item.Setting?.PublicDescription),
                            DurationMinutes = item.Service.DuracionMinutos,
                            Price = canShowPrice ? item.Service.Precio : null,
                            BookingUrl = !string.IsNullOrWhiteSpace(bookingUrl)
                                ? BookingLinkBuilder.BuildForService(request, slug, item.Service.Id)
                                : null,
                            ReserveActionUrl = !string.IsNullOrWhiteSpace(bookingUrl)
                                ? TenantPublicPageLinkBuilder.BuildServiceReserveAction(request, slug, item.Service.Id)
                                : null,
                            MainImage = serviceAssets
                                .Where(asset => asset.AssetType == TenantPublicAssetType.ServiceMain)
                                .Select(asset => MapPublicAsset(asset, publicName))
                                .FirstOrDefault()
                        },
                        Order = item.Setting?.DisplayOrder ?? 0,
                        Name = publicName
                    };
                })
                .OrderBy(item => item.Order > 0 ? item.Order : int.MaxValue)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => item.Card)
                .ToList();
        }

        private async Task<IReadOnlyList<PublicTeamMemberViewModel>> LoadTeamAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            return await _context.Funcionarios
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(funcionario => funcionario.TenantId == tenantId && funcionario.Activo)
                .OrderBy(funcionario => funcionario.Nombre)
                .Select(funcionario => new PublicTeamMemberViewModel
                {
                    Name = funcionario.Nombre,
                    Specialty = funcionario.Puesto != null ? funcionario.Puesto.NombrePuesto : null,
                    Description = funcionario.DescripcionPublica,
                    PhotoUrl = funcionario.MostrarFotoEnReservas ? funcionario.FotoUrl : null
                })
                .ToListAsync(cancellationToken);
        }

        private void SetResolvedTenant(Guid tenantId)
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext is not null && tenantId != Guid.Empty)
            {
                httpContext.Items[ResolvedTenantItemKey] = tenantId;
            }
        }

        private static PublicImageAssetViewModel MapPublicAsset(
            PublicAssetRecord asset,
            string altText) =>
            new()
            {
                Url = asset.Url,
                Width = asset.Width,
                Height = asset.Height,
                AltText = altText
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

        private static string? NormalizeForDisplay(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private sealed class PublicPageRecord
        {
            public Guid TenantId { get; init; }
            public bool TenantActive { get; init; }
            public string BusinessName { get; init; } = string.Empty;
            public string Slug { get; init; } = string.Empty;
            public bool BookingEnabled { get; init; }
            public bool IsPublished { get; init; }
            public string? PublicBusinessName { get; init; }
            public string? HeroTitle { get; init; }
            public string? HeroSubtitle { get; init; }
            public string? HeroEyebrow { get; init; }
            public string? Description { get; init; }
            public string? AccentColorHex { get; init; }
            public string? Phone { get; init; }
            public string? WhatsAppPhone { get; init; }
            public string? Email { get; init; }
            public string? Address { get; init; }
            public string? BusinessHours { get; init; }
            public string? BusinessHoursJson { get; init; }
            public string? GoogleMapsUrl { get; init; }
            public string? WazeUrl { get; init; }
            public string? InstagramUrl { get; init; }
            public string? FacebookUrl { get; init; }
            public string? TikTokUrl { get; init; }
            public bool ShowServices { get; init; }
            public bool ShowPrices { get; init; }
            public bool ShowTeam { get; init; }
            public bool ShowLocation { get; init; }
            public bool ShowWhatsAppButton { get; init; }
            public string? SeoTitle { get; init; }
            public string? SeoDescription { get; init; }
        }

        private sealed class PublicAssetRecord
        {
            public TenantPublicAssetType AssetType { get; init; }
            public int? ServicioId { get; init; }
            public string Url { get; init; } = string.Empty;
            public int Width { get; init; }
            public int Height { get; init; }
            public int SortOrder { get; init; }
            public DateTime CreatedAtUtc { get; init; }
        }
    }
}
