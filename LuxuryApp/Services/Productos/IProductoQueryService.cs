using LuxuryApp.Models.Productos;

namespace LuxuryApp.Services.Productos
{
    public interface IProductoQueryService
    {
        Task<ProductoIndexViewModel> BuildIndexViewModelAsync(CancellationToken cancellationToken = default);

        ProductoViewModel BuildFormViewModel(Producto? producto = null);

        Task<ProductoViewModel?> BuildEditViewModelAsync(int idProducto, CancellationToken cancellationToken = default);

        /// <summary>
        /// Fiscalidad guardada del producto, para comparar contra lo que llega del formulario y
        /// saber si el cambio puede reinterpretar cobros históricos. Null si no existe.
        /// </summary>
        Task<ProductoFiscalidad?> ObtenerFiscalidadAsync(int idProducto, CancellationToken cancellationToken = default);
    }

    /// <summary>Configuración fiscal persistida de un producto.</summary>
    public sealed record ProductoFiscalidad(bool AplicaIva, decimal? TarifaIva, bool? PrecioIncluyeIva);
}
