using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.Finanzas
{
    public class Egreso : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }
        [Key]
        public int IdEgreso { get; set; }

        [Required]
        [Display(Name = "Fecha")]
        public DateTime FechaEgreso { get; set; }

        [Required]
        [Display(Name = "Detalle")]
        [StringLength(200)]
        public string Detalle { get; set; } = string.Empty;



        [Required]
        [Display(Name = "Monto")]
        [DecimalRange(0.01, 999999, ErrorMessage = "Debe indicar un monto mayor a cero y dentro del rango permitido.")]
        public decimal Monto { get; set; }

        [Required]
        [Display(Name = "Método de Pago")]
        public string MetodoPago { get; set; } = string.Empty;
        public int CategoriaId { get; set; }
        public Categoria? Categoria { get; set; }
    }
}
