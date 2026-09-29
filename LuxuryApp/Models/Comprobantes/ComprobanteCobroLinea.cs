using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.Comprobantes
{
    /// <summary>
    /// Línea de detalle de un comprobante. La estructura admite múltiples ítems aunque
    /// hoy normalmente se genere una sola línea (servicio o producto del cobro).
    /// Es <see cref="ITenantEntity"/> para que el aislamiento multi-tenant aplique también
    /// a las líneas (defensa en profundidad).
    /// </summary>
    public class ComprobanteCobroLinea : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int ComprobanteCobroId { get; set; }
        public ComprobanteCobro? ComprobanteCobro { get; set; }

        [MaxLength(250)]
        public string Descripcion { get; set; } = string.Empty;

        /// <summary>Servicio, Producto u Otro (ver <see cref="ComprobanteTipoLinea"/>).</summary>
        [MaxLength(20)]
        public string TipoLinea { get; set; } = ComprobanteTipoLinea.Otro;

        public decimal Cantidad { get; set; } = 1;
        public decimal PrecioUnitario { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Impuesto { get; set; }
        public decimal Total { get; set; }

        public int? ServicioId { get; set; }
        public int? ProductoId { get; set; }
    }
}
