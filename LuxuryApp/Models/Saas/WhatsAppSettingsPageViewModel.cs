using LuxuryApp.Models.Marketing;

namespace LuxuryApp.Models.SaaS
{
    /// <summary>
    /// Modelo de la vista privada del modulo WhatsApp (/WhatsApp).
    /// Reutiliza <see cref="BillingSubscriptionSummaryViewModel"/> para el resumen operativo
    /// (consumo/saldo/settings) y las cards de add-on para la seccion de paquetes.
    /// </summary>
    public sealed class WhatsAppSettingsPageViewModel
    {
        public BillingSubscriptionSummaryViewModel? Summary { get; init; }

        public IReadOnlyCollection<MarketingPlanCardViewModel> WhatsAppAddonCards { get; init; } = Array.Empty<MarketingPlanCardViewModel>();

        /// <summary>True si existe un addon WhatsApp activo (decide estado vacio vs operativo).</summary>
        public bool HasWhatsAppAddon => Summary?.HasWhatsAppAddon == true;

        /// <summary>True si el tenant tiene plan base activo (requisito para contratar add-on).</summary>
        public bool HasBaseAccess { get; init; }
    }
}
