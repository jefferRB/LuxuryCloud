using LuxuryApp.Models.Productos;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ProductoQueryServiceTests
    {
        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldReturnTenantScopedRowsAndKpis()
        {
            var tenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedProductoAsync(context, "Shampoo", 10m, stock: 3, stockMinimo: 1, activo: true);
            await SeedProductoAsync(context, "Cera", 5m, stock: 1, stockMinimo: 2, activo: false);

            tenantProvider.TenantId = foreignTenantId;
            context.ChangeTracker.Clear();
            await SeedProductoAsync(context, "Producto Externo", 100m, stock: 4, stockMinimo: 1, activo: true);

            tenantProvider.TenantId = tenantId;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateProductoQueryService(context);
            var result = await service.BuildIndexViewModelAsync();

            Assert.Equal(2, result.TotalProductos);
            Assert.Equal(1, result.ProductosBajoStock);
            Assert.Equal(35m, result.ValorInventario);
            Assert.Collection(
                result.Productos,
                row => Assert.Equal("Cera", row.NombreProducto),
                row => Assert.Equal("Shampoo", row.NombreProducto));
            Assert.DoesNotContain(result.Productos, row => row.NombreProducto == "Producto Externo");
        }

        [Fact]
        public async Task BuildEditViewModelAsync_ShouldReturnNull_ForForeignProduct()
        {
            var tenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var producto = await SeedProductoAsync(context, "Locion", 15m, stock: 2, stockMinimo: 1, activo: true);

            tenantProvider.TenantId = foreignTenantId;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateProductoQueryService(context);
            var result = await service.BuildEditViewModelAsync(producto.IdProducto);

            Assert.Null(result);
        }

        private static async Task<Producto> SeedProductoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            decimal precio,
            int stock,
            int stockMinimo,
            bool activo)
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
