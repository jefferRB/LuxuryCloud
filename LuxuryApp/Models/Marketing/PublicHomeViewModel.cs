namespace LuxuryApp.Models.Marketing
{
    public sealed class PublicHomeViewModel
    {
        public IReadOnlyCollection<MarketingMetricViewModel> HeroMetrics { get; init; } = Array.Empty<MarketingMetricViewModel>();
        public IReadOnlyCollection<MarketingModuleViewModel> Modules { get; init; } = Array.Empty<MarketingModuleViewModel>();

        /// <summary>
        /// Vista comercial de precios para la landing, derivada del calculador real
        /// (LC_M_/LC_A_). Reemplaza las cards de planes legacy/TEST de la iteración anterior.
        /// </summary>
        public CommercialPricingPreview Pricing { get; init; } = CommercialPricingPreview.Unavailable();
    }
}
