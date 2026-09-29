namespace LuxuryApp.Models.Productos
{
    public class ProductoIndexViewModel
    {
        public List<ProductoIndexItemViewModel> Productos { get; set; } = new();

        public int TotalProductos { get; set; }

        public int ProductosBajoStock { get; set; }

        public decimal ValorInventario { get; set; }
    }
}
