using ClosedXML.Excel;
using LuxuryApp.Controllers.Finanzas;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class CobrosControllerTests
    {
        [Fact]
        public async Task ExportarExcel_ShouldReturnWorkbook_WithSummaryAndRows()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Exportador");
            await SeedCobroServicioAsync(context, funcionario, "Corte Export", 100m, "EFECTIVO", "Cliente Servicio");
            await SeedCobroServicioAsync(context, funcionario, "Peinado Export", 150m, "SINPE", "Cliente Sinpe");

            var controller = new CobrosController(
                ControllerTestSupport.CreateCobroService(context),
                ControllerTestSupport.CreateCobroQueryService(context, tenantProvider),
                ControllerTestSupport.CreateComprobanteCobroService(),
                ControllerTestSupport.BusinessDateTimeProvider,
                ControllerTestSupport.CreateTenantDisplayNameService());
            ControllerTestSupport.AttachHttpContext(controller);

            var result = await controller.ExportarExcel(new CobroFiltroViewModel { VistaTiempo = "todo" });

            var file = Assert.IsType<FileContentResult>(result);
            using var workbook = new XLWorkbook(new MemoryStream(file.FileContents));
            var worksheet = workbook.Worksheet("Reporte Cobros");
            var text = string.Join("|", worksheet.CellsUsed().Select(cell => cell.GetString()));

            Assert.Contains("Reporte Financiero de Cobros", text, StringComparison.Ordinal);
            Assert.Contains("Cliente Servicio", text, StringComparison.Ordinal);
            Assert.Contains("Cliente Sinpe", text, StringComparison.Ordinal);
            Assert.Contains("TOTAL GENERAL", text, StringComparison.Ordinal);
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
                ColorCalendario = "#444444",
                PorcentajeGanancia = 40m,
                PorcentajeProducto = 10m,
                FechaIngreso = new DateTime(2026, 4, 1),
                Activo = true
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task SeedCobroServicioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Funcionario funcionario,
            string servicioNombre,
            decimal monto,
            string metodoPago,
            string cliente)
        {
            var servicio = new Servicio
            {
                Nombre = servicioNombre,
                Precio = monto,
                DuracionMinutos = 45,
                Activo = true
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();

            context.Cobros.Add(new Cobro
            {
                FechaCobro = new DateTime(2026, 4, 23, 9, 0, 0),
                NombreCliente = cliente,
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Monto = monto,
                MetodoPago = metodoPago
            });

            await context.SaveChangesAsync();
        }
    }
}
