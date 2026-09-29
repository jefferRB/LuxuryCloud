using LuxuryApp.Models.Productos;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Productos
{
    public sealed class ProductoQueryService : IProductoQueryService
    {
        private readonly ApplicationDbContext _context;

        public ProductoQueryService(ApplicationDbContext context)
        {
            _context = context;
        }

        public Task<ProductoFiscalidad?> ObtenerFiscalidadAsync(
            int idProducto,
            CancellationToken cancellationToken = default) =>
            _context.Productos
                .AsNoTracking()
                .Where(p => p.IdProducto == idProducto)
                .Select(p => new ProductoFiscalidad(p.AplicaIva, p.TarifaIva, p.PrecioIncluyeIva))
                .FirstOrDefaultAsync(cancellationToken);

        public async Task<ProductoIndexViewModel> BuildIndexViewModelAsync(CancellationToken cancellationToken = default)
        {
            var query = _context.Productos
                .AsNoTracking()
                .AsQueryable();

            var aggregate = await query
                .GroupBy(_ => 1)
                .Select(group => new ProductoAggregateProjection
                {
                    TotalProductos = group.Count(),
                    ProductosBajoStock = group.Count(p => p.CantidadProducto <= p.StockMinimo),
                    ValorInventario = group.Sum(p => p.PrecioProducto * p.CantidadProducto)
                })
                .SingleOrDefaultAsync(cancellationToken)
                ?? new ProductoAggregateProjection();

            var rows = await query
                .OrderBy(p => p.NombreProducto)
                .Select(p => new ProductoIndexItemViewModel
                {
                    IdProducto = p.IdProducto,
                    NombreProducto = p.NombreProducto,
                    PrecioProducto = p.PrecioProducto,
                    CantidadProducto = p.CantidadProducto,
                    StockMinimo = p.StockMinimo,
                    Activo = p.Activo
                })
                .ToListAsync(cancellationToken);

            return new ProductoIndexViewModel
            {
                Productos = rows,
                TotalProductos = aggregate.TotalProductos,
                ProductosBajoStock = aggregate.ProductosBajoStock,
                ValorInventario = aggregate.ValorInventario
            };
        }

        public ProductoViewModel BuildFormViewModel(Producto? producto = null) =>
            new()
            {
                Producto = producto ?? new Producto()
            };

        public Task<ProductoViewModel?> BuildEditViewModelAsync(int idProducto, CancellationToken cancellationToken = default) =>
            _context.Productos
                .AsNoTracking()
                .Where(p => p.IdProducto == idProducto)
                .Select(p => new ProductoViewModel
                {
                    Producto = new Producto
                    {
                        IdProducto = p.IdProducto,
                        NombreProducto = p.NombreProducto,
                        DetalleProducto = p.DetalleProducto,
                        PrecioProducto = p.PrecioProducto,
                        CantidadProducto = p.CantidadProducto,
                        StockMinimo = p.StockMinimo,
                        Activo = p.Activo,
                        FechaRegistro = p.FechaRegistro,
                        AplicaIva = p.AplicaIva,
                        TarifaIva = p.TarifaIva,
                        PrecioIncluyeIva = p.PrecioIncluyeIva
                    }
                })
                .SingleOrDefaultAsync(cancellationToken);

        private sealed class ProductoAggregateProjection
        {
            public int TotalProductos { get; init; }

            public int ProductosBajoStock { get; init; }

            public decimal ValorInventario { get; init; }
        }
    }
}
