using LuxuryApp.Models.Productos;
using LuxuryApp.Services.Productos;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ProductoServiceTests
    {
        [Fact]
        public async Task RegistrarAsync_ShouldPersistNormalizedProduct_AndInitialInventoryMovement()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var service = ControllerTestSupport.CreateProductoService(context);
            await service.RegistrarAsync(new ProductoWriteRequest
            {
                NombreProducto = "  Shampoo   Premium  ",
                DetalleProducto = "  Presentacion   grande ",
                PrecioProducto = 12.345m,
                CantidadProducto = 5,
                StockMinimo = 2
            });

            var producto = await context.Productos.AsNoTracking().SingleAsync();
            var movimiento = await context.MovimientosInventario.AsNoTracking().SingleAsync();

            Assert.Equal(tenantId, producto.TenantId);
            Assert.Equal("Shampoo Premium", producto.NombreProducto);
            Assert.Equal("Presentacion grande", producto.DetalleProducto);
            Assert.Equal(12.35m, producto.PrecioProducto);
            Assert.True(producto.Activo);
            Assert.Equal("COMPRA", movimiento.TipoMovimiento);
            Assert.Equal(5, movimiento.Cantidad);
            Assert.Equal(0, movimiento.StockAnterior);
            Assert.Equal(5, movimiento.StockNuevo);
            Assert.Contains("Stock inicial", movimiento.Observacion, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RegistrarAsync_ShouldRejectDuplicateNameWithinTenant()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedProductoAsync(context, "Shampoo Azul", 10m, stock: 2, stockMinimo: 1);

            var service = ControllerTestSupport.CreateProductoService(context);
            var exception = await Assert.ThrowsAsync<ProductoValidationException>(() => service.RegistrarAsync(new ProductoWriteRequest
            {
                NombreProducto = "  Shampoo   Azul  ",
                PrecioProducto = 15m,
                CantidadProducto = 1,
                StockMinimo = 0
            }));

            Assert.Equal("Producto.NombreProducto", exception.ModelStateKey);
            Assert.Single(await context.Productos.ToListAsync());
        }

        [Fact]
        public async Task RegistrarAsync_ShouldAllowSameNameAcrossDifferentTenants()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var service = ControllerTestSupport.CreateProductoService(context);
            await service.RegistrarAsync(new ProductoWriteRequest
            {
                NombreProducto = "Shampoo Unico",
                PrecioProducto = 10m,
                CantidadProducto = 1,
                StockMinimo = 0
            });

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();

            await service.RegistrarAsync(new ProductoWriteRequest
            {
                NombreProducto = "Shampoo Unico",
                PrecioProducto = 20m,
                CantidadProducto = 2,
                StockMinimo = 1
            });

            var productos = await context.Productos
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => p.NombreProducto == "Shampoo Unico")
                .ToListAsync();

            Assert.Equal(2, productos.Count);
            Assert.Equal(2, productos.Select(p => p.TenantId).Distinct().Count());
        }

        [Fact]
        public async Task ActualizarAsync_ShouldNormalizeData_AndCreateAdjustmentMovement_WhenStockChanges()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var producto = await SeedProductoAsync(context, "Aceite", 8m, stock: 5, stockMinimo: 1);
            var service = ControllerTestSupport.CreateProductoService(context);

            await service.ActualizarAsync(producto.IdProducto, new ProductoWriteRequest
            {
                NombreProducto = "  Aceite   Premium ",
                DetalleProducto = "  Aroma   suave ",
                PrecioProducto = 9.499m,
                CantidadProducto = 2,
                StockMinimo = 1
            });

            context.ChangeTracker.Clear();

            var actualizado = await context.Productos.AsNoTracking().SingleAsync();
            var movimiento = await context.MovimientosInventario.AsNoTracking().SingleAsync();

            Assert.Equal("Aceite Premium", actualizado.NombreProducto);
            Assert.Equal("Aroma suave", actualizado.DetalleProducto);
            Assert.Equal(9.50m, actualizado.PrecioProducto);
            Assert.Equal(2, actualizado.CantidadProducto);
            Assert.Equal("AJUSTE", movimiento.TipoMovimiento);
            Assert.Equal(3, movimiento.Cantidad);
            Assert.Equal(5, movimiento.StockAnterior);
            Assert.Equal(2, movimiento.StockNuevo);
            Assert.Contains("5 -> 2", movimiento.Observacion, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ActualizarAsync_ShouldNotCreateMovement_WhenStockDoesNotChange()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var producto = await SeedProductoAsync(context, "Gel", 7m, stock: 4, stockMinimo: 1);
            var service = ControllerTestSupport.CreateProductoService(context);

            await service.ActualizarAsync(producto.IdProducto, new ProductoWriteRequest
            {
                NombreProducto = "  Gel  Fuerte ",
                DetalleProducto = "  Acabado   mate ",
                PrecioProducto = 7m,
                CantidadProducto = 4,
                StockMinimo = 2
            });

            context.ChangeTracker.Clear();

            var actualizado = await context.Productos.AsNoTracking().SingleAsync();
            var movimientos = await context.MovimientosInventario.AsNoTracking().ToListAsync();

            Assert.Equal("Gel Fuerte", actualizado.NombreProducto);
            Assert.Equal("Acabado mate", actualizado.DetalleProducto);
            Assert.Equal(4, actualizado.CantidadProducto);
            Assert.Empty(movimientos);
        }

        [Fact]
        public async Task ActualizarAsync_ShouldRejectForeignProduct()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var producto = await SeedProductoAsync(context, "Locion", 11m, stock: 3, stockMinimo: 1);

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateProductoService(context);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActualizarAsync(producto.IdProducto, new ProductoWriteRequest
            {
                NombreProducto = "Locion Editada",
                PrecioProducto = 11m,
                CantidadProducto = 3,
                StockMinimo = 1
            }));

            Assert.Contains("no existe", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await context.MovimientosInventario.ToListAsync());
        }

        [Fact]
        public async Task ToggleActivoAsync_ShouldToggleProductState()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var producto = await SeedProductoAsync(context, "Serum", 20m, stock: 1, stockMinimo: 0);
            var service = ControllerTestSupport.CreateProductoService(context);

            var firstToggle = await service.ToggleActivoAsync(producto.IdProducto);
            context.ChangeTracker.Clear();
            var afterFirst = await context.Productos.AsNoTracking().SingleAsync();

            var secondToggle = await service.ToggleActivoAsync(producto.IdProducto);
            context.ChangeTracker.Clear();
            var afterSecond = await context.Productos.AsNoTracking().SingleAsync();

            Assert.True(firstToggle);
            Assert.False(afterFirst.Activo);
            Assert.True(secondToggle);
            Assert.True(afterSecond.Activo);
        }

        [Fact]
        public async Task RegistrarAsync_ShouldRejectInvalidPriceStockAndMinimumStock()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var service = ControllerTestSupport.CreateProductoService(context);

            await Assert.ThrowsAsync<ProductoValidationException>(() => service.RegistrarAsync(new ProductoWriteRequest
            {
                NombreProducto = "Precio Invalido",
                PrecioProducto = 0m,
                CantidadProducto = 1,
                StockMinimo = 0
            }));

            await Assert.ThrowsAsync<ProductoValidationException>(() => service.RegistrarAsync(new ProductoWriteRequest
            {
                NombreProducto = "Stock Invalido",
                PrecioProducto = 10m,
                CantidadProducto = -1,
                StockMinimo = 0
            }));

            await Assert.ThrowsAsync<ProductoValidationException>(() => service.RegistrarAsync(new ProductoWriteRequest
            {
                NombreProducto = "Minimo Invalido",
                PrecioProducto = 10m,
                CantidadProducto = 1,
                StockMinimo = -1
            }));

            Assert.Empty(await context.Productos.ToListAsync());
            Assert.Empty(await context.MovimientosInventario.ToListAsync());
        }

        private static async Task<Producto> SeedProductoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            decimal precio,
            int stock,
            int stockMinimo,
            bool activo = true)
        {
            var producto = new Producto
            {
                NombreProducto = nombre,
                DetalleProducto = $"{nombre} detalle",
                PrecioProducto = precio,
                CantidadProducto = stock,
                StockMinimo = stockMinimo,
                Activo = activo,
                FechaRegistro = new DateTime(2026, 4, 23, 9, 0, 0)
            };

            context.Productos.Add(producto);
            await context.SaveChangesAsync();
            return producto;
        }
    }
}
