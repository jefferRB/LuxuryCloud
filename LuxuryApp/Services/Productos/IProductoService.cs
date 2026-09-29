using LuxuryApp.Models.Productos;

namespace LuxuryApp.Services.Productos
{
    public interface IProductoService
    {
        Task RegistrarAsync(ProductoWriteRequest request, CancellationToken cancellationToken = default);

        Task ActualizarAsync(int idProducto, ProductoWriteRequest request, CancellationToken cancellationToken = default);

        Task<bool> ToggleActivoAsync(int idProducto, CancellationToken cancellationToken = default);
    }
}
