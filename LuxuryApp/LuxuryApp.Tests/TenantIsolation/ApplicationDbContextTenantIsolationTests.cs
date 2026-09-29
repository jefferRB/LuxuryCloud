using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Productos;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ApplicationDbContextTenantIsolationTests
    {
        [Fact]
        public async Task QueryFilter_ShouldOnlyReturnRowsForCurrentTenant()
        {
            var tenantProvider = new TestTenantProvider();
            using var bundle = CreateSeededContext(tenantProvider);

            tenantProvider.TenantId = bundle.TenantA;

            var productos = await bundle.Context.Productos
                .OrderBy(p => p.NombreProducto)
                .ToListAsync();

            Assert.Single(productos);
            Assert.Equal(bundle.TenantA, productos[0].TenantId);
            Assert.Equal("Producto Tenant A", productos[0].NombreProducto);
        }

        [Fact]
        public async Task DetachedUpdate_ShouldRejectCrossTenantEntity()
        {
            var tenantProvider = new TestTenantProvider();
            using var bundle = CreateSeededContext(tenantProvider);

            tenantProvider.TenantId = bundle.TenantA;

            var productoAjeno = new Producto
            {
                IdProducto = bundle.ProductoTenantBId,
                TenantId = bundle.TenantA,
                NombreProducto = "Intento malicioso",
                PrecioProducto = 999,
                CantidadProducto = 1,
                StockMinimo = 1,
                Activo = true
            };

            bundle.Context.Attach(productoAjeno);
            bundle.Context.Entry(productoAjeno).State = EntityState.Modified;

            var ex = await Assert.ThrowsAsync<Exception>(() => bundle.Context.SaveChangesAsync());

            Assert.True(
                ex.Message.Contains("otro tenant", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("no existe", StringComparison.OrdinalIgnoreCase),
                $"Mensaje inesperado: {ex.Message}");
        }

        [Fact]
        public async Task CrossTenantForeignKey_ShouldBeRejected()
        {
            var tenantProvider = new TestTenantProvider();
            using var bundle = CreateSeededContext(tenantProvider);

            tenantProvider.TenantId = bundle.TenantA;

            bundle.Context.Egresos.Add(new Egreso
            {
                FechaEgreso = DateTime.UtcNow,
                Detalle = "Intento cross-tenant",
                MetodoPago = "EFECTIVO",
                Monto = 100,
                CategoriaId = bundle.CategoriaTenantBId
            });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bundle.Context.SaveChangesAsync());

            Assert.True(
                ex.Message.Contains("otro tenant", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("no existe", StringComparison.OrdinalIgnoreCase),
                $"Mensaje inesperado: {ex.Message}");
        }

        [Fact]
        public async Task TenantScopedEntity_CanReferenceGlobalPlan()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant A",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Global",
                PrecioMensual = 19,
                Moneda = "CRC",
                Activo = true
            });

            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantId;

            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = "LXA-ABCDEF-1234567890",
                ProviderReference = "LXA-ABCDEF-1234567890",
                Estado = EstadoPagoProveedor.Pendiente,
                Descripcion = "Checkout con plan global",
                Monto = 19,
                Moneda = "CRC"
            });

            await context.SaveChangesAsync();

            var pago = await context.PagosSuscripcion
                .IgnoreQueryFilters()
                .SingleAsync();

            Assert.Equal(tenantId, pago.TenantId);
            Assert.Equal(planId, pago.PlanId);
        }

        private static SeededBundle CreateSeededContext(TestTenantProvider tenantProvider)
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();

            tenantProvider.TenantId = tenantA;
            context.Productos.Add(new Producto
            {
                NombreProducto = "Producto Tenant A",
                PrecioProducto = 1000,
                CantidadProducto = 5,
                StockMinimo = 1,
                Activo = true
            });
            context.Categorias.Add(new Categoria
            {
                Nombre = "Categoria A",
                Detalle = "Categoria tenant A",
                Activo = true
            });
            context.SaveChanges();

            var productoTenantAId = context.Productos.Single().IdProducto;
            var categoriaTenantAId = context.Categorias.Single().Id;

            tenantProvider.TenantId = tenantB;
            context.Productos.Add(new Producto
            {
                NombreProducto = "Producto Tenant B",
                PrecioProducto = 2000,
                CantidadProducto = 8,
                StockMinimo = 2,
                Activo = true
            });
            context.Categorias.Add(new Categoria
            {
                Nombre = "Categoria B",
                Detalle = "Categoria tenant B",
                Activo = true
            });
            context.SaveChanges();

            var productoTenantBId = context.Productos.Single(p => p.TenantId == tenantB).IdProducto;
            var categoriaTenantBId = context.Categorias.Single(c => c.TenantId == tenantB).Id;

            tenantProvider.TenantId = Guid.Empty;
            context.ChangeTracker.Clear();

            return new SeededBundle(
                context,
                connection,
                tenantA,
                tenantB,
                productoTenantAId,
                productoTenantBId,
                categoriaTenantAId,
                categoriaTenantBId);
        }

        private sealed record SeededBundle(
            ApplicationDbContext Context,
            IDisposable Connection,
            Guid TenantA,
            Guid TenantB,
            int ProductoTenantAId,
            int ProductoTenantBId,
            int CategoriaTenantAId,
            int CategoriaTenantBId) : IDisposable
        {
            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }
    }
}
