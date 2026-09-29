namespace LuxuryApp.Models.Productos
{
    public class ProductoVendidoVM
    {
        public DateTime Fecha { get; set; }

        public string NombreProducto { get; set; } = string.Empty;

        public decimal Precio { get; set; }

        public decimal GananciaFuncionario { get; set; }
    }
}
