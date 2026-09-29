using LuxuryApp.Models.Finanzas;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// "Ganancia del mes" debe significar LO MISMO en el Dashboard y en el correo ejecutivo.
    ///
    /// <para>
    /// El correo venía reportando ingresos netos − salidas de caja, que no es la ganancia: mezcla
    /// una base devengada con salidas de efectivo que pueden pertenecer a otro período. Para el
    /// mismo mes, el administrador veía un número en pantalla y otro distinto en su correo.
    /// </para>
    /// </summary>
    public class CorreoMensualVsDashboardTests
    {
        private const int Anio = 2026;
        private const int Mes = 8;
        private const decimal MontoCobro = 100_000m;
        private const decimal ComisionDevengada = 44_247.79m;
        private const decimal GastoOperativo = 10_000m;

        [Fact]
        public async Task DashboardYCorreo_MismaGananciaAnalitica()
        {
            var (dashboard, reporte) = await ConstruirAsync();

            Assert.Equal(dashboard.ResultadoAnalitico, reporte.GananciaReal);
            Assert.Equal(dashboard.TotalEgresosAnaliticos, reporte.Egresos);
            Assert.Equal(dashboard.TotalSinImpuestos, reporte.TotalSinImpuestos);
        }

        [Fact]
        public async Task DashboardYCorreo_MismasSalidasCaja()
        {
            var (dashboard, reporte) = await ConstruirAsync();

            // La caja de agosto es SOLO el alquiler: la planilla salió en septiembre.
            Assert.Equal(dashboard.SalidasCajaMes, reporte.SalidasCaja);
            Assert.Equal(GastoOperativo, reporte.SalidasCaja);

            // …mientras que el costo del mes sí incluye la liquidación devengada.
            Assert.Equal(ComisionDevengada + GastoOperativo, reporte.Egresos);
        }

        [Fact]
        public async Task Correo_NoUsaResultadoCajaComoGanancia()
        {
            var (dashboard, reporte) = await ConstruirAsync();

            // El escenario está armado para que caja y devengado NO coincidan: así, si alguien
            // vuelve a usar el resultado de caja como ganancia, este test lo ve.
            Assert.NotEqual(dashboard.ResultadoCajaMes, dashboard.ResultadoAnalitico);
            Assert.NotEqual(dashboard.ResultadoCajaMes, reporte.GananciaReal);
            Assert.NotEqual(dashboard.SalidasCajaMes, reporte.Egresos);

            // Y el margen se calcula sobre la misma ganancia.
            var margenEsperado = Math.Round(
                reporte.GananciaReal / reporte.Ingresos * 100m, 2, MidpointRounding.ToEven);
            Assert.Equal(margenEsperado, reporte.MargenGanancia);
        }

        /// <summary>
        /// Producción de agosto con una parte pagada en agosto y un gasto operativo: garantiza que
        /// el costo devengado y la salida de caja sean números DISTINTOS.
        /// </summary>
        private static async Task<(DashboardViewModel Dashboard, Models.Reports.MonthlyBusinessReportViewModel Reporte)>
            ConstruirAsync()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(Anio, Mes, 5, 9, 0, 0), MontoCobro);

            // Producción de AGOSTO pagada en SEPTIEMBRE: el costo es de agosto (devengado) pero el
            // efectivo sale en septiembre. Así devengado y caja son números distintos de verdad.
            await FinanzasTestSupport.PagarAsync(
                context, tenantProvider, funcionario,
                new DateTime(Anio, Mes, 3), new DateTime(Anio, Mes, 9),
                new DateTime(Anio, Mes + 1, 2, 10, 0, 0), ComisionDevengada);

            // Y un gasto operativo de agosto, que sí es costo y sí es caja del mismo mes.
            var categoria = await FinanzasTestSupport.SeedCategoriaAsync(context, "Alquiler");
            await FinanzasTestSupport.SeedEgresoAsync(
                context, categoria, new DateTime(Anio, Mes, 15, 9, 0, 0), GastoOperativo, "Alquiler agosto");

            var dashboardService = ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider);
            var reportService = ControllerTestSupport.CreateMonthlyBusinessReportService(
                context, tenantProvider, new FakeMonthlyReportEmailSender());

            var dashboard = await dashboardService.BuildViewModelAsync(Mes, Anio);
            var reporte = await reportService.GenerateAsync(tenantProvider.TenantId, Anio, Mes);

            return (dashboard, reporte);
        }
    }
}
