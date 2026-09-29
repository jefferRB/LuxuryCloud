using LuxuryApp.Controllers.Finanzas;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ServiciosControllerTests
    {
        [Fact]
        public async Task Save_ShouldPersistServicio_WhenPayloadIsValid()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId);
            var result = await controller.Save(new Servicio
            {
                Nombre = "  Tratamiento   Capilar  ",
                Precio = 15.129m,
                DuracionMinutos = 45
            });

            Assert.IsType<OkResult>(result);

            var servicio = await context.Servicios.SingleAsync();
            Assert.Equal("Tratamiento Capilar", servicio.Nombre);
            Assert.Equal(15.13m, servicio.Precio);
            Assert.True(servicio.Activo);
        }

        [Fact]
        public async Task Save_ShouldNormalizeAndRejectDuplicateNames()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Servicios.Add(new Servicio
            {
                Nombre = "Corte Premium",
                Precio = 20m,
                DuracionMinutos = 45,
                Activo = true
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.Save(new Servicio
            {
                Nombre = "  Corte   Premium  ",
                Precio = 22m,
                DuracionMinutos = 50
            });

            var partial = Assert.IsType<PartialViewResult>(result);
            var model = Assert.IsType<Servicio>(partial.Model);

            Assert.False(controller.ModelState.IsValid);
            Assert.Equal("Corte Premium", model.Nombre);
            Assert.Contains(nameof(Servicio.Nombre), controller.ModelState.Keys);
            Assert.Single(await context.Servicios.ToListAsync());
        }

        [Fact]
        public async Task ToggleActivo_ShouldUpdateServicioState()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Servicios.Add(new Servicio
            {
                Nombre = "Pedicure",
                Precio = 35m,
                DuracionMinutos = 60,
                Activo = true
            });
            await context.SaveChangesAsync();

            var servicioId = await context.Servicios.Select(s => s.Id).SingleAsync();
            var controller = CreateController(context, tenantId);

            var result = await controller.ToggleActivo(servicioId);

            Assert.IsType<OkResult>(result);
            Assert.False((await context.Servicios.SingleAsync()).Activo);
        }

        [Fact]
        public async Task Eliminar_ShouldBlockServicio_WhenCobrosExist()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Cobros");
            var servicio = new Servicio
            {
                Nombre = "Servicio Cobrado",
                Precio = 50m,
                DuracionMinutos = 30,
                Activo = true
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();

            context.Cobros.Add(new Cobro
            {
                NombreCliente = "Cliente",
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                FechaCobro = new DateTime(2026, 4, 23, 8, 0, 0),
                Monto = 50m,
                MetodoPago = "EFECTIVO"
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.Eliminar(servicio.Id);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("cobros asociados", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Eliminar_ShouldBlockServicio_WhenCitasExist()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Agenda");
            var servicio = new Servicio
            {
                Nombre = "Servicio Agenda",
                Precio = 45m,
                DuracionMinutos = 30,
                Activo = true
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();

            context.Citas.Add(new Cita
            {
                ServicioId = servicio.Id,
                FuncionarioId = funcionario.IdFuncionario,
                FechaHoraCita = new DateTime(2026, 4, 24, 10, 0, 0),
                NombreCliente = "Agenda"
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.Eliminar(servicio.Id);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("citas asociadas", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ModalServicios_ShouldReturnOnlyCurrentTenantProjectedRows()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Servicios.Add(new Servicio
            {
                Nombre = "Servicio Privado",
                Precio = 30m,
                DuracionMinutos = 40,
                Activo = true
            });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            context.Servicios.Add(new Servicio
            {
                Nombre = "Servicio Visible",
                Precio = 35m,
                DuracionMinutos = 50,
                Activo = true
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantA);
            var result = await controller.ModalServicios();

            var partial = Assert.IsType<PartialViewResult>(result);
            var model = Assert.IsAssignableFrom<IReadOnlyList<ServicioListItemViewModel>>(partial.Model);
            var servicio = Assert.Single(model);
            Assert.Equal("Servicio Visible", servicio.Nombre);
        }

        private static ServiciosController CreateController(ProyectoIdentity.Datos.ApplicationDbContext context, Guid tenantId)
        {
            var controller = new ServiciosController(context, NullLogger<ServiciosController>.Instance, new LuxuryApp.Services.Finanzas.LegacyFinancialImpactService(context));
            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("user-servicios", tenantId));
            return controller;
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre)
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
                ColorCalendario = "#999999",
                PorcentajeGanancia = 40m,
                PorcentajeProducto = 10m,
                FechaIngreso = new DateTime(2026, 4, 1),
                Activo = true
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }
    }
}
