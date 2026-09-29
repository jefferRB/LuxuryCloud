namespace LuxuryApp.Models.Productos
{
    public class ProductoWriteRequest
    {
        public string NombreProducto { get; set; } = string.Empty;

        public string? DetalleProducto { get; set; }

        public decimal PrecioProducto { get; set; }

        public int CantidadProducto { get; set; }

        public int StockMinimo { get; set; }

        // ─────────────── Configuración fiscal (opcional; hereda del tenant) ───────────────
        public bool AplicaIva { get; set; } = LuxuryApp.Models.Fiscal.FiscalDefaults.AplicaIvaPorDefecto;

        public decimal? TarifaIva { get; set; }

        public bool? PrecioIncluyeIva { get; set; }
    }
}
