using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Services.Fiscal;
using LuxuryApp.Tests.Support;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// Mismo tenant + mismo periodo + mismos cobros ⇒ mismo IVA y misma base, sin importar si
    /// pregunta el Dashboard, la pantalla Ingresos o el Excel de cobros.
    ///
    /// <para>
    /// La política de agregación canónica es <b>por línea de cobro → redondear → sumar</b>
    /// (<see cref="ITaxCalculationService.Sumar"/>), nunca "sumar el mes y dividir el total".
    /// Estos tests existen para que nadie reintroduzca una segunda aritmética.
    /// </para>
    /// </summary>
    public class ConsistenciaFiscalModulosTests
    {
        private const int Anio = 2026;
        private const int Mes = 8;

        [Fact]
        public async Task Fiscal_PrecioIncluyeIva13_UsaDivision113()
        {
            // Regla base del negocio: precio con IVA incluido al 13 % ⇒ base = Total / 1,13.
            // 10.000 / 1,13 = 8.849,5575… → 8.849,56 (half-even, 2 decimales); IVA = el resto.
            var motor = new TaxCalculationService();
            var desglose = motor.Calcular(10_000m, taxRatePercent: 13m, priceIncludesTax: true, taxable: true);

            Assert.Equal(8_849.56m, desglose.NetBase);
            Assert.Equal(1_150.44m, desglose.TaxAmount);
            Assert.Equal(10_000m, desglose.GrossTotal);
            Assert.Equal(desglose.GrossTotal, desglose.NetBase + desglose.TaxAmount);

            // Y el mismo número debe salir por las dos pantallas.
            using var escenario = await EscenarioBuilder.CrearAsync(async ctx =>
            {
                var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(ctx.Context, "Uno");
                var servicio = await FinanzasTestSupport.SeedServicioAsync(ctx.Context, "Corte", 10_000m);
                await FinanzasTestSupport.SeedCobroServicioAsync(
                    ctx.Context, funcionario, servicio, new DateTime(Anio, Mes, 5, 9, 0, 0), 10_000m);
            });

            var (dashboard, ingresos, export) = await escenario.LeerTodoAsync();

            Assert.Equal(8_849.56m, dashboard.TotalSinImpuestos);
            Assert.Equal(8_849.56m, ingresos.TotalSinImpuestos);
            Assert.Equal(8_849.56m, export.Filas.Sum(f => f.BaseSinIva));
            Assert.Equal(1_150.44m, dashboard.TotalImpuestos);
            Assert.Equal(1_150.44m, ingresos.TotalImpuestos);
            Assert.Equal(1_150.44m, export.Filas.Sum(f => f.IvaIncluido));
        }

        [Fact]
        public async Task DashboardEIngresos_MismoPeriodo_MismoIva()
        {
            using var escenario = await EscenarioBuilder.CrearAsync(SeedMezclaGravadaAsync);
            var (dashboard, ingresos, _) = await escenario.LeerTodoAsync();

            Assert.Equal(dashboard.TotalImpuestos, ingresos.TotalImpuestos);
        }

        [Fact]
        public async Task DashboardEIngresos_MismoPeriodo_MismaBaseSinIva()
        {
            using var escenario = await EscenarioBuilder.CrearAsync(SeedMezclaGravadaAsync);
            var (dashboard, ingresos, _) = await escenario.LeerTodoAsync();

            Assert.Equal(dashboard.TotalSinImpuestos, ingresos.TotalSinImpuestos);
            Assert.Equal(dashboard.TotalGenerado, ingresos.TotalGenerado);
        }

        [Fact]
        public async Task ExcelIngresosYDashboard_MismoPeriodo_MismoIvaYBase()
        {
            using var escenario = await EscenarioBuilder.CrearAsync(SeedMezclaGravadaAsync);
            var (dashboard, ingresos, export) = await escenario.LeerTodoAsync();

            // Las filas del Excel deben cuadrar con su propio resumen…
            Assert.Equal(export.Resumen.TotalImpuestos, export.Filas.Sum(f => f.IvaIncluido));
            Assert.Equal(export.Resumen.TotalSinImpuestos, export.Filas.Sum(f => f.BaseSinIva));
            Assert.Equal(export.Resumen.TotalGenerado, export.Filas.Sum(f => f.Monto));

            // …y con las otras dos pantallas.
            Assert.Equal(dashboard.TotalImpuestos, export.Filas.Sum(f => f.IvaIncluido));
            Assert.Equal(dashboard.TotalSinImpuestos, export.Filas.Sum(f => f.BaseSinIva));
            Assert.Equal(ingresos.TotalImpuestos, export.Resumen.TotalImpuestos);
        }

        /// <summary>
        /// Regresión: una venta marcada explícitamente como EXENTA
        /// (<c>AplicaIva = false</c>). El test NO decide si debe estar exenta; decide que los tres
        /// módulos deben interpretar esa configuración de la MISMA manera.
        /// </summary>
        [Fact]
        public async Task Fiscal_ServicioExento_EsConsistenteEnTodosLosModulos()
        {
            using var escenario = await EscenarioBuilder.CrearAsync(async ctx =>
            {
                var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(ctx.Context, "Exenciones");
                var gravado = await FinanzasTestSupport.SeedServicioAsync(ctx.Context, "Corte", 10_000m);
                var exento = await FinanzasTestSupport.SeedServicioAsync(
                    ctx.Context, "Limpieza facial", 19_500m, aplicaIva: false);

                await FinanzasTestSupport.SeedCobroServicioAsync(
                    ctx.Context, funcionario, gravado, new DateTime(Anio, Mes, 10, 9, 0, 0), 10_000m);
                await FinanzasTestSupport.SeedCobroServicioAsync(
                    ctx.Context, funcionario, exento, new DateTime(Anio, Mes, 31, 9, 0, 0), 19_500m);
            });

            var (dashboard, ingresos, export) = await escenario.LeerTodoAsync();

            // Exento: base = total, IVA = 0. Gravado: 10.000 → 8.849,56 + 1.150,44.
            Assert.Equal(28_349.56m, dashboard.TotalSinImpuestos);
            Assert.Equal(1_150.44m, dashboard.TotalImpuestos);

            Assert.Equal(dashboard.TotalSinImpuestos, ingresos.TotalSinImpuestos);
            Assert.Equal(dashboard.TotalImpuestos, ingresos.TotalImpuestos);
            Assert.Equal(dashboard.TotalSinImpuestos, export.Filas.Sum(f => f.BaseSinIva));
            Assert.Equal(dashboard.TotalImpuestos, export.Filas.Sum(f => f.IvaIncluido));

            // La línea exenta no debe aportar ni un colón de IVA en ningún módulo.
            var filaExenta = Assert.Single(export.Filas, f => f.Monto == 19_500m);
            Assert.Equal(0m, filaExenta.IvaIncluido);
            Assert.Equal(19_500m, filaExenta.BaseSinIva);
        }

        [Fact]
        public async Task Ingresos_BaseMasIva_IgualTotalGenerado()
        {
            using var escenario = await EscenarioBuilder.CrearAsync(SeedMezclaGravadaAsync);
            var (_, ingresos, export) = await escenario.LeerTodoAsync();

            Assert.Equal(ingresos.TotalGenerado, ingresos.TotalSinImpuestos + ingresos.TotalImpuestos);
            Assert.Equal(ingresos.TotalGenerado, ingresos.TotalServicios + ingresos.TotalProductos);
            Assert.Equal(
                ingresos.TotalGenerado,
                ingresos.GananciaEfectivo + ingresos.GananciaSinpe + ingresos.GananciaTarjeta);

            foreach (var fila in export.Filas)
            {
                Assert.Equal(fila.Monto, fila.BaseSinIva + fila.IvaIncluido);
            }
        }

        /// <summary>Mezcla gravada: catálogo, servicio personalizado y producto, en los tres métodos.</summary>
        private static async Task SeedMezclaGravadaAsync(EscenarioBuilder ctx)
        {
            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(ctx.Context, "Mezcla");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(ctx.Context, "Corte", 7_000m);
            var producto = await FinanzasTestSupport.SeedProductoAsync(ctx.Context, "Cera", 9_500m);

            await FinanzasTestSupport.SeedCobroServicioAsync(
                ctx.Context, funcionario, servicio, new DateTime(Anio, Mes, 3, 9, 0, 0), 7_000m, "EFECTIVO");
            await FinanzasTestSupport.SeedCobroServicioPersonalizadoAsync(
                ctx.Context, funcionario, "Afeitado", new DateTime(Anio, Mes, 4, 9, 0, 0), 4_000m, "SINPE");
            await FinanzasTestSupport.SeedCobroProductoAsync(
                ctx.Context, funcionario, producto, new DateTime(Anio, Mes, 5, 9, 0, 0), 9_500m, "TARJETA");
            await FinanzasTestSupport.SeedCobroServicioAsync(
                ctx.Context, funcionario, servicio, new DateTime(Anio, Mes, 31, 23, 30, 0), 7_000m, "EFECTIVO");
        }

        /// <summary>
        /// Monta un tenant con contexto SQLite en memoria y lee el MISMO mes por las tres vías.
        /// </summary>
        private sealed class EscenarioBuilder : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
            private readonly TestTenantProvider _tenantProvider;

            private EscenarioBuilder(
                ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider)
            {
                Context = context;
                _connection = connection;
                _tenantProvider = tenantProvider;
            }

            public ApplicationDbContext Context { get; }

            public static async Task<EscenarioBuilder> CrearAsync(Func<EscenarioBuilder, Task> seed)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
                var escenario = new EscenarioBuilder(context, connection, tenantProvider);
                await seed(escenario);
                return escenario;
            }

            public async Task<(DashboardViewModel Dashboard, CobroIndexViewModel Ingresos, CobroExportViewModel Export)>
                LeerTodoAsync()
            {
                var dashboardService =
                    ControllerTestSupport.CreateDashboardFinancieroQueryService(Context, _tenantProvider);
                var cobroService = ControllerTestSupport.CreateCobroQueryService(Context, _tenantProvider);

                var dashboard = await dashboardService.BuildViewModelAsync(Mes, Anio);
                var ingresos = await cobroService.BuildIndexViewModelAsync(
                    FinanzasTestSupport.FiltroMes(Anio, Mes), includeFilterOptions: false);
                var export = await cobroService.BuildExportAsync(FinanzasTestSupport.FiltroMes(Anio, Mes));

                return (dashboard, ingresos, export);
            }

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }
    }
}
