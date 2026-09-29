using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Saas;

namespace LuxuryApp.Models.SaaS
{
    public class Plan
    {
        public Guid Id { get; set; }

        [MaxLength(50)]
        public string? Codigo { get; set; }

        [Required]
        [MaxLength(50)]
        public string Nombre { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? ProviderProductId { get; set; }

        [MaxLength(100)]
        public string? ProviderPriceId { get; set; }

        [MaxLength(100)]
        public string Moneda { get; set; } = "CRC";

        // Monto que se cobra por ciclo de facturacion. Para planes mensuales es el precio
        // mensual; para planes anuales (calculadora LC_A_*) es el total anual adelantado.
        [Required]
        public decimal PrecioMensual { get; set; }

        // Ciclo de facturacion. Default Monthly preserva los planes legacy existentes.
        public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;

        // Equivalente mensual para mostrar en planes anuales ("equivale a X/mes"). Solo display.
        // Para mensuales coincide con PrecioMensual (puede quedar null y derivarse).
        public decimal? MonthlyEquivalentAmount { get; set; }

        public bool Activo { get; set; } = true;
        public bool EsPlanValidacion { get; set; } = false;
        public int? MaxFuncionarios { get; set; } // null = ilimitado
        public int? LimiteMensajesMensual { get; set; }

        // Navegación
        public ICollection<PlanFeature> PlanFeatures { get; set; } = new List<PlanFeature>();
    }
}
