using LuxuryApp.Controllers.Funcionarios;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PuestosControllerTests
    {
        [Fact]
        public async Task Save_ShouldUpdateExistingPuesto()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Puestos.Add(new Puesto
            {
                NombrePuesto = "Colorista",
                Detalle = "Cabello"
            });
            await context.SaveChangesAsync();

            var puesto = await context.Puestos.SingleAsync();
            var controller = CreateController(context, tenantId);

            var result = await controller.Save(new Puesto
            {
                IdPuesto = puesto.IdPuesto,
                NombrePuesto = "Colorista Senior",
                Detalle = "Cabello y asesoria"
            });

            Assert.IsType<OkResult>(result);

            var puestoActualizado = await context.Puestos.SingleAsync();
            Assert.Equal("Colorista Senior", puestoActualizado.NombrePuesto);
            Assert.Equal("Cabello y asesoria", puestoActualizado.Detalle);
        }

        [Fact]
        public async Task Save_ShouldNormalizeAndRejectDuplicateNames()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Puestos.Add(new Puesto
            {
                NombrePuesto = "Barbero Senior",
                Detalle = "Cabina 1"
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.Save(new Puesto
            {
                NombrePuesto = "  Barbero   Senior  ",
                Detalle = "  Cabina   2  "
            });

            var partial = Assert.IsType<PartialViewResult>(result);
            var model = Assert.IsType<Puesto>(partial.Model);

            Assert.Equal("Barbero Senior", model.NombrePuesto);
            Assert.Equal("Cabina 2", model.Detalle);
            Assert.False(controller.ModelState.IsValid);
            Assert.Contains(nameof(Puesto.NombrePuesto), controller.ModelState.Keys);
            Assert.Single(await context.Puestos.ToListAsync());
        }

        [Fact]
        public async Task ToggleActivo_ShouldAllowDeactivation_WhenOnlyInactiveFuncionariosExist()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var puesto = new Puesto
            {
                NombrePuesto = "Masajista",
                Detalle = "Spa"
            };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            context.Funcionarios.Add(new Funcionario
            {
                Nombre = "Inactiva",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = 40,
                PorcentajeProducto = 10,
                FechaIngreso = new DateTime(2026, 4, 13),
                Activo = false
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.ToggleActivo(puesto.IdPuesto);

            Assert.IsType<OkResult>(result);
            var persisted = await context.Puestos.SingleAsync();
            Assert.False(persisted.Activo);
        }

        [Fact]
        public async Task ToggleActivo_ShouldBlockDeactivation_WhenActiveFuncionariosExist()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var puesto = new Puesto
            {
                NombrePuesto = "Barbero",
                Detalle = "Cabina 1"
            };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            context.Funcionarios.Add(new Funcionario
            {
                Nombre = "Luis",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#444444",
                PorcentajeGanancia = 35,
                PorcentajeProducto = 5,
                FechaIngreso = new DateTime(2026, 4, 13),
                Activo = true
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.ToggleActivo(puesto.IdPuesto);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("funcionarios activos", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

            var persisted = await context.Puestos.SingleAsync();
            Assert.True(persisted.Activo);
        }

        [Fact]
        public async Task Delete_ShouldRemovePuesto_WhenNoFuncionariosAreAssociated()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Puestos.Add(new Puesto
            {
                NombrePuesto = "Masajista",
                Detalle = "Spa"
            });
            await context.SaveChangesAsync();

            var puesto = await context.Puestos.SingleAsync();
            var controller = CreateController(context, tenantId);

            var result = await controller.Delete(puesto.IdPuesto);

            Assert.IsType<OkResult>(result);
            Assert.Empty(await context.Puestos.ToListAsync());
        }

        [Fact]
        public async Task Delete_ShouldBlockPuesto_WhenFuncionariosExist()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var puesto = new Puesto
            {
                NombrePuesto = "Barbero",
                Detalle = "Cabina 1"
            };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            context.Funcionarios.Add(new Funcionario
            {
                Nombre = "Luis",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#444444",
                PorcentajeGanancia = 35,
                PorcentajeProducto = 5,
                FechaIngreso = new DateTime(2026, 4, 13),
                Activo = false
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.Delete(puesto.IdPuesto);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("funcionarios asociados", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Single(await context.Puestos.ToListAsync());
        }

        private static PuestosController CreateController(ProyectoIdentity.Datos.ApplicationDbContext context, Guid tenantId)
        {
            var controller = new PuestosController(
                context,
                NullLogger<PuestosController>.Instance);

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("user-puestos", tenantId));

            return controller;
        }
    }
}
