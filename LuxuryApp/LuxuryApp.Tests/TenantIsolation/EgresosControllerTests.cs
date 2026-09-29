using ClosedXML.Excel;
using LuxuryApp.Controllers.Finanzas;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class EgresosControllerTests
    {
        [Fact]
        public async Task Create_ShouldPersistExpenseAndRedirect()
        {
            var tenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Operativo", "Caja");
            var controller = CreateController(context, tenantProvider, tenantId);

            var result = await controller.Create(new EgresoViewModel
            {
                Egreso = new Egreso
                {
                    TenantId = foreignTenantId,
                    FechaEgreso = new DateTime(2026, 4, 23, 10, 45, 45),
                    Detalle = "  Salida   de   caja ",
                    Monto = 125m,
                    MetodoPago = "sinpe",
                    CategoriaId = categoria.Id
                }
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(EgresosController.Index), redirect.ActionName);

            var egreso = context.Egresos.Single();
            Assert.Equal(tenantId, egreso.TenantId);
            Assert.Equal("Salida de caja", egreso.Detalle);
            Assert.Equal("SINPE", egreso.MetodoPago);
        }

        [Fact]
        public async Task ExportarExcel_ShouldReturnWorkbook_WithSummaryAndRows()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Operativo", "Caja");
            await SeedTenantDisplayNameAsync(context, tenantId, "Jorhanna Diaz", "Barberia jor");
            await SeedEgresoAsync(context, categoria, new DateTime(2026, 4, 23, 9, 0, 0), 100m, "EFECTIVO", "Compra Insumos");
            await SeedEgresoAsync(context, categoria, new DateTime(2026, 4, 23, 10, 0, 0), 150m, "TARJETA", "Pago Servicios");

            var controller = CreateController(context, tenantProvider, tenantId);
            var result = await controller.ExportarExcel(new EgresoFiltroViewModel { VistaTiempo = "todo" });

            var file = Assert.IsType<FileContentResult>(result);
            Assert.Equal("BarberiaJor_ReporteEgresos_2026-05-26.xlsx", file.FileDownloadName);
            Assert.DoesNotContain("LuxeReporteEgresos", file.FileDownloadName, StringComparison.OrdinalIgnoreCase);
            using var workbook = new XLWorkbook(new MemoryStream(file.FileContents));
            var worksheet = workbook.Worksheet("Reporte Egresos");
            var text = string.Join("|", worksheet.CellsUsed().Select(cell => cell.GetString()));

            Assert.Contains("Barberia jor", text, StringComparison.Ordinal);
            Assert.Contains("Reporte Financiero de Egresos", text, StringComparison.Ordinal);
            Assert.Contains("Compra Insumos", text, StringComparison.Ordinal);
            Assert.Contains("Pago Servicios", text, StringComparison.Ordinal);
            Assert.Contains("Monto Total", text, StringComparison.Ordinal);
        }

        private static EgresosController CreateController(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            Guid tenantId)
        {
            var controller = new EgresosController(
                ControllerTestSupport.CreateEgresoService(context),
                ControllerTestSupport.CreateEgresoQueryService(context),
                ControllerTestSupport.BusinessDateTimeProvider,
                ControllerTestSupport.CreateTenantDisplayNameService(context, tenantProvider));

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("user-egresos", tenantId));

            return controller;
        }

        private static async Task SeedTenantDisplayNameAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            string tenantName,
            string displayName)
        {
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = tenantName,
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = $"owner-{tenantId:N}",
                TenantId = tenantId,
                UserName = $"owner-{tenantId:N}@test.local",
                Email = $"owner-{tenantId:N}@test.local",
                Name = displayName,
                State = true
            });

            await context.SaveChangesAsync();
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

        private static async Task SeedEgresoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Categoria categoria,
            DateTime fecha,
            decimal monto,
            string metodoPago,
            string detalle)
        {
            context.Egresos.Add(new Egreso
            {
                FechaEgreso = fecha,
                CategoriaId = categoria.Id,
                Detalle = detalle,
                Monto = monto,
                MetodoPago = metodoPago
            });

            await context.SaveChangesAsync();
        }
    }
}
