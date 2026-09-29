using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.SaaS
{
    public class Factura : ITenantEntity
    {
        public Guid Id { get; set; }

        [Required]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        public Guid? SuscripcionId { get; set; }

        public Guid? PagoSuscripcionId { get; set; }

        public PaymentProviderType Proveedor { get; set; } = PaymentProviderType.None;

        [MaxLength(100)]
        public string? ProviderInvoiceId { get; set; }

        [MaxLength(100)]
        public string? ProviderTransactionId { get; set; }

        [MaxLength(100)]
        public string? ProviderReference { get; set; }

        public decimal? Monto { get; set; }

        [MaxLength(10)]
        public string Moneda { get; set; } = string.Empty;

        [MaxLength(50)]
        public string Estado { get; set; } = string.Empty;

        public DateTime? Fecha { get; set; }

        [ForeignKey("TenantId")]
        public Tenant? Tenant { get; set; }

        [ForeignKey(nameof(SuscripcionId))]
        public Suscripcion? Suscripcion { get; set; }

        [ForeignKey(nameof(PagoSuscripcionId))]
        public PagoSuscripcion? PagoSuscripcion { get; set; }
    }
}
