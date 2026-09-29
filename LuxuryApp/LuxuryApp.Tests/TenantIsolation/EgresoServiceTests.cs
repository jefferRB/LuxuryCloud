using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class EgresoServiceTests
    {
        [Fact]
        public async Task RegistrarAsync_ShouldPersistNormalizedExpense()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Compras", "Operativo");
            var service = ControllerTestSupport.CreateEgresoService(context);

            await service.RegistrarAsync(new EgresoCreateRequest
            {
                FechaEgreso = new DateTime(2026, 4, 23, 10, 15, 49),
                Detalle = "  Pago    proveedor   local  ",
                Monto = 1250.567m,
                MetodoPago = " efectivo ",
                CategoriaId = categoria.Id
            });

            context.ChangeTracker.Clear();

            var egreso = await context.Egresos.AsNoTracking().SingleAsync();
            Assert.Equal(categoria.Id, egreso.CategoriaId);
            Assert.Equal(new DateTime(2026, 4, 23, 10, 15, 0), egreso.FechaEgreso);
            Assert.Equal("Pago proveedor local", egreso.Detalle);
            Assert.Equal(1250.57m, egreso.Monto);
            Assert.Equal("EFECTIVO", egreso.MetodoPago);
        }

        [Fact]
        public async Task RegistrarAsync_ShouldRejectInactiveCategoria()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Compras", "Inactiva", activo: false);
            var service = ControllerTestSupport.CreateEgresoService(context);

            var exception = await Assert.ThrowsAsync<EgresoValidationException>(() => service.RegistrarAsync(new EgresoCreateRequest
            {
                FechaEgreso = new DateTime(2026, 4, 23, 11, 0, 0),
                Detalle = "Intento invalido",
                Monto = 500m,
                MetodoPago = "TARJETA",
                CategoriaId = categoria.Id
            }));

            Assert.Equal("Egreso.CategoriaId", exception.ModelStateKey);
            Assert.Empty(await context.Egresos.ToListAsync());
        }

        [Fact]
        public async Task ActualizarAsync_ShouldUpdateExpenseFields()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Compras", "Operativo");
            context.Egresos.Add(new Egreso
            {
                FechaEgreso = new DateTime(2026, 4, 23, 10, 0, 0),
                Detalle = "Original",
                Monto = 100m,
                MetodoPago = "EFECTIVO",
                CategoriaId = categoria.Id
            });
            await context.SaveChangesAsync();

            var egresoId = await context.Egresos.Select(e => e.IdEgreso).SingleAsync();
            var service = ControllerTestSupport.CreateEgresoService(context);

            var updated = await service.ActualizarAsync(new EgresoUpdateRequest
            {
                IdEgreso = egresoId,
                FechaEgreso = new DateTime(2026, 4, 24, 9, 30, 59),
                Detalle = "  Pago   actualizado ",
                Monto = 250.456m,
                MetodoPago = "tarjeta",
                CategoriaId = categoria.Id
            });

            Assert.True(updated);

            context.ChangeTracker.Clear();
            var egreso = await context.Egresos.AsNoTracking().SingleAsync();
            Assert.Equal(new DateTime(2026, 4, 24, 9, 30, 0), egreso.FechaEgreso);
            Assert.Equal("Pago actualizado", egreso.Detalle);
            Assert.Equal(250.46m, egreso.Monto);
            Assert.Equal("TARJETA", egreso.MetodoPago);
        }

        [Fact]
        public async Task EliminarAsync_ShouldDeleteExpense_WhenItIsNotLinkedToLiquidation()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Compras", "Operativo");
            context.Egresos.Add(new Egreso
            {
                FechaEgreso = new DateTime(2026, 4, 23, 10, 0, 0),
                Detalle = "Eliminar",
                Monto = 100m,
                MetodoPago = "EFECTIVO",
                CategoriaId = categoria.Id
            });
            await context.SaveChangesAsync();

            var egresoId = await context.Egresos.Select(e => e.IdEgreso).SingleAsync();
            var service = ControllerTestSupport.CreateEgresoService(context);

            var deleted = await service.EliminarAsync(egresoId);

            Assert.True(deleted);
            Assert.Empty(await context.Egresos.ToListAsync());
        }

        [Fact]
        public async Task EliminarAsync_ShouldRejectExpenseLinkedToLiquidation()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, LiquidacionSemanalDefaults.CategoriaPagoFuncionarios, "Planilla");
            context.Egresos.Add(new Egreso
            {
                FechaEgreso = new DateTime(2026, 4, 23, 10, 0, 0),
                Detalle = "Liquidacion",
                Monto = 100m,
                MetodoPago = "EFECTIVO",
                CategoriaId = categoria.Id
            });
            await context.SaveChangesAsync();

            var egresoId = await context.Egresos.Select(e => e.IdEgreso).SingleAsync();
            context.LiquidacionesSemanales.Add(new LiquidacionSemanal
            {
                SemanaInicio = new DateTime(2026, 4, 20),
                SemanaFin = new DateTime(2026, 4, 26),
                FechaPago = new DateTime(2026, 4, 23, 10, 0, 0),
                MontoTotal = 100m,
                Estado = LiquidacionSemanalDefaults.EstadoPagada,
                FechaCreacion = new DateTime(2026, 4, 23, 10, 0, 0),
                EgresoId = egresoId
            });
            await context.SaveChangesAsync();

            var service = ControllerTestSupport.CreateEgresoService(context);
            var exception = await Assert.ThrowsAsync<EgresoValidationException>(() => service.EliminarAsync(egresoId));

            // El COMPORTAMIENTO no cambió (sigue rechazando y el egreso sigue ahí); cambió la
            // redacción para indicar la corrección válida: revertir el pago desde Liquidaciones.
            Assert.Contains("liquidación de colaboradores", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("revertí el pago", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await context.Egresos.ToListAsync());
        }

        [Fact]
        public async Task RegistrarAsync_ShouldRejectCrossTenantCategoria()
        {
            var currentTenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = foreignTenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var foreignCategoria = await SeedCategoriaAsync(context, "Externa", "Foreign");

            tenantProvider.TenantId = currentTenantId;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateEgresoService(context);
            var exception = await Assert.ThrowsAsync<EgresoValidationException>(() => service.RegistrarAsync(new EgresoCreateRequest
            {
                FechaEgreso = new DateTime(2026, 4, 23, 12, 0, 0),
                Detalle = "Cross tenant",
                Monto = 300m,
                MetodoPago = "SINPE",
                CategoriaId = foreignCategoria.Id
            }));

            Assert.Equal("Egreso.CategoriaId", exception.ModelStateKey);
            Assert.Empty(await context.Egresos.ToListAsync());
        }

        [Fact]
        public async Task RegistrarAsync_ShouldRejectUnsupportedPaymentMethod()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Compras", "Operativo");
            var service = ControllerTestSupport.CreateEgresoService(context);

            var exception = await Assert.ThrowsAsync<EgresoValidationException>(() => service.RegistrarAsync(new EgresoCreateRequest
            {
                FechaEgreso = new DateTime(2026, 4, 23, 13, 0, 0),
                Detalle = "Metodo invalido",
                Monto = 150m,
                MetodoPago = "CHEQUE",
                CategoriaId = categoria.Id
            }));

            Assert.Equal("Egreso.MetodoPago", exception.ModelStateKey);
            Assert.Empty(await context.Egresos.ToListAsync());
        }

        private static async Task<Categoria> SeedCategoriaAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            string detalle,
            bool activo = true)
        {
            var categoria = new Categoria
            {
                Nombre = nombre,
                Detalle = detalle,
                Activo = activo
            };

            context.Categorias.Add(categoria);
            await context.SaveChangesAsync();
            return categoria;
        }
    }
}
