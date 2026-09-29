using LuxuryApp.Models.Marketing;

namespace LuxuryApp.Models.SaaS
{
    /// <summary>
    /// Modelo del partial compartido de cards de paquetes WhatsApp (add-on).
    /// Usado tanto en la vista privada de Suscripcion como en el modulo WhatsApp.
    /// </summary>
    public sealed class WhatsAppPackageCardsViewModel
    {
        public IReadOnlyCollection<MarketingPlanCardViewModel> Cards { get; init; } = Array.Empty<MarketingPlanCardViewModel>();

        /// <summary>Codigo del addon actualmente activo (para marcar "Paquete actual").</summary>
        public string? CurrentAddonCode { get; init; }

        /// <summary>True si el tenant ya tiene un plan base activo (requisito para contratar addon).</summary>
        public bool HasBaseAccess { get; init; }

        /// <summary>True si ya tiene un addon WhatsApp activo (cambia el texto del boton a "Cambiar").</summary>
        public bool HasActiveAddon { get; init; }
    }
}
