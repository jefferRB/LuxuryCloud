using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Productos;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class CobroServiceTests
    {
        [Fact]
        public async Task RegistrarAsync_ShouldPersistServiceCharge_UsingSubmittedAmount()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana", porcentajeGanancia: 45m, porcentajeProducto: 10m);
            var servicio = await SeedServicioAsync(context, "Corte", 10000m);

            var service = ControllerTestSupport.CreateCobroService(context);
            await service.RegistrarAsync(new CobroCreateRequest
            {
                FechaCobro = new DateTime(2026, 4, 23, 10, 15, 47),
                NombreCliente = "  Ana   Maria  ",
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Monto = 8000m,
                MetodoPago = "efectivo"
            });

            var cobro = await context.Cobros.SingleAsync();
            Assert.Equal(servicio.Id, cobro.ServicioId);
            Assert.Null(cobro.ProductoId);
            Assert.Equal(8000m, cobro.Monto);
            Assert.Equal("Ana Maria", cobro.NombreCliente);
            Assert.Equal("EFECTIVO", cobro.MetodoPago);
            Assert.Equal(new DateTime(2026, 4, 23, 10, 15, 0), cobro.FechaCobro);

            var persistedServicio = await context.Servicios.AsNoTracking().SingleAsync();
            Assert.Equal(10000m, persistedServicio.Precio);
        }

        [Fact]
        public async Task RegistrarAsync_ShouldPersistProductCharge_AndAdjustInventory()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Luis", porcentajeGanancia: 40m, porcentajeProducto: 12m);
            var producto = await SeedProductoAsync(context, "Shampoo", 25m, stock: 2);

            var service = ControllerTestSupport.CreateCobroService(context);
            await service.RegistrarAsync(new CobroCreateRequest
            {
                FechaCobro = new DateTime(2026, 4, 23, 11, 20, 55),
                NombreCliente = "Cliente Producto",
                FuncionarioId = funcionario.IdFuncionario,
                ProductoId = producto.IdProducto,
                Monto = 25m,
                MetodoPago = "SINPE"
            });

            context.ChangeTracker.Clear();

            var cobro = await context.Cobros.AsNoTracking().SingleAsync();
            var productoActualizado = await context.Productos.AsNoTracking().SingleAsync();
            var detalle = await context.DetalleCobroProductos.AsNoTracking().SingleAsync();
            var movimiento = await context.MovimientosInventario.AsNoTracking().SingleAsync();

            Assert.Equal(producto.IdProducto, cobro.ProductoId);
            Assert.Null(cobro.ServicioId);
            Assert.Equal(25m, cobro.Monto);
            Assert.Equal(1, productoActualizado.CantidadProducto);
            Assert.Equal(cobro.IdCobro, detalle.CobroId);
            Assert.Equal(25m, detalle.Subtotal);
            Assert.Equal("VENTA", movimiento.TipoMovimiento);
            Assert.Equal(2, movimiento.StockAnterior);
            Assert.Equal(1, movimiento.StockNuevo);
        }

        [Fact]
        public async Task ActualizarAsync_ShouldPreserveManualServiceAmount_WithoutChangingServicePrice()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Manual", porcentajeGanancia: 45m, porcentajeProducto: 10m);
            var servicio = await SeedServicioAsync(context, "Corte Premium", 10000m);

            context.Cobros.Add(new Cobro
            {
                FechaCobro = new DateTime(2026, 4, 23, 10, 0, 0),
                NombreCliente = "Cliente Manual",
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Monto = 10000m,
                MetodoPago = "EFECTIVO"
            });
            await context.SaveChangesAsync();

            var cobroId = await context.Cobros.Select(c => c.IdCobro).SingleAsync();
            var service = ControllerTestSupport.CreateCobroService(context);

            var updated = await service.ActualizarAsync(new CobroUpdateRequest
            {
                IdCobro = cobroId,
                FechaCobro = new DateTime(2026, 4, 23, 11, 5, 42),
                NombreCliente = "Cliente Manual Editado",
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Monto = 8000m,
                MetodoPago = "sinpe"
            });

            Assert.True(updated);

            context.ChangeTracker.Clear();
            var cobro = await context.Cobros.AsNoTracking().SingleAsync();
            var servicioPersistido = await context.Servicios.AsNoTracking().SingleAsync();

            Assert.Equal(8000m, cobro.Monto);
            Assert.Equal("SINPE", cobro.MetodoPago);
            Assert.Equal(new DateTime(2026, 4, 23, 11, 5, 0), cobro.FechaCobro);
            Assert.Equal(10000m, servicioPersistido.Precio);
        }

        [Fact]
        public async Task EliminarAsync_ShouldRemoveProductCharge_AndRestoreInventory()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Inventario", porcentajeGanancia: 40m, porcentajeProducto: 12m);
            var producto = await SeedProductoAsync(context, "Pomada", 30m, stock: 2);

            var service = ControllerTestSupport.CreateCobroService(context);
            await service.RegistrarAsync(new CobroCreateRequest
            {
                FechaCobro = new DateTime(2026, 4, 23, 11, 20, 55),
                NombreCliente = "Cliente Producto",
                FuncionarioId = funcionario.IdFuncionario,
                ProductoId = producto.IdProducto,
                Monto = 30m,
                MetodoPago = "SINPE"
            });

            var cobroId = await context.Cobros.Select(c => c.IdCobro).SingleAsync();
            var deleted = await service.EliminarAsync(cobroId);

            Assert.True(deleted);
            Assert.Empty(await context.Cobros.ToListAsync());
            Assert.Empty(await context.DetalleCobroProductos.ToListAsync());

            var productoActualizado = await context.Productos.AsNoTracking().SingleAsync();
            Assert.Equal(2, productoActualizado.CantidadProducto);
            Assert.Contains(await context.MovimientosInventario.AsNoTracking().ToListAsync(), m => m.TipoMovimiento == "ANULACION_VENTA");
        }

        [Fact]
        public async Task RegistrarAsync_ShouldReject_WhenProductHasNoStock()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Paola");
            var producto = await SeedProductoAsync(context, "Crema", 15m, stock: 0);

            var service = ControllerTestSupport.CreateCobroService(context);
            var exception = await Assert.ThrowsAsync<CobroValidationException>(() => service.RegistrarAsync(new CobroCreateRequest
            {
                FechaCobro = new DateTime(2026, 4, 23, 12, 0, 0),
                NombreCliente = "Sin Stock",
                FuncionarioId = funcionario.IdFuncionario,
                ProductoId = producto.IdProducto,
                Monto = 15m,
                MetodoPago = "TARJETA"
            }));

            Assert.Contains("stock", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await context.Cobros.ToListAsync());
            Assert.Empty(await context.DetalleCobroProductos.ToListAsync());
            Assert.Empty(await context.MovimientosInventario.ToListAsync());
        }

        [Fact]
        public async Task RegistrarAsync_ShouldPersistCustomServiceCharge_FromCita_WithoutCatalogService()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Personalizado", porcentajeGanancia: 50m, porcentajeProducto: 10m);
            // Cita con servicio personalizado: sin ServicioId y sin precio base.
            var cita = await SeedCitaPersonalizadaAsync(context, funcionario.IdFuncionario, "Diseño especial");

            var service = ControllerTestSupport.CreateCobroService(context);
            await service.RegistrarAsync(new CobroCreateRequest
            {
                FechaCobro = new DateTime(2026, 4, 23, 16, 0, 0),
                NombreCliente = "Cliente Personalizado",
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = null,
                ServicioNombrePersonalizado = "Diseño especial",
                CitaId = cita.Id,
                Monto = 12500m,
                MetodoPago = "EFECTIVO"
            });

            context.ChangeTracker.Clear();
            var cobro = await context.Cobros.AsNoTracking().SingleAsync();

            Assert.Null(cobro.ServicioId);
            Assert.Null(cobro.ProductoId);
            Assert.Equal("Diseño especial", cobro.ServicioNombrePersonalizado);
            Assert.Equal(cita.Id, cobro.CitaId);
            Assert.Equal(12500m, cobro.Monto);
            Assert.Equal("EFECTIVO", cobro.MetodoPago);
        }

        [Fact]
        public async Task RegistrarAsync_ShouldRejectCustomService_WhenNotLinkedToCita()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "SinCita");
            var service = ControllerTestSupport.CreateCobroService(context);

            // Un nombre personalizado sin cita de origen no es un cobro válido: debe exigir
            // servicio de catálogo o producto. Evita cobros "sueltos" sin servicio ni producto.
            await Assert.ThrowsAsync<CobroValidationException>(() => service.RegistrarAsync(new CobroCreateRequest
            {
                FechaCobro = new DateTime(2026, 4, 23, 17, 0, 0),
                NombreCliente = "Sin Cita",
                FuncionarioId = funcionario.IdFuncionario,
                ServicioNombrePersonalizado = "Algo suelto",
                Monto = 5000m,
                MetodoPago = "EFECTIVO"
            }));

            Assert.Empty(await context.Cobros.ToListAsync());
        }

        [Fact]
        public async Task RegistrarAsync_ShouldRejectCustomService_WhenCitaAlreadyCharged()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "DobleCobro");
            var cita = await SeedCitaPersonalizadaAsync(context, funcionario.IdFuncionario, "Corte libre");
            var service = ControllerTestSupport.CreateCobroService(context);

            var request = new CobroCreateRequest
            {
                FechaCobro = new DateTime(2026, 4, 23, 18, 0, 0),
                NombreCliente = "Cliente",
                FuncionarioId = funcionario.IdFuncionario,
                ServicioNombrePersonalizado = "Corte libre",
                CitaId = cita.Id,
                Monto = 7000m,
                MetodoPago = "EFECTIVO"
            };

            await service.RegistrarAsync(request);

            // Segundo intento sobre la misma cita: anti doble cobro.
            await Assert.ThrowsAsync<CobroValidationException>(() => service.RegistrarAsync(request));
            Assert.Single(await context.Cobros.ToListAsync());
        }

        [Fact]
        public async Task RegistrarAsync_ShouldReject_WhenServicioAndProductoAreProvidedTogether()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Carlos");
            var servicio = await SeedServicioAsync(context, "Manicure", 40m);
            var producto = await SeedProductoAsync(context, "Aceite", 10m, stock: 3);

            var service = ControllerTestSupport.CreateCobroService(context);
            var exception = await Assert.ThrowsAsync<CobroValidationException>(() => service.RegistrarAsync(new CobroCreateRequest
            {
                FechaCobro = new DateTime(2026, 4, 23, 13, 0, 0),
                NombreCliente = "Conflicto",
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                ProductoId = producto.IdProducto,
                MetodoPago = "EFECTIVO"
            }));

            Assert.Contains("no ambos", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task RegistrarAsync_ShouldRejectCrossTenantFuncionario()
        {
            var currentTenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = foreignTenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var foreignFuncionario = await SeedFuncionarioAsync(context, "Funcionario Externo");

            tenantProvider.TenantId = currentTenantId;
            context.ChangeTracker.Clear();

            var servicio = await SeedServicioAsync(context, "Servicio Local", 55m);
            var service = ControllerTestSupport.CreateCobroService(context);

            var exception = await Assert.ThrowsAsync<CobroValidationException>(() => service.RegistrarAsync(new CobroCreateRequest
            {
                FechaCobro = new DateTime(2026, 4, 23, 14, 0, 0),
                NombreCliente = "Tenant Check",
                FuncionarioId = foreignFuncionario.IdFuncionario,
                ServicioId = servicio.Id,
                Monto = 55m,
                MetodoPago = "EFECTIVO"
            }));

            Assert.Equal("Cobro.FuncionarioId", exception.ModelStateKey);
            Assert.Empty(await context.Cobros.ToListAsync());
        }

        [Fact]
        public async Task RegistrarAsync_ShouldRejectCrossTenantServicio()
        {
            var currentTenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = foreignTenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var foreignServicio = await SeedServicioAsync(context, "Servicio Externo", 80m);

            tenantProvider.TenantId = currentTenantId;
            context.ChangeTracker.Clear();

            var funcionario = await SeedFuncionarioAsync(context, "Funcionario Local");
            var service = ControllerTestSupport.CreateCobroService(context);

            var exception = await Assert.ThrowsAsync<CobroValidationException>(() => service.RegistrarAsync(new CobroCreateRequest
            {
                FechaCobro = new DateTime(2026, 4, 23, 15, 0, 0),
                NombreCliente = "Servicio Tenant",
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = foreignServicio.Id,
                Monto = 80m,
                MetodoPago = "TARJETA"
            }));

            Assert.Equal("Cobro.ServicioId", exception.ModelStateKey);
            Assert.Empty(await context.Cobros.ToListAsync());
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            decimal porcentajeGanancia = 40m,
            decimal porcentajeProducto = 10m,
            bool activo = true)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                Detalle = "Operativo",
                Activo = true
            };

            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = porcentajeGanancia,
                PorcentajeProducto = porcentajeProducto,
                FechaIngreso = new DateTime(2026, 4, 1),
                Activo = activo
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task<LuxuryApp.Models.Calendar.Cita> SeedCitaPersonalizadaAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            string nombrePersonalizado)
        {
            var cita = new LuxuryApp.Models.Calendar.Cita
            {
                NombreCliente = "Cliente Personalizado",
                FuncionarioId = funcionarioId,
                ServicioId = null,
                ServicioNombrePersonalizado = nombrePersonalizado,
                FechaHoraCita = new DateTime(2026, 4, 23, 16, 0, 0),
                Tipo = "CITA",
                DuracionMinutos = 30
            };

            context.Citas.Add(cita);
            await context.SaveChangesAsync();
            return cita;
        }

        private static async Task<Servicio> SeedServicioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            decimal precio)
        {
            var servicio = new Servicio
            {
                Nombre = nombre,
                Precio = precio,
                DuracionMinutos = 45,
                Activo = true
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<Producto> SeedProductoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            decimal precio,
            int stock)
        {
            var producto = new Producto
            {
                NombreProducto = nombre,
                PrecioProducto = precio,
                CantidadProducto = stock,
                Activo = true,
                FechaRegistro = new DateTime(2026, 4, 1)
            };

            context.Productos.Add(producto);
            await context.SaveChangesAsync();
            return producto;
        }
    }
}
