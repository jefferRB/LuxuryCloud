using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.Productos
{
    public class Producto : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }
        [Key]
        public int IdProducto { get; set; }

        [Required]
        [Display(Name = "Nombre")]
        [StringLength(150)]
        public string NombreProducto { get; set; } = string.Empty;

        [Display(Name = "Detalle")]
        [StringLength(300)]
        public string? DetalleProducto { get; set; }

        [Required]
        [Display(Name = "Precio")]
        [DecimalRange(0.01, 999999, ErrorMessage = "Debe indicar un precio mayor a cero y dentro del rango permitido.")]
        public decimal PrecioProducto { get; set; }

        [Required]
        [Display(Name = "Cantidad en Stock")]
        [Range(0, 99999)]
        public int CantidadProducto { get; set; }

        [Display(Name = "Stock mínimo")]
        [Range(0, 99999)]
        public int StockMinimo { get; set; } = 5;

        public bool Activo { get; set; } = true;

        public DateTime FechaRegistro { get; set; }

        // ─────────────── Configuración fiscal (opcional; hereda del tenant) ───────────────

        /// <summary>Si el producto está sujeto a IVA. Default true.</summary>
        public bool AplicaIva { get; set; } = LuxuryApp.Models.Fiscal.FiscalDefaults.AplicaIvaPorDefecto;

        /// <summary>Tarifa de IVA propia, en porcentaje. Null → hereda la tarifa del tenant.</summary>
        public decimal? TarifaIva { get; set; }

        /// <summary>Si el precio incluye IVA. Null → hereda la configuración del tenant.</summary>
        public bool? PrecioIncluyeIva { get; set; }
    }
}
