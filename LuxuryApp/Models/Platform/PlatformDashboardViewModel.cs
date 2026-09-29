using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformDashboardViewModel
    {
        public int TotalTenants { get; init; }
        public int TotalUsers { get; init; }
        public int TotalActiveSubscriptions { get; init; }
        public int TotalPromotionalCodes { get; init; }
        /// <summary>Catalogo completo de planes activos. Solo para listados informativos.</summary>
        public IReadOnlyCollection<Plan> AvailablePlans { get; init; } = Array.Empty<Plan>();

        /// <summary>
        /// Opciones validas del selector de PLAN BASE forzado: solo planes comerciales de la
        /// calculadora (LC_M_01..11 / LC_A_01..11). NUNCA contiene add-ons WhatsApp: un paquete de
        /// mensajes no define limite de funcionarios y la validacion server-side lo rechaza.
        /// </summary>
        public IReadOnlyCollection<Plan> BasePlanOptions { get; init; } = Array.Empty<Plan>();

        /// <summary>
        /// Planes legacy (BASIC/PRO/BUSINESS) y de validacion/prueba. Se ofrecen SOLO en la seccion
        /// avanzada colapsada, para poder migrar un tenant historico; la eleccion queda auditada.
        /// </summary>
        public IReadOnlyCollection<Plan> AdvancedPlanOptions { get; init; } = Array.Empty<Plan>();

        public IReadOnlyCollection<PlatformTenantRowViewModel> Tenants { get; init; } = Array.Empty<PlatformTenantRowViewModel>();
        public IReadOnlyCollection<PlatformRecentUserViewModel> RecentUsers { get; init; } = Array.Empty<PlatformRecentUserViewModel>();
        public IReadOnlyCollection<PlatformRecentPaymentViewModel> RecentPayments { get; init; } = Array.Empty<PlatformRecentPaymentViewModel>();
        public IReadOnlyCollection<PlatformBillingPendingCheckoutViewModel> PendingRecurringCheckouts { get; init; } = Array.Empty<PlatformBillingPendingCheckoutViewModel>();
        public IReadOnlyCollection<PlatformBillingEventViewModel> RecentBillingEvents { get; init; } = Array.Empty<PlatformBillingEventViewModel>();
        public IReadOnlyCollection<PlatformSubscriptionStatusViewModel> ActiveRecurringSubscriptions { get; init; } = Array.Empty<PlatformSubscriptionStatusViewModel>();
        public IReadOnlyCollection<PlatformSubscriptionStatusViewModel> ActiveRecurringAddons { get; init; } = Array.Empty<PlatformSubscriptionStatusViewModel>();
    }
}
