namespace LuxuryApp.Models.Productos
{
    public class ProductoIndexItemViewModel
    {
        public int IdProducto { get; set; }

        public string NombreProducto { get; set; } = string.Empty;

        public decimal PrecioProducto { get; set; }

        public int CantidadProducto { get; set; }

        public int StockMinimo { get; set; }

        public bool Activo { get; set; }
    }
}
