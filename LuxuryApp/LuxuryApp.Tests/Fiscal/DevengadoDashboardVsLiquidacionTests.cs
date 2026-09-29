using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Tests.Support;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// PRINCIPIO CONTABLE: la producción del colaborador genera la obligación, y con ella el costo.
    /// El pago solo cancela esa obligación y produce una salida de caja.
    ///
    /// <code>
    /// producción → costo devengado   (siempre, aunque nadie haya pagado)
    /// pago       → salida de caja    (nunca vuelve a crear el costo)
    /// </code>
    ///
    /// <para>
    /// Por lo tanto el resultado analítico NO puede depender de si ya se pagó, y la planilla
    /// devengada que muestra la pantalla de Liquidación tiene que ser exactamente la misma línea
    /// que resta el Dashboard para el mismo rango.
    /// </para>
    ///
    /// <para>
    /// Caso que originó estos tests: un cobro de ₡7.000 del 14/09/2026 con comisión del 50 % sobre
    /// el total cobrado. Liquidación mostraba ₡3.500 de planilla pendiente y el Dashboard del mismo
    /// mes mostraba "Liquidaciones del equipo ₡0" y una ganancia de ₡6.194,69 — es decir, el
    /// negocio se estaba atribuyendo como ganancia una plata que ya le debe al colaborador.
    /// </para>
    /// </summary>
    public class DevengadoDashboardVsLiquidacionTests
    {
        private const int Anio = 2026;
        private const int Mes = 9;

        private const decimal MontoCobro = 7_000m;

        /// <summary>7.000 / 1,13 redondeado a 2 decimales (half-even), igual que FiscalMath.</summary>
        private const decimal BaseSinIva = 6_194.69m;

        /// <summary>50 % sobre el TOTAL COBRADO (no sobre la base): 7.000 × 0,50.</summary>
        private const decimal ComisionDevengada = 3_500m;

        /// <summary>6.194,69 − 3.500.</summary>
        private const decimal GananciaEsperada = 2_694.69m;

        private static readonly DateTime FechaCobro = new(Anio, Mes, 14, 10, 0, 0);
        private static readonly DateTime SemanaInicio = new(Anio, Mes, 14);
        private static readonly DateTime SemanaFin = new(Anio, Mes, 20);

        /// <summary>
        /// 1. El cobro existe, nadie pagó todavía, y no hay ningún Egreso registrado.
        /// El costo ya tiene que estar en el Dashboard.
        /// </summary>
        [Fact]
        public async Task CobroConComision_NoPagada_ApareceComoLiquidacionDevengadaDashboard()
        {
            using var escenario = await EscenarioBase.CrearAsync();

            var liquidacion = await escenario.ObtenerLiquidacionMesAsync();
            var dashboard = await escenario.ObtenerDashboardAsync();

            // La pantalla de Liquidación ya lo calculaba bien: es el número de referencia.
            Assert.Equal(ComisionDevengada, liquidacion.TotalAPagarColaboradoresGeneral);
            Assert.Equal(ComisionDevengada, liquidacion.TotalPendienteGeneral);
            Assert.Equal(0m, liquidacion.TotalPagadoAplicadoGeneral);

            // …y el Dashboard tiene que decir exactamente lo mismo.
            Assert.Equal(BaseSinIva, dashboard.TotalSinImpuestos);
            Assert.Equal(ComisionDevengada, dashboard.TotalPagadoFuncionariosAnalitico);
            Assert.Equal(ComisionDevengada, dashboard.TotalEgresosAnaliticos);
            Assert.Equal(GananciaEsperada, dashboard.ResultadoAnalitico);

            // Lo único que sí debe quedar en cero: la caja. Nadie sacó plata todavía.
            Assert.Equal(0m, dashboard.SalidasCajaMes);
        }

        /// <summary>
        /// 2. Pagar no vuelve a crear el costo: mueve caja y nada más.
        /// </summary>
        [Fact]
        public async Task PagarComision_NoVuelveARestarDelResultadoAnalitico()
        {
            using var escenario = await EscenarioBase.CrearAsync();

            var antes = await escenario.ObtenerDashboardAsync();
            Assert.Equal(ComisionDevengada, antes.TotalPagadoFuncionariosAnalitico);
            Assert.Equal(0m, antes.SalidasCajaMes);

            await escenario.PagarAsync(ComisionDevengada, new DateTime(Anio, Mes, 21, 9, 0, 0));

            var despues = await escenario.ObtenerDashboardAsync();

            // El costo NO se duplica ni desaparece: es el mismo antes y después.
            Assert.Equal(antes.TotalPagadoFuncionariosAnalitico, despues.TotalPagadoFuncionariosAnalitico);
            Assert.Equal(antes.TotalEgresosAnaliticos, despues.TotalEgresosAnaliticos);
            Assert.Equal(antes.ResultadoAnalitico, despues.ResultadoAnalitico);
            Assert.Equal(GananciaEsperada, despues.ResultadoAnalitico);

            // Lo único que cambia es la caja.
            Assert.Equal(ComisionDevengada, despues.SalidasCajaMes);
        }

        /// <summary>
        /// 3. Producción de septiembre pagada en octubre: el costo se queda en septiembre y octubre
        /// solo ve la salida de caja. Si octubre volviera a restar la comisión, el negocio pagaría
        /// contablemente dos veces el mismo trabajo.
        /// </summary>
        [Fact]
        public async Task PagoEnMesPosterior_NoMueveCostoDevengado()
        {
            using var escenario = await EscenarioBase.CrearAsync();

            await escenario.PagarAsync(ComisionDevengada, new DateTime(Anio, Mes + 1, 5, 9, 0, 0));

            var septiembre = await escenario.ObtenerDashboardAsync();
            var octubre = await escenario.ObtenerDashboardAsync(Mes + 1);

            // Septiembre: el costo es suyo (lo devengó), la caja no.
            Assert.Equal(ComisionDevengada, septiembre.TotalEgresosAnaliticos);
            Assert.Equal(GananciaEsperada, septiembre.ResultadoAnalitico);
            Assert.Equal(0m, septiembre.SalidasCajaMes);

            // Octubre: la caja es suya, el costo NO.
            Assert.Equal(ComisionDevengada, octubre.SalidasCajaMes);
            Assert.Equal(0m, octubre.TotalEgresosAnaliticos);
            Assert.Equal(0m, octubre.ResultadoAnalitico);
        }

        /// <summary>
        /// 4. El devengado depende de la PRODUCCIÓN, nunca del estado de pago: pagado, pendiente,
        /// parcial y sin pagar tienen que sumar lo mismo en las dos pantallas.
        /// </summary>
        [Fact]
        public async Task DashboardYLiquidacion_MismoRango_MismaPlanillaDevengada()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedPolicyPagadoAsync(context);
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte con máquina", 10_000m);

            var pagadoCompleto = await SeedColaboradorAsync(context, "Ana");
            var sinPagar = await SeedColaboradorAsync(context, "Beto");
            var parcial = await SeedColaboradorAsync(context, "Carla");

            foreach (var funcionario in new[] { pagadoCompleto, sinPagar, parcial })
            {
                await FinanzasTestSupport.SeedCobroServicioAsync(
                    context, funcionario, servicio, FechaCobro, 10_000m);
            }

            // 50 % sobre el total cobrado ⇒ 5.000 devengados por cada uno.
            await FinanzasTestSupport.PagarAsync(
                context, tenantProvider, pagadoCompleto, SemanaInicio, SemanaFin,
                new DateTime(Anio, Mes, 21, 9, 0, 0), 5_000m);

            await FinanzasTestSupport.PagarAsync(
                context, tenantProvider, parcial, SemanaInicio, SemanaFin,
                new DateTime(Anio, Mes, 21, 9, 0, 0), 2_000m);

            var liquidacion = await ControllerTestSupport
                .CreateLiquidacionSemanalService(context, tenantProvider)
                .ObtenerResumenSemanaAsync(new DateTime(Anio, Mes, 1), new DateTime(Anio, Mes, 30));

            var dashboard = await ControllerTestSupport
                .CreateDashboardFinancieroQueryService(context, tenantProvider)
                .BuildViewModelAsync(Mes, Anio);

            const decimal devengadoTotal = 15_000m;
            const decimal pagadoTotal = 7_000m;

            Assert.Equal(devengadoTotal, liquidacion.TotalAPagarColaboradoresGeneral);
            Assert.Equal(pagadoTotal, liquidacion.TotalPagadoAplicadoGeneral);

            // LA INVARIANTE: misma planilla devengada en las dos pantallas, para el mismo rango.
            Assert.Equal(liquidacion.TotalAPagarColaboradoresGeneral, dashboard.TotalPagadoFuncionariosAnalitico);
            Assert.Equal(devengadoTotal, dashboard.TotalEgresosAnaliticos);

            // Y la caja sí refleja únicamente lo pagado.
            Assert.Equal(pagadoTotal, dashboard.SalidasCajaMes);
            Assert.NotEqual(dashboard.SalidasCajaMes, dashboard.TotalEgresosAnaliticos);
        }

        /// <summary>
        /// 5. La participación del asociado se calcula sobre la ganancia YA descontada la planilla
        /// devengada. Repartir sobre los ingresos netos sería repartir plata que le pertenece al
        /// colaborador.
        /// </summary>
        [Fact]
        public async Task AssociateAllocation_UsaGananciaDespuesDeDevengado()
        {
            using var escenario = await EscenarioBase.CrearAsync();

            await InvestorTestSupport.SeedInvestorAsync(
                escenario.Context,
                "Socia",
                "socia@example.com",
                porcentaje: 30m,
                effectiveFrom: new DateOnly(Anio, 1, 1));

            var kpi = await escenario.ObtenerKpiAsociadosAsync();

            Assert.NotNull(kpi);
            Assert.Equal(GananciaEsperada, kpi!.GananciaDistribuible);
            Assert.NotEqual(BaseSinIva, kpi.GananciaDistribuible);

            // 2.694,69 × 30 % = 808,407 → 808,41 (half-even).
            Assert.Equal(808.41m, kpi.ParticipacionMonto);
        }

        private static Task<Funcionario> SeedColaboradorAsync(ApplicationDbContext context, string nombre) =>
            FinanzasTestSupport.SeedFuncionarioAsync(
                context,
                nombre,
                porcentajeServicio: 50m,
                comisionSobre: ComisionCalculadaSobre.TotalCobrado);

        /// <summary>
        /// Política del negocio configurada con base PAGADO — que es exactamente la configuración
        /// real que destapó el bug. El resultado económico del Dashboard NO puede depender de este
        /// ajuste: es una decisión contractual del módulo de inversionistas, no contabilidad.
        /// </summary>
        private static Task SeedPolicyPagadoAsync(ApplicationDbContext context) =>
            InvestorTestSupport.SeedPolicyAsync(context, policy =>
            {
                policy.ExcluirIva = true;
                policy.IncluirLiquidaciones = true;
                policy.BaseLiquidaciones = InvestorSettlementBasis.Pagado;
            });

        /// <summary>Escenario mínimo: un colaborador, un cobro de ₡7.000, nada pagado.</summary>
        private sealed class EscenarioBase : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

            private EscenarioBase(
                ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider,
                Funcionario funcionario)
            {
                Context = context;
                _connection = connection;
                TenantProvider = tenantProvider;
                Funcionario = funcionario;
            }

            public ApplicationDbContext Context { get; }

            public TestTenantProvider TenantProvider { get; }

            public Funcionario Funcionario { get; }

            public static async Task<EscenarioBase> CrearAsync()
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                await SeedPolicyPagadoAsync(context);

                var funcionario = await SeedColaboradorAsync(context, "Drew");
                var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte con máquina", MontoCobro);

                await FinanzasTestSupport.SeedCobroServicioAsync(
                    context, funcionario, servicio, FechaCobro, MontoCobro);

                return new EscenarioBase(context, connection, tenantProvider, funcionario);
            }

            public Task<int> PagarAsync(decimal monto, DateTime fechaPago) =>
                FinanzasTestSupport.PagarAsync(
                    Context, TenantProvider, Funcionario, SemanaInicio, SemanaFin, fechaPago, monto);

            public Task<PagosSemanaResumen> ObtenerLiquidacionMesAsync() =>
                ControllerTestSupport
                    .CreateLiquidacionSemanalService(Context, TenantProvider)
                    .ObtenerResumenSemanaAsync(new DateTime(Anio, Mes, 1), new DateTime(Anio, Mes, 30));

            public Task<Models.Finanzas.DashboardViewModel> ObtenerDashboardAsync(int? mes = null) =>
                ControllerTestSupport
                    .CreateDashboardFinancieroQueryService(Context, TenantProvider)
                    .BuildViewModelAsync(mes ?? Mes, Anio);

            public Task<Models.Asociados.AssociateAllocationKpiViewModel?> ObtenerKpiAsociadosAsync() =>
                ControllerTestSupport
                    .CreateAssociateProfitAllocationService(Context, TenantProvider)
                    .BuildMonthlyKpiAsync(Mes, Anio);

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }
    }
}
