using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LuxuryApp.Models.SaaS
{
    public class HistorialSuscripcion
    {
        public Guid Id { get; set; }

        public Guid SuscripcionId { get; set; }

        public Guid? PlanIdAnterior { get; set; }

        public Guid? PlanIdNuevo { get; set; }

        public DateTime FechaCambio { get; set; } = DateTime.Now;

        public PaymentProviderType? Proveedor { get; set; }

        [MaxLength(250)]
        public string? Motivo { get; set; }

        // Relación
        [ForeignKey("SuscripcionId")]
        public Suscripcion? Suscripcion { get; set; }
    }
}
