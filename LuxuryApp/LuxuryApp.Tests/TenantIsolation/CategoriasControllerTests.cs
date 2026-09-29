using LuxuryApp.Controllers.Finanzas;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class CategoriasControllerTests
    {
        [Fact]
        public async Task Save_ShouldUpdateExistingCategoria()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Marketing", "Ads");
            var controller = CreateController(context, tenantId);

            var result = await controller.Save(new Categoria
            {
                Id = categoria.Id,
                Nombre = "Marketing Digital",
                Detalle = "Ads y campanas"
            });

            Assert.IsType<OkResult>(result);

            var categoriaActualizada = await context.Categorias.SingleAsync();
            Assert.Equal("Marketing Digital", categoriaActualizada.Nombre);
            Assert.Equal("Ads y campanas", categoriaActualizada.Detalle);
        }

        [Fact]
        public async Task Save_ShouldRejectDuplicateNombreWithinTenant()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedCategoriaAsync(context, "Compras", "Compras generales");
            var controller = CreateController(context, tenantId);

            var result = await controller.Save(new Categoria
            {
                Nombre = " compras ",
                Detalle = "Intento duplicado"
            });

            var partial = Assert.IsType<PartialViewResult>(result);
            var model = Assert.IsType<Categoria>(partial.Model);

            Assert.Equal("compras", model.Nombre, ignoreCase: true);
            Assert.Single(await context.Categorias.ToListAsync());
        }

        [Fact]
        public async Task Save_ShouldAllowSameNombreAcrossDifferentTenants()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedCategoriaAsync(context, "Servicios", "Tenant A");

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantB);
            var result = await controller.Save(new Categoria
            {
                Nombre = "Servicios",
                Detalle = "Tenant B"
            });

            Assert.IsType<OkResult>(result);
            Assert.Single(await context.Categorias.ToListAsync());

            tenantProvider.TenantId = Guid.Empty;
            context.ChangeTracker.Clear();
            Assert.Equal(2, await context.Categorias.IgnoreQueryFilters().CountAsync());
        }

        [Fact]
        public async Task ModalCategorias_ShouldOnlyReturnCurrentTenantRows()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedCategoriaAsync(context, "Visible", "Tenant A");

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();
            await SeedCategoriaAsync(context, "Oculta", "Tenant B");

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantA);
            var result = await controller.ModalCategorias();

            var partial = Assert.IsType<PartialViewResult>(result);
            var categorias = Assert.IsAssignableFrom<IEnumerable<Categoria>>(partial.Model);
            var nombres = categorias.Select(c => c.Nombre).ToArray();

            Assert.Contains("Visible", nombres, StringComparer.Ordinal);
            Assert.DoesNotContain("Oculta", nombres, StringComparer.Ordinal);
        }

        [Fact]
        public async Task Delete_ShouldRemoveCategoria_WhenItIsUnused()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Papeleria", "Insumos");
            var controller = CreateController(context, tenantId);

            var result = await controller.Delete(categoria.Id);

            Assert.IsType<OkResult>(result);
            Assert.Empty(await context.Categorias.ToListAsync());
        }

        [Fact]
        public async Task Delete_ShouldBlockCategoria_WhenUsedByEgresos()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Servicios", "Pagos recurrentes");
            context.Egresos.Add(new Egreso
            {
                FechaEgreso = new DateTime(2026, 4, 13),
                Detalle = "Pago proveedor",
                Monto = 150,
                MetodoPago = "EFECTIVO",
                CategoriaId = categoria.Id
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.Delete(categoria.Id);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("ya esta siendo usada", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Single(await context.Categorias.ToListAsync());
        }

        private static CategoriasController CreateController(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId)
        {
            var controller = new CategoriasController(
                context,
                NullLogger<CategoriasController>.Instance);

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("user-categorias", tenantId));

            return controller;
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
