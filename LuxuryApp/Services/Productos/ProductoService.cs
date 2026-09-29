using System.Data;
using LuxuryApp.Models.Productos;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Productos
{
    public sealed class ProductoService : IProductoService
    {
        private const int MaxNombreLength = 150;
        private const int MaxDetalleLength = 300;
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ILogger<ProductoService> _logger;

        public ProductoService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ILogger<ProductoService> logger)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _logger = logger;
        }

        public async Task RegistrarAsync(ProductoWriteRequest request, CancellationToken cancellationToken = default)
        {
            var normalizedRequest = NormalizeRequest(request);
            ValidateRequest(normalizedRequest);

            var executionStrategy = _context.Database.CreateExecutionStrategy();

            try
            {
                await executionStrategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database
                        .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

                    await EnsureNombreDisponibleAsync(normalizedRequest.NombreProducto, productoIdToExclude: null, cancellationToken);

                    var timestamp = NormalizeTimestamp(_businessDateTimeProvider.Now());
                    var producto = new Producto
                    {
                        NombreProducto = normalizedRequest.NombreProducto,
                        DetalleProducto = normalizedRequest.DetalleProducto,
                        PrecioProducto = normalizedRequest.PrecioProducto,
                        CantidadProducto = normalizedRequest.CantidadProducto,
                        StockMinimo = normalizedRequest.StockMinimo,
                        AplicaIva = normalizedRequest.AplicaIva,
                        TarifaIva = normalizedRequest.TarifaIva,
                        PrecioIncluyeIva = normalizedRequest.PrecioIncluyeIva,
                        Activo = true,
                        FechaRegistro = timestamp
                    };

                    _context.Productos.Add(producto);

                    if (normalizedRequest.CantidadProducto > 0)
                    {
                        _context.MovimientosInventario.Add(new MovimientoInventario
                        {
                            Producto = producto,
                            FechaMovimiento = timestamp,
                            TipoMovimiento = "COMPRA",
                            Cantidad = normalizedRequest.CantidadProducto,
                            StockAnterior = 0,
                            StockNuevo = normalizedRequest.CantidadProducto,
                            Observacion = "Stock inicial al crear producto."
                        });
                    }

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                });
            }
            catch (ProductoValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al registrar producto {NombreProducto}.", normalizedRequest.NombreProducto);
                throw new InvalidOperationException("No fue posible registrar el producto.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Operacion invalida al registrar producto {NombreProducto}.", normalizedRequest.NombreProducto);
                throw;
            }
        }

        public async Task ActualizarAsync(int idProducto, ProductoWriteRequest request, CancellationToken cancellationToken = default)
        {
            var normalizedRequest = NormalizeRequest(request);
            ValidateRequest(normalizedRequest);

            var executionStrategy = _context.Database.CreateExecutionStrategy();

            try
            {
                await executionStrategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database
                        .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

                    var producto = await _context.Productos
                        .FirstOrDefaultAsync(p => p.IdProducto == idProducto, cancellationToken);

                    if (producto is null)
                    {
                        throw new InvalidOperationException("El producto seleccionado no existe o no pertenece al tenant actual.");
                    }

                    await EnsureNombreDisponibleAsync(normalizedRequest.NombreProducto, idProducto, cancellationToken);

                    var stockAnterior = producto.CantidadProducto;

                    producto.NombreProducto = normalizedRequest.NombreProducto;
                    producto.DetalleProducto = normalizedRequest.DetalleProducto;
                    producto.PrecioProducto = normalizedRequest.PrecioProducto;
                    producto.CantidadProducto = normalizedRequest.CantidadProducto;
                    producto.StockMinimo = normalizedRequest.StockMinimo;
                    producto.AplicaIva = normalizedRequest.AplicaIva;
                    producto.TarifaIva = normalizedRequest.TarifaIva;
                    producto.PrecioIncluyeIva = normalizedRequest.PrecioIncluyeIva;

                    if (stockAnterior != normalizedRequest.CantidadProducto)
                    {
                        _context.MovimientosInventario.Add(new MovimientoInventario
                        {
                            ProductoId = producto.IdProducto,
                            FechaMovimiento = NormalizeTimestamp(_businessDateTimeProvider.Now()),
                            TipoMovimiento = "AJUSTE",
                            Cantidad = Math.Abs(normalizedRequest.CantidadProducto - stockAnterior),
                            StockAnterior = stockAnterior,
                            StockNuevo = normalizedRequest.CantidadProducto,
                            Observacion = $"Ajuste manual de inventario: {stockAnterior} -> {normalizedRequest.CantidadProducto}."
                        });
                    }

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                });
            }
            catch (ProductoValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al actualizar producto {ProductoId}.", idProducto);
                throw new InvalidOperationException("No fue posible actualizar el producto.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Operacion invalida al actualizar producto {ProductoId}.", idProducto);
                throw;
            }
        }

        public async Task<bool> ToggleActivoAsync(int idProducto, CancellationToken cancellationToken = default)
        {
            try
            {
                var producto = await _context.Productos
                    .FirstOrDefaultAsync(p => p.IdProducto == idProducto, cancellationToken);

                if (producto is null)
                {
                    return false;
                }

                producto.Activo = !producto.Activo;
                await _context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al cambiar el estado del producto {ProductoId}.", idProducto);
                throw new InvalidOperationException("No fue posible cambiar el estado del producto.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Operacion invalida al cambiar el estado del producto {ProductoId}.", idProducto);
                throw;
            }
        }

        private async Task EnsureNombreDisponibleAsync(
            string nombreProducto,
            int? productoIdToExclude,
            CancellationToken cancellationToken)
        {
            var normalizedNameUpper = nombreProducto.ToUpperInvariant();

            var exists = await _context.Productos
                .AsNoTracking()
                .AnyAsync(
                    p => (!productoIdToExclude.HasValue || p.IdProducto != productoIdToExclude.Value) &&
                         p.NombreProducto.ToUpper() == normalizedNameUpper,
                    cancellationToken);

            if (exists)
            {
                throw new ProductoValidationException(
                    "Ya existe un producto con ese nombre en el tenant actual.",
                    "Producto.NombreProducto");
            }
        }

        private static ProductoWriteRequest NormalizeRequest(ProductoWriteRequest request) =>
            new()
            {
                NombreProducto = CollapseWhitespace(request.NombreProducto),
                DetalleProducto = NormalizeDetalle(request.DetalleProducto),
                PrecioProducto = Math.Round(request.PrecioProducto, 2, MidpointRounding.AwayFromZero),
                CantidadProducto = request.CantidadProducto,
                StockMinimo = request.StockMinimo,
                AplicaIva = request.AplicaIva,
                TarifaIva = request.TarifaIva.HasValue
                    ? Math.Round(request.TarifaIva.Value, 2, MidpointRounding.AwayFromZero)
                    : null,
                PrecioIncluyeIva = request.PrecioIncluyeIva
            };

        private static void ValidateRequest(ProductoWriteRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.NombreProducto))
            {
                throw new ProductoValidationException("Debe indicar el nombre del producto.", "Producto.NombreProducto");
            }

            if (request.NombreProducto.Length > MaxNombreLength)
            {
                throw new ProductoValidationException(
                    $"El nombre del producto no puede exceder {MaxNombreLength} caracteres.",
                    "Producto.NombreProducto");
            }

            if (!string.IsNullOrEmpty(request.DetalleProducto) && request.DetalleProducto.Length > MaxDetalleLength)
            {
                throw new ProductoValidationException(
                    $"El detalle del producto no puede exceder {MaxDetalleLength} caracteres.",
                    "Producto.DetalleProducto");
            }

            if (request.PrecioProducto <= 0 || request.PrecioProducto > 999999m)
            {
                throw new ProductoValidationException(
                    "Debe indicar un precio mayor a cero y dentro del rango permitido.",
                    "Producto.PrecioProducto");
            }

            if (request.CantidadProducto < 0)
            {
                throw new ProductoValidationException(
                    "La cantidad en stock no puede ser negativa.",
                    "Producto.CantidadProducto");
            }

            if (request.StockMinimo < 0)
            {
                throw new ProductoValidationException(
                    "El stock minimo no puede ser negativo.",
                    "Producto.StockMinimo");
            }
        }

        private static string CollapseWhitespace(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return string.Join(
                ' ',
                value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        private static string? NormalizeDetalle(string? value)
        {
            var collapsed = CollapseWhitespace(value);
            return string.IsNullOrWhiteSpace(collapsed) ? null : collapsed;
        }

        private static DateTime NormalizeTimestamp(DateTime value) =>
            new(
                value.Year,
                value.Month,
                value.Day,
                value.Hour,
                value.Minute,
                0);
    }
}
