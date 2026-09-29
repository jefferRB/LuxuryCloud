using LuxuryApp.Models.Marketing;
using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.PublicSite
{
    public interface IPublicSiteContentService
    {
        IReadOnlyCollection<MarketingMetricViewModel> GetHeroMetrics();
        IReadOnlyCollection<MarketingModuleViewModel> GetModules();
        Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetPlanCardsAsync(
            CancellationToken cancellationToken = default);
        Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetWhatsAppAddonCardsAsync(
            CancellationToken cancellationToken = default);
        Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetInternalPlanCardsAsync(
            CancellationToken cancellationToken = default);
        Task<Plan?> FindAvailablePlanAsync(Guid planId, CancellationToken cancellationToken = default);
        Task<string?> GetPlanNameAsync(Guid? planId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Catálogo comercial COMPACTO para la landing (mismo calculador que /Billing/Planes,
        /// planes LC_M_/LC_A_). Implementación por defecto: no disponible, para que los stubs
        /// que no lo necesitan no cambien.
        /// </summary>
        Task<CommercialPricingPreview> GetCommercialPricingPreviewAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CommercialPricingPreview.Unavailable());
    }
}
