using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.Productos
{
    public class MovimientoInventario : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }
        [Key]
        public int IdMovimiento { get; set; }

        public int ProductoId { get; set; }

        public DateTime FechaMovimiento { get; set; }

        public string TipoMovimiento { get; set; } = string.Empty; // VENTA, AJUSTE, COMPRA

        public int Cantidad { get; set; }

        public int StockAnterior { get; set; }

        public int StockNuevo { get; set; }

        public string Observacion { get; set; } = string.Empty;

        public Producto Producto { get; set; } = null!;
    }
}
