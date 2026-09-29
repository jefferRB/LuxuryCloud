using LuxuryApp.Controllers.Productos;
using LuxuryApp.Models.Productos;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ProductosControllerTests
    {
        [Fact]
        public async Task ToggleActivo_ShouldReturnOk_AndUpdateState()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var producto = new Producto
            {
                NombreProducto = "Mascarilla",
                PrecioProducto = 12m,
                CantidadProducto = 2,
                StockMinimo = 1,
                Activo = true
            };

            context.Productos.Add(producto);
            await context.SaveChangesAsync();

            var controller = new ProductosController(
                ControllerTestSupport.CreateProductoService(context),
                ControllerTestSupport.CreateProductoQueryService(context),
                NullLogger<ProductosController>.Instance,
                new LuxuryApp.Services.Finanzas.LegacyFinancialImpactService(context));

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("user-productos", tenantId));

            var result = await controller.ToggleActivo(producto.IdProducto, CancellationToken.None);

            var ok = Assert.IsType<OkResult>(result);
            Assert.Equal(200, ok.StatusCode);
            Assert.False(context.Productos.Single().Activo);
        }
    }
}
