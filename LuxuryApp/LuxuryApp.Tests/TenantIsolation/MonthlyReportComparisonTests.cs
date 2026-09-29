using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Reports;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class MonthlyReportComparisonTests
    {
        [Fact]
        public async Task Comparison_IncomeUp_ComputesPositiveVariation()
        {
            var (context, connection, tenantProvider) = CreateContext();
            using var _ = context;
            using var __ = connection;

            var (funcionario, servicio) = await SeedBaseAsync(context);
            // Marzo (mes anterior) 100; Abril (actual) 150.
            await SeedCobroAsync(context, funcionario, servicio, new DateTime(2026, 3, 10, 9, 0, 0), 100m);
            await SeedCobroAsync(context, funcionario, servicio, new DateTime(2026, 4, 10, 9, 0, 0), 150m);

            var report = await Generate(context, tenantProvider, 2026, 4);

            Assert.True(report.IncluirComparativa);
            Assert.True(report.TieneComparativa);
            Assert.Equal("Marzo", report.MesAnteriorNombre);
            Assert.Equal(100m, report.IngresosMesAnterior);
            Assert.Equal(150m, report.Ingresos);
            Assert.Equal(50m, report.VariacionIngresosPorcentaje);
            Assert.Contains("subieron", report.ComentarioComparativa);
        }

        [Fact]
        public async Task Comparison_PreviousMonthZero_NoVariation_NuevoMovimiento()
        {
            var (context, connection, tenantProvider) = CreateContext();
            using var _ = context;
            using var __ = connection;

            var (funcionario, servicio) = await SeedBaseAsync(context);
            // Solo abril tiene datos; marzo queda en cero.
            await SeedCobroAsync(context, funcionario, servicio, new DateTime(2026, 4, 10, 9, 0, 0), 150m);

            var report = await Generate(context, tenantProvider, 2026, 4);

            Assert.False(report.TieneComparativa);
            Assert.Null(report.VariacionIngresosPorcentaje); // no divide entre cero
            Assert.Contains("No hay datos suficientes para comparar", report.ComentarioComparativa);
        }

        [Fact]
        public async Task Comparison_CurrentMonthZero_PreviousHadData_ShowsDrop()
        {
            var (context, connection, tenantProvider) = CreateContext();
            using var _ = context;
            using var __ = connection;

            var (funcionario, servicio) = await SeedBaseAsync(context);
            // Marzo 200; Abril sin ingresos.
            await SeedCobroAsync(context, funcionario, servicio, new DateTime(2026, 3, 10, 9, 0, 0), 200m);

            var report = await Generate(context, tenantProvider, 2026, 4);

            Assert.True(report.TieneComparativa);
            Assert.Equal(0m, report.Ingresos);
            Assert.Equal(200m, report.IngresosMesAnterior);
            Assert.Equal(-100m, report.VariacionIngresosPorcentaje);
            Assert.Contains("bajaron", report.ComentarioComparativa);
        }

        [Fact]
        public async Task Comparison_Disabled_NotComputed()
        {
            var (context, connection, tenantProvider) = CreateContext();
            using var _ = context;
            using var __ = connection;

            var (funcionario, servicio) = await SeedBaseAsync(context);
            await SeedCobroAsync(context, funcionario, servicio, new DateTime(2026, 3, 10, 9, 0, 0), 100m);
            await SeedCobroAsync(context, funcionario, servicio, new DateTime(2026, 4, 10, 9, 0, 0), 150m);

            context.TenantMonthlyReportSettings.Add(new TenantMonthlyReportSettings
            {
                TenantId = tenantProvider.TenantId,
                IncludeMonthOverMonth = false,
                CreatedAt = new DateTime(2026, 4, 1),
                UpdatedAt = new DateTime(2026, 4, 1)
            });
            await context.SaveChangesAsync();

            var report = await Generate(context, tenantProvider, 2026, 4);

            Assert.False(report.IncluirComparativa);
            Assert.False(report.TieneComparativa);
            Assert.Equal(string.Empty, report.MesAnteriorNombre);
            Assert.Null(report.VariacionIngresosPorcentaje);
        }

        // ─────────────── Soporte ───────────────

        private static (ProyectoIdentity.Datos.ApplicationDbContext, Microsoft.Data.Sqlite.SqliteConnection, TestTenantProvider) CreateContext()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            return (context, connection, tenantProvider);
        }

        private static Task<MonthlyBusinessReportViewModel> Generate(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            int year,
            int month)
        {
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(
                context, tenantProvider, new FakeMonthlyReportEmailSender());
            return service.GenerateAsync(tenantProvider.TenantId, year, month);
        }

        private static async Task<(int FuncionarioId, int ServicioId)> SeedBaseAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context)
        {
            var puesto = new Puesto { NombrePuesto = $"P{Guid.NewGuid():N}", Detalle = "x", Activo = true };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = "Ana",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111",
                PorcentajeGanancia = 50m,
                PorcentajeProducto = 0m,
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);

            var servicio = new Servicio { Nombre = "Corte", Precio = 100m, DuracionMinutos = 30, Activo = true };
            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();

            return (funcionario.IdFuncionario, servicio.Id);
        }

        private static async Task SeedCobroAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            int servicioId,
            DateTime fecha,
            decimal monto)
        {
            context.Cobros.Add(new Cobro
            {
                FechaCobro = fecha,
                NombreCliente = "Cliente",
                FuncionarioId = funcionarioId,
                ServicioId = servicioId,
                Monto = monto,
                MetodoPago = "EFECTIVO"
            });
            await context.SaveChangesAsync();
        }
    }
}
