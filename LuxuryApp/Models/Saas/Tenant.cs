using System.ComponentModel.DataAnnotations;

using LuxuryApp.Models.WhatsApp;

namespace LuxuryApp.Models.SaaS
{
    public class Tenant
    {
        public Guid Id { get; set; }

        [Required]
        [MaxLength(150)]
        public string Nombre { get; set; } = string.Empty;

        public DateTime FechaCreacion { get; set; } = DateTime.Now;

        public bool Activo { get; set; } = true;

        public TenantCommercialAccessMode CommercialAccessMode { get; set; } = TenantCommercialAccessMode.RequiresSubscription;

        public Guid? ForcedPlanId { get; set; }

        [MaxLength(250)]
        public string? CommercialNotes { get; set; }

        public DateTime? CommercialUpdatedUtc { get; set; }

        [MaxLength(450)]
        public string? CommercialUpdatedByUserId { get; set; }

        // ─────────────── Configuración fiscal del negocio ───────────────

        /// <summary>
        /// Si los precios de servicios/productos ya incluyen IVA. Default true (CR). Un
        /// servicio/producto puede sobreescribirlo con su propio <c>PrecioIncluyeIva</c>.
        /// </summary>
        public bool PreciosIncluyenIva { get; set; } = LuxuryApp.Models.Fiscal.FiscalDefaults.PreciosIncluyenIvaPorDefecto;

        /// <summary>Tarifa de IVA por defecto del negocio, en porcentaje. Default 13.</summary>
        public decimal TarifaIvaPorDefecto { get; set; } = LuxuryApp.Models.Fiscal.FiscalDefaults.TarifaIvaPorDefecto;

        public Plan? ForcedPlan { get; set; }

        // Navegación
        public ICollection<Suscripcion> Suscripciones { get; set; } = new List<Suscripcion>();
        public ICollection<TenantSubscriptionAddon> SubscriptionAddons { get; set; } = new List<TenantSubscriptionAddon>();
        public ICollection<TenantCommercialAccessGrant> CommercialAccessGrants { get; set; } = new List<TenantCommercialAccessGrant>();
        public TenantWhatsAppSettings? WhatsAppSettings { get; set; }
    }
}
