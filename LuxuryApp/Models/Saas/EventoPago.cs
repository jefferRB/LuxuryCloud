using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.SaaS
{
    public class EventoPago
    {
        public Guid Id { get; set; }

        public PaymentProviderType Proveedor { get; set; } = PaymentProviderType.None;

        public Guid? TenantId { get; set; }

        public Guid? PlanId { get; set; }

        public Guid? PagoSuscripcionId { get; set; }

        [MaxLength(100)]
        public string ProveedorEventId { get; set; } = string.Empty;

        [MaxLength(100)]
        public string Tipo { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? ReferenciaExterna { get; set; }

        [MaxLength(100)]
        public string? ProviderTransactionId { get; set; }

        public int? TilopayRecurringPlanId { get; set; }

        [MaxLength(100)]
        public string? ProviderSubscriberId { get; set; }

        public decimal? Monto { get; set; }

        [MaxLength(10)]
        public string? Moneda { get; set; }

        [MaxLength(100)]
        public string? CorrelationId { get; set; }

        public bool Procesado { get; set; } = false;

        [MaxLength(50)]
        public string EstadoProcesamiento { get; set; } = "Pendiente";

        public string Payload { get; set; } = string.Empty;

        public DateTime FechaRecepcionUtc { get; set; } = DateTime.UtcNow;

        public DateTime? FechaProcesamientoUtc { get; set; }

        [MaxLength(500)]
        public string? Error { get; set; }
    }
}
