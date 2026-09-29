using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.PublicPages
{
    public sealed class TenantPublicPageViewModel
    {
        public string Slug { get; init; } = string.Empty;
        public string BusinessName { get; init; } = string.Empty;
        public string HeroTitle { get; init; } = string.Empty;
        public string? HeroSubtitle { get; init; }
        public string? HeroEyebrow { get; init; }
        public string? Description { get; init; }
        public PublicImageAssetViewModel? LogoImage { get; init; }
        public PublicImageAssetViewModel? CoverImage { get; init; }

        /// <summary>Tokens de color derivados del color de marca del tenant.</summary>
        public PublicBrandTheme Theme { get; init; } = PublicBrandTheme.Resolve(null);
        public string? Phone { get; init; }
        public string? WhatsAppPhone { get; init; }
        public string? WhatsAppUrl { get; init; }
        public string? Email { get; init; }
        public string? Address { get; init; }
        public string? BusinessHours { get; init; }
        public BusinessScheduleStatusViewModel? Schedule { get; init; }
        public PublicImageAssetViewModel? LocationImage { get; init; }
        public string? GoogleMapsUrl { get; init; }
        public string? WazeUrl { get; init; }
        public string? InstagramUrl { get; init; }
        public string? FacebookUrl { get; init; }
        public string? TikTokUrl { get; init; }
        public string? BookingUrl { get; init; }
        public string? ReserveActionUrl { get; init; }
        public string? WhatsAppActionUrl { get; init; }
        public string? MapsActionUrl { get; init; }
        public string? WazeActionUrl { get; init; }
        public string? PublicSiteUrl { get; init; }
        public bool BookingEnabled { get; init; }
        public bool ShowLocation { get; init; }
        public string SeoTitle { get; init; } = string.Empty;
        public string SeoDescription { get; init; } = string.Empty;
        public IReadOnlyList<PublicImageAssetViewModel> BusinessGallery { get; init; } =
            Array.Empty<PublicImageAssetViewModel>();
        public IReadOnlyList<PublicServiceCardViewModel> Services { get; init; } =
            Array.Empty<PublicServiceCardViewModel>();
        public IReadOnlyList<PublicTeamMemberViewModel> TeamMembers { get; init; } =
            Array.Empty<PublicTeamMemberViewModel>();

        public bool HasBusinessHours =>
            Schedule?.HasSchedule == true || !string.IsNullOrWhiteSpace(BusinessHours);

        // Instagram ya no aparece en la seccion de informacion (ahora es burbuja flotante).
        public bool HasContactInfo =>
            !string.IsNullOrWhiteSpace(Phone) ||
            !string.IsNullOrWhiteSpace(Email) ||
            HasBusinessHours ||
            !string.IsNullOrWhiteSpace(FacebookUrl) ||
            !string.IsNullOrWhiteSpace(TikTokUrl);
    }

    public sealed class PublicServiceCardViewModel
    {
        public int ServiceId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public int? DurationMinutes { get; init; }
        public decimal? Price { get; init; }
        public string? BookingUrl { get; init; }
        public string? ReserveActionUrl { get; init; }
        public PublicImageAssetViewModel? MainImage { get; init; }
    }

    public sealed class PublicImageAssetViewModel
    {
        public string Url { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
        public string AltText { get; init; } = string.Empty;
    }

    public sealed class PublicTeamMemberViewModel
    {
        public string Name { get; init; } = string.Empty;
        public string? Specialty { get; init; }
        public string? Description { get; init; }
        public string? PhotoUrl { get; init; }

        public string Initial =>
            string.IsNullOrWhiteSpace(Name)
                ? "?"
                : char.ToUpperInvariant(Name.Trim()[0]).ToString();
    }

    public sealed class EditTenantPublicPageViewModel : IValidatableObject
    {
        public bool IsPublished { get; set; }

        [Display(Name = "Nombre publico del negocio")]
        [MaxLength(120)]
        public string? PublicBusinessName { get; set; }

        [Display(Name = "Titulo principal")]
        [MaxLength(120)]
        public string? HeroTitle { get; set; }

        [Display(Name = "Subtitulo")]
        [MaxLength(180)]
        public string? HeroSubtitle { get; set; }

        [Display(Name = "Etiqueta superior opcional")]
        [MaxLength(80)]
        public string? HeroEyebrow { get; set; }

        [Display(Name = "Descripcion corta")]
        [MaxLength(1500)]
        public string? Description { get; set; }

        /// <summary>
        /// Color principal de la landing (#RRGGBB). Vacio = negro por defecto.
        /// </summary>
        [Display(Name = "Color principal")]
        [MaxLength(7)]
        [RegularExpression("^#([0-9A-Fa-f]{6})$", ErrorMessage = "El color debe tener el formato #RRGGBB.")]
        public string? AccentColorHex { get; set; }

        /// <summary>Color efectivo para pintar el selector (nunca vacio).</summary>
        public string EffectiveAccentColorHex =>
            PublicBrandTheme.Normalize(AccentColorHex) ?? PublicBrandTheme.DefaultAccentHex;

        [Display(Name = "Telefono")]
        [MaxLength(30)]
        public string? Phone { get; set; }

        [Display(Name = "WhatsApp")]
        [MaxLength(30)]
        public string? WhatsAppPhone { get; set; }

        [Display(Name = "Correo")]
        [MaxLength(256)]
        [EmailAddress]
        public string? Email { get; set; }

        [Display(Name = "Direccion")]
        [MaxLength(300)]
        public string? Address { get; set; }

        [Display(Name = "Horario del negocio")]
        [MaxLength(500)]
        public string? BusinessHours { get; set; }

        /// <summary>
        /// Editor estructurado del horario (7 dias, Lunes-Domingo). Al guardar se valida y
        /// serializa a <c>TenantPublicPage.BusinessHoursJson</c>.
        /// </summary>
        public List<BusinessScheduleDayInput> ScheduleDays { get; set; } = new();

        [Display(Name = "Google Maps URL")]
        [MaxLength(500)]
        public string? GoogleMapsUrl { get; set; }

        [Display(Name = "Waze URL")]
        [MaxLength(500)]
        public string? WazeUrl { get; set; }

        [Display(Name = "Instagram URL")]
        [MaxLength(300)]
        public string? InstagramUrl { get; set; }

        [Display(Name = "Facebook URL")]
        [MaxLength(300)]
        public string? FacebookUrl { get; set; }

        [Display(Name = "TikTok URL")]
        [MaxLength(300)]
        public string? TikTokUrl { get; set; }

        public bool ShowServices { get; set; } = true;

        public bool ShowPrices { get; set; } = true;

        public bool ShowTeam { get; set; }

        public bool ShowLocation { get; set; } = true;

        public bool ShowWhatsAppButton { get; set; } = true;

        [Display(Name = "SEO title")]
        [MaxLength(70)]
        public string? SeoTitle { get; set; }

        [Display(Name = "Meta description")]
        [MaxLength(180)]
        public string? SeoDescription { get; set; }

        public string BusinessName { get; set; } = string.Empty;
        public string? PublicSiteUrl { get; set; }
        public string? BookingUrl { get; set; }
        public bool BookingEnabled { get; set; }
        public bool CanUsePublicLandingPage { get; set; } = true;
        public AdminPublicAssetImageViewModel? LogoAsset { get; set; }
        public AdminPublicAssetImageViewModel? CoverAsset { get; set; }
        public AdminPublicAssetImageViewModel? LocationAsset { get; set; }
        public IReadOnlyList<AdminPublicAssetImageViewModel> BusinessGallery { get; set; } =
            Array.Empty<AdminPublicAssetImageViewModel>();
        public IReadOnlyList<PublicServiceAssetSettingsViewModel> ServiceAssets { get; set; } =
            Array.Empty<PublicServiceAssetSettingsViewModel>();
        public PublicAssetUsageViewModel StorageUsage { get; set; } = new();
        public PublicPageAnalyticsSummaryViewModel Analytics { get; set; } = new();
        public int MaxBusinessGalleryImages { get; set; } = 12;

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            foreach (var result in ValidatePlainText(nameof(PublicBusinessName), PublicBusinessName)) yield return result;
            foreach (var result in ValidatePlainText(nameof(HeroTitle), HeroTitle)) yield return result;
            foreach (var result in ValidatePlainText(nameof(HeroSubtitle), HeroSubtitle)) yield return result;
            foreach (var result in ValidatePlainText(nameof(HeroEyebrow), HeroEyebrow)) yield return result;
            foreach (var result in ValidatePlainText(nameof(Description), Description)) yield return result;
            foreach (var result in ValidatePlainText(nameof(Phone), Phone)) yield return result;
            foreach (var result in ValidatePlainText(nameof(WhatsAppPhone), WhatsAppPhone)) yield return result;
            foreach (var result in ValidatePlainText(nameof(Address), Address)) yield return result;
            foreach (var result in ValidateMultilinePlainText(nameof(BusinessHours), BusinessHours)) yield return result;
            foreach (var result in ValidatePlainText(nameof(SeoTitle), SeoTitle)) yield return result;
            foreach (var result in ValidatePlainText(nameof(SeoDescription), SeoDescription)) yield return result;
        }

        private static IEnumerable<ValidationResult> ValidatePlainText(string memberName, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            if (value.Contains('<', StringComparison.Ordinal) ||
                value.Contains('>', StringComparison.Ordinal))
            {
                yield return new ValidationResult(
                    "No se permite HTML en los textos publicos.",
                    new[] { memberName });
            }
        }

        private static IEnumerable<ValidationResult> ValidateMultilinePlainText(string memberName, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            if (value.Contains('<', StringComparison.Ordinal) ||
                value.Contains('>', StringComparison.Ordinal))
            {
                yield return new ValidationResult(
                    "No se permite HTML en los textos publicos.",
                    new[] { memberName });
            }
        }
    }

    public sealed class AdminPublicAssetImageViewModel
    {
        public Guid Id { get; init; }
        public string Url { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
        public long SizeBytes { get; init; }
        public int SortOrder { get; init; }

        public string SizeDisplay => FormatBytes(SizeBytes);

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

    public sealed class PublicAssetUsageViewModel
    {
        public long UsedBytes { get; init; }
        public long MaxBytes { get; init; } = 25L * 1024 * 1024;

        public decimal PercentUsed =>
            MaxBytes <= 0 ? 0 : Math.Round((decimal)UsedBytes * 100 / MaxBytes, 1);

        public string UsedDisplay => FormatBytes(UsedBytes);

        public string MaxDisplay => FormatBytes(MaxBytes);

        private static string FormatBytes(long bytes)
        {
            var mb = bytes / 1024m / 1024m;
            return $"{mb:0.#} MB";
        }
    }

    public sealed class PublicServiceAssetSettingsViewModel
    {
        public int ServiceId { get; init; }
        public string ServiceName { get; init; } = string.Empty;
        public AdminPublicAssetImageViewModel? MainImage { get; init; }
    }

    /// <summary>Un dia del editor estructurado de horario (config interna).</summary>
    public sealed class BusinessScheduleDayInput
    {
        /// <summary>0 = Lunes ... 6 = Domingo.</summary>
        public int DayIndex { get; set; }
        public bool Closed { get; set; }
        public string? Open1 { get; set; }
        public string? Close1 { get; set; }
        public string? Open2 { get; set; }
        public string? Close2 { get; set; }
    }

    public sealed class PublicPageAnalyticsSummaryViewModel
    {
        public int Days { get; init; } = 30;
        public long PageViews { get; init; }
        public long ReserveClicks { get; init; }
        public long ServiceReserveClicks { get; init; }
        public long WhatsAppClicks { get; init; }
        public long MapsClicks { get; init; }
        public long TotalEvents { get; init; }
        public IReadOnlyList<PublicPageTopServiceMetricViewModel> TopServices { get; init; } =
            Array.Empty<PublicPageTopServiceMetricViewModel>();

        public bool HasAnyActivity => TotalEvents > 0;
    }

    public sealed class PublicPageTopServiceMetricViewModel
    {
        public string ServiceName { get; init; } = string.Empty;
        public long Clicks { get; init; }
    }
}
