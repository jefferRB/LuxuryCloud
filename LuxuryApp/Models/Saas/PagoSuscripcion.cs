using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.SaaS
{
    public class PagoSuscripcion : ITenantEntity
    {
        public Guid Id { get; set; }

        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        public Guid PlanId { get; set; }

        public PaymentProviderType Proveedor { get; set; }

        public EstadoPagoProveedor Estado { get; set; } = EstadoPagoProveedor.Pendiente;

        [MaxLength(100)]
        public string ReferenciaInterna { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? ProviderCheckoutId { get; set; }

        [MaxLength(100)]
        public string? ProviderTransactionId { get; set; }

        [MaxLength(100)]
        public string? ProviderReference { get; set; }

        public int? TilopayRecurringPlanId { get; set; }

        [MaxLength(100)]
        public string? ProviderSubscriberId { get; set; }

        [MaxLength(100)]
        public string? CorrelationToken { get; set; }

        [MaxLength(50)]
        public string? ProviderResultCode { get; set; }

        [MaxLength(300)]
        public string? ProviderResultMessage { get; set; }

        [MaxLength(100)]
        public string? ProviderAuthorizationCode { get; set; }

        [MaxLength(50)]
        public string? ProviderCardBrand { get; set; }

        [MaxLength(20)]
        public string? ProviderCardLast4 { get; set; }

        [MaxLength(500)]
        public string? CheckoutUrl { get; set; }

        [MaxLength(150)]
        public string? ClienteNombre { get; set; }

        [MaxLength(200)]
        public string? ClienteEmail { get; set; }

        [MaxLength(250)]
        public string Descripcion { get; set; } = string.Empty;

        public decimal Monto { get; set; }

        [MaxLength(10)]
        public string Moneda { get; set; } = "CRC";

        public DateTime FechaCreacionUtc { get; set; } = DateTime.UtcNow;

        public DateTime? FechaActualizacionUtc { get; set; }

        public DateTime? FechaConfirmacionUtc { get; set; }

        public string? UltimoPayloadProveedor { get; set; }

        [ForeignKey(nameof(TenantId))]
        public Tenant? Tenant { get; set; }

        [ForeignKey(nameof(PlanId))]
        public Plan? Plan { get; set; }
    }
}
