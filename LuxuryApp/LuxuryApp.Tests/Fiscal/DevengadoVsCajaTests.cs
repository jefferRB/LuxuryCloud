using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// El Dashboard responde DOS preguntas distintas y no deben mezclarse:
    ///
    /// <list type="bullet">
    ///   <item><b>Costos y gastos del mes</b> (devengado): liquidaciones del equipo generadas por la
    ///   producción del mes + gastos económicos elegibles. Es lo que alimenta la ganancia.</item>
    ///   <item><b>Salidas de caja</b>: egresos registrados con FechaEgreso dentro del mes. Es lo mismo
    ///   que muestra la pantalla /Egresos con ese rango.</item>
    /// </list>
    ///
    /// <para>
    /// Que difieran NO es un bug: un pago de agosto puede corresponder a producción de julio. Estos
    /// tests existen para que nadie "arregle" la ganancia reemplazando devengado por caja.
    /// </para>
    /// </summary>
    public class DevengadoVsCajaTests
    {
        // 100.000 con IVA incluido → base 88.495,58; comisión 50 % = 44.247,79.
        private const decimal MontoCobro = 100_000m;
        private const decimal ComisionDevengada = 44_247.79m;

        [Fact]
        public async Task Dashboard_CostosDevengados_NoUsanSalidasCaja()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Producción en AGOSTO, pago (salida de caja) en SEPTIEMBRE.
            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(2026, 8, 5, 9, 0, 0), MontoCobro);

            await PagarAsync(
                context,
                tenantProvider,
                funcionario,
                semanaInicio: new DateTime(2026, 8, 3),
                semanaFin: new DateTime(2026, 8, 9),
                fechaPago: new DateTime(2026, 9, 2, 10, 0, 0),
                monto: ComisionDevengada);

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider);
            var agosto = await dashboard.BuildViewModelAsync(8, 2026);
            var septiembre = await dashboard.BuildViewModelAsync(9, 2026);

            // El COSTO pertenece a agosto: ahí se generó la producción.
            Assert.Equal(ComisionDevengada, agosto.Desglose!.LiquidacionesEquipo);
            Assert.Equal(ComisionDevengada, agosto.TotalEgresosAnaliticos);

            // La CAJA salió en septiembre.
            Assert.Equal(0m, agosto.SalidasCajaMes);
            Assert.Equal(ComisionDevengada, septiembre.SalidasCajaMes);

            // Y septiembre no hereda el costo: no tuvo producción.
            Assert.Equal(0m, septiembre.Desglose!.LiquidacionesEquipo);
        }

        [Fact]
        public async Task Dashboard_SalidasCaja_UsaFechaRealDelEgreso()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Producción en JULIO, egreso en AGOSTO.
            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(2026, 7, 8, 9, 0, 0), MontoCobro);

            await PagarAsync(
                context,
                tenantProvider,
                funcionario,
                semanaInicio: new DateTime(2026, 7, 6),
                semanaFin: new DateTime(2026, 7, 12),
                fechaPago: new DateTime(2026, 8, 5, 10, 0, 0),
                monto: ComisionDevengada);

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider);
            var julio = await dashboard.BuildViewModelAsync(7, 2026);
            var agosto = await dashboard.BuildViewModelAsync(8, 2026);

            // Agosto NO carga el costo solo porque el egreso se registró ahí…
            Assert.Equal(0m, agosto.Desglose!.LiquidacionesEquipo);
            Assert.Equal(ComisionDevengada, julio.Desglose!.LiquidacionesEquipo);

            // …pero la caja de agosto sí lo muestra, igual que la pantalla /Egresos.
            Assert.Equal(ComisionDevengada, agosto.SalidasCajaMes);
            Assert.Equal(0m, julio.SalidasCajaMes);
        }

        [Fact]
        public async Task PagoLiquidacion_NoSeRestaDosVecesDeGanancia()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(2026, 8, 5, 9, 0, 0), MontoCobro);

            // Se devenga Y se paga dentro del mismo mes.
            await PagarAsync(
                context,
                tenantProvider,
                funcionario,
                semanaInicio: new DateTime(2026, 8, 3),
                semanaFin: new DateTime(2026, 8, 9),
                fechaPago: new DateTime(2026, 8, 10, 10, 0, 0),
                monto: ComisionDevengada);

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider);
            var agosto = await dashboard.BuildViewModelAsync(8, 2026);
            var desglose = agosto.Desglose!;

            // El egreso de "Pago Funcionarios" NO cuenta como gasto operativo: ya está en liquidaciones.
            Assert.Equal(0m, desglose.GastosOperativos);
            Assert.Equal(ComisionDevengada, desglose.LiquidacionesEquipo);
            Assert.Equal(
                desglose.IngresosNetos - desglose.LiquidacionesEquipo,
                desglose.GananciaDistribuible);

            // La salida de caja sí ocurrió y se ve como tal, sin tocar la ganancia.
            Assert.Equal(ComisionDevengada, agosto.SalidasCajaMes);

            var categoriaPago = await context.Categorias
                .SingleAsync(c => c.Nombre == LiquidacionSemanalDefaults.CategoriaPagoFuncionarios);
            var lineaExcluida = Assert.Single(
                desglose.GastosPorCategoria, g => g.CategoriaId == categoriaPago.Id);
            Assert.False(lineaExcluida.Incluido);
        }

        [Fact]
        public async Task CostoLaboralExtraordinario_SiReduceGanancia()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(2026, 8, 5, 9, 0, 0), MontoCobro);

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider);
            var antes = await dashboard.BuildViewModelAsync(8, 2026);

            // Vacaciones: NO forman parte de la comisión devengada ordinaria.
            var categoria = await FinanzasTestSupport.SeedCategoriaAsync(
                context, LiquidacionSemanalDefaults.CategoriaCostoLaboralExtraordinario);
            await FinanzasTestSupport.SeedEgresoAsync(
                context, categoria, new DateTime(2026, 8, 20, 9, 0, 0), 20_000m, "Vacaciones Jordan");

            var despues = await dashboard.BuildViewModelAsync(8, 2026);

            Assert.Equal(0m, antes.Desglose!.GastosOperativos);
            Assert.Equal(20_000m, despues.Desglose!.GastosOperativos);
            Assert.Equal(
                antes.Desglose.GananciaDistribuible - 20_000m,
                despues.Desglose.GananciaDistribuible);

            // La categoría NO puede quedar excluida del cálculo.
            var linea = Assert.Single(
                despues.Desglose.GastosPorCategoria, g => g.CategoriaId == categoria.Id);
            Assert.True(linea.Incluido);
            Assert.Null(linea.MotivoExclusion);
        }

        [Fact]
        public async Task CostoLaboralExtraordinario_ApareceEnSalidasCaja()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await FinanzasTestSupport.SeedCategoriaAsync(
                context, LiquidacionSemanalDefaults.CategoriaCostoLaboralExtraordinario);
            await FinanzasTestSupport.SeedEgresoAsync(
                context, categoria, new DateTime(2026, 8, 20, 9, 0, 0), 20_000m, "Vacaciones Jordan");

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider);
            var agosto = await dashboard.BuildViewModelAsync(8, 2026);

            Assert.Equal(20_000m, agosto.SalidasCajaMes);
            Assert.Equal(20_000m, agosto.TotalEgresosAnaliticos);
        }

        /// <summary>
        /// Un gasto laboral extraordinario es un EGRESO, no una liquidación: no puede alterar ni un
        /// colón de lo que el motor de planilla dice que se le debe al colaborador.
        /// </summary>
        [Fact]
        public async Task CostoLaboralExtraordinario_NoModificaLiquidacionFuncionario()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(2026, 8, 5, 9, 0, 0), MontoCobro);

            var liquidacion = ControllerTestSupport.CreateLiquidacionSemanalService(context, tenantProvider);
            var antes = (await liquidacion.ObtenerResumenSemanaAsync(
                    new DateTime(2026, 8, 3), new DateTime(2026, 8, 9)))
                .Funcionarios.Single(f => f.FuncionarioId == funcionario.IdFuncionario);

            var categoria = await FinanzasTestSupport.SeedCategoriaAsync(
                context, LiquidacionSemanalDefaults.CategoriaCostoLaboralExtraordinario);
            await FinanzasTestSupport.SeedEgresoAsync(
                context, categoria, new DateTime(2026, 8, 6, 9, 0, 0), 20_000m, "Bono Jordan");

            var despues = (await liquidacion.ObtenerResumenSemanaAsync(
                    new DateTime(2026, 8, 3), new DateTime(2026, 8, 9)))
                .Funcionarios.Single(f => f.FuncionarioId == funcionario.IdFuncionario);

            Assert.Equal(antes.TotalServicios, despues.TotalServicios);
            Assert.Equal(antes.TotalProductos, despues.TotalProductos);
            Assert.Equal(antes.MontoColaborador, despues.MontoColaborador);
            Assert.Equal(antes.TotalAPagarColaborador, despues.TotalAPagarColaborador);
            Assert.Equal(antes.MontoPagado, despues.MontoPagado);
            Assert.Equal(antes.MontoPendiente, despues.MontoPendiente);
            Assert.Equal(ComisionDevengada, despues.TotalAPagarColaborador);
        }

        private static async Task PagarAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            Funcionario funcionario,
            DateTime semanaInicio,
            DateTime semanaFin,
            DateTime fechaPago,
            decimal monto)
        {
            var service = ControllerTestSupport.CreateLiquidacionSemanalService(context, tenantProvider);

            await service.RegistrarPagoAsync(new RegistrarLiquidacionSemanalCommand
            {
                SemanaInicio = semanaInicio,
                SemanaFin = semanaFin,
                FechaPago = fechaPago,
                MetodoPago = "EFECTIVO",
                CreadoPor = "test",
                Detalles =
                {
                    new RegistrarLiquidacionSemanalDetalleCommand
                    {
                        FuncionarioId = funcionario.IdFuncionario,
                        MontoPagado = monto
                    }
                }
            });
        }
    }
}
