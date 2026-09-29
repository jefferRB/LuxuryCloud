using LuxuryApp.Models.Marketing;

namespace LuxuryApp.Models.SaaS
{
    public sealed class BillingPlanesViewModel
    {
        public IReadOnlyCollection<MarketingPlanCardViewModel> BasePlanCards { get; init; } = Array.Empty<MarketingPlanCardViewModel>();
        public IReadOnlyCollection<MarketingPlanCardViewModel> WhatsAppAddonCards { get; init; } = Array.Empty<MarketingPlanCardViewModel>();
        public IReadOnlyCollection<MarketingPlanCardViewModel> InternalPlanCards { get; init; } = Array.Empty<MarketingPlanCardViewModel>();
        public TenantCommercialAccessResult? CurrentAccess { get; init; }
        public BillingSubscriptionSummaryViewModel? CurrentSubscription { get; init; }
        public bool IsAuthenticated { get; init; }
        public Guid? SelectedPlanId { get; init; }

        /// <summary>Calculadora dinamica de suscripcion (reemplaza las tarjetas BASIC/PRO/BUSINESS).</summary>
        public SubscriptionCalculatorViewModel? Calculator { get; init; }

        /// <summary>
        /// True si la integración admin de TiloPay está activa: habilita el botón
        /// "Actualizar tarjeta / Reintentar pago" (recurrentUrl) cuando la suscripción está morosa.
        /// </summary>
        public bool RecurrentUpdateAvailable { get; init; }
    }
}
