using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// El devengado que pesa la atribución de pagos dejó de tener su propia aritmética.
    ///
    /// <para>
    /// Hasta la Fase 5, <c>Devengado()</c> calculaba la base de comisión dividiendo el monto entre
    /// 1,13 fijo, sin mirar si el servicio estaba exento ni cuál era su tarifa real. Era una CUARTA
    /// implementación fiscal, y daba una base distinta de la que usan Dashboard, Ingresos, Excel y
    /// la propia liquidación. Consecuencia visible: pagabas exactamente lo que debías y la pantalla
    /// seguía mostrando un pendiente que no existía.
    /// </para>
    ///
    /// <para>
    /// La responsabilidad de <c>Devengado()</c> no cambió: sigue siendo el peso con el que el
    /// algoritmo greedy reparte los pagos, en orden cronológico, con la misma semántica de
    /// excedentes. Lo único que cambió es de dónde saca el porcentaje y la base.
    /// </para>
    /// </summary>
    public class DevengadoCanonicoTests
    {
        private const decimal Monto = 10_000m;
        private static readonly DateTime Inicio = new(2026, 9, 1);
        private static readonly DateTime Quincena = new(2026, 9, 15);
        private static readonly DateTime FinMes = new(2026, 9, 30);

        /// <summary>
        /// §28.17 — Servicio EXENTO. Sin IVA que descontar, la base de comisión es el monto entero.
        /// La división plana entre 1,13 inventaba un IVA que el cliente nunca pagó.
        /// </summary>
        [Fact]
        public async Task DevengadoServicioExento_UsaFiscalidadCanonica()
        {
            using var e = await Escenario.CrearAsync(comisionSobre: ComisionCalculadaSobre.BaseSinIva);

            var exento = await FinanzasTestSupport.SeedServicioAsync(
                e.Context, "Consulta", Monto, aplicaIva: false);
            var gravado = await FinanzasTestSupport.SeedServicioAsync(e.Context, "Corte", Monto);

            await e.RegistrarAsync(exento.Id, dia: 3);
            await e.RegistrarAsync(gravado.Id, dia: 20);

            // El exento devenga 10.000 × 50 % = 5.000: no hay IVA que restarle.
            var quincena = await e.ResumenAsync(Inicio, Quincena);
            Assert.Equal(5_000m, quincena.TotalAPagarColaboradoresGeneral);

            // Se paga EXACTAMENTE lo devengado de la quincena.
            await FinanzasTestSupport.PagarAsync(
                e.Context, e.TenantProvider, e.Funcionario, Inicio, FinMes,
                new DateTime(2026, 9, 30, 12, 0, 0), 5_000m);

            var despues = await e.ResumenAsync(Inicio, Quincena);

            // Con la base plana /1,13 el devengado del cobro exento salía 4.424,78 y quedaban
            // ₡575,22 "pendientes" de una deuda ya saldada. Ahora cierra en cero.
            Assert.Equal(5_000m, despues.TotalPagadoAplicadoGeneral);
            Assert.Equal(0m, despues.TotalPendienteGeneral);
        }

        /// <summary>§28.18 — Tarifa distinta del 13 %: la base sale del motor, no de una constante.</summary>
        [Fact]
        public async Task DevengadoTarifaDiferente13_UsaFiscalidadCanonica()
        {
            using var e = await Escenario.CrearAsync(comisionSobre: ComisionCalculadaSobre.BaseSinIva);

            var servicio = await FinanzasTestSupport.SeedServicioAsync(
                e.Context, "Servicio 4 %", Monto, aplicaIva: true, tarifaIva: 4m);

            await e.RegistrarAsync(servicio.Id, dia: 3);

            // 10.000 / 1,04 = 9.615,38 → 50 % = 4.807,69
            var resumen = await e.ResumenAsync(Inicio, Quincena);
            Assert.Equal(4_807.69m, resumen.TotalAPagarColaboradoresGeneral);

            await FinanzasTestSupport.PagarAsync(
                e.Context, e.TenantProvider, e.Funcionario, Inicio, Quincena,
                new DateTime(2026, 9, 16, 12, 0, 0), 4_807.69m);

            var despues = await e.ResumenAsync(Inicio, Quincena);
            Assert.Equal(0m, despues.TotalPendienteGeneral);
        }

        /// <summary>§28.16 — El devengado respeta el porcentaje congelado, no el vigente.</summary>
        [Fact]
        public async Task DevengadoSnapshot_UsaPorcentajeHistorico()
        {
            using var e = await Escenario.CrearAsync();
            var servicio = await FinanzasTestSupport.SeedServicioAsync(e.Context, "Corte", Monto);

            await e.RegistrarAsync(servicio.Id, dia: 3);

            await FinanzasTestSupport.PagarAsync(
                e.Context, e.TenantProvider, e.Funcionario, Inicio, Quincena,
                new DateTime(2026, 9, 16, 12, 0, 0), 5_000m);

            await e.CambiarPorcentajeAsync(55m);

            var despues = await e.ResumenAsync(Inicio, Quincena);

            // Si el devengado siguiera el porcentaje VIGENTE, la deuda pasaría a 5.500 y aparecerían
            // ₡500 pendientes de un pago que ya canceló la obligación completa.
            Assert.Equal(5_000m, despues.TotalAPagarColaboradoresGeneral);
            Assert.Equal(0m, despues.TotalPendienteGeneral);
        }

        /// <summary>§28.19 — Con una sola configuración, la atribución no se movió ni un centavo.</summary>
        [Fact]
        public async Task AtribuirPagos_MismaConfiguracion_NoCambiaResultadoHistorico()
        {
            using var e = await Escenario.CrearAsync();
            var servicio = await FinanzasTestSupport.SeedServicioAsync(e.Context, "Corte", Monto);

            foreach (var dia in new[] { 2, 5, 9, 12 })
            {
                await e.RegistrarAsync(servicio.Id, dia);
            }

            // 4 × 10.000 × 50 % = 20.000 devengados; se pagan 12.000.
            await FinanzasTestSupport.PagarAsync(
                e.Context, e.TenantProvider, e.Funcionario, Inicio, Quincena,
                new DateTime(2026, 9, 16, 12, 0, 0), 12_000m);

            var resumen = await e.ResumenAsync(Inicio, Quincena);

            Assert.Equal(20_000m, resumen.TotalAPagarColaboradoresGeneral);
            Assert.Equal(12_000m, resumen.TotalPagadoAplicadoGeneral);
            Assert.Equal(8_000m, resumen.TotalPendienteGeneral);
            Assert.Equal(0m, resumen.TotalExcedenteGeneral);
        }

        /// <summary>§28.20 — Un pago parcial sigue aplicándose igual.</summary>
        [Fact]
        public async Task PagoParcial_SigueAplicandoseCorrectamente()
        {
            using var e = await Escenario.CrearAsync();
            var servicio = await FinanzasTestSupport.SeedServicioAsync(e.Context, "Corte", Monto);
            await e.RegistrarAsync(servicio.Id, dia: 3);

            await FinanzasTestSupport.PagarAsync(
                e.Context, e.TenantProvider, e.Funcionario, Inicio, Quincena,
                new DateTime(2026, 9, 16, 12, 0, 0), 2_000m);

            var resumen = await e.ResumenAsync(Inicio, Quincena);

            Assert.Equal(5_000m, resumen.TotalAPagarColaboradoresGeneral);
            Assert.Equal(2_000m, resumen.TotalPagadoAplicadoGeneral);
            Assert.Equal(3_000m, resumen.TotalPendienteGeneral);
        }

        /// <summary>§28.21 — Revertir devuelve la obligación intacta.</summary>
        [Fact]
        public async Task Reversion_SigueRestaurandoPendienteCorrectamente()
        {
            using var e = await Escenario.CrearAsync();
            var servicio = await FinanzasTestSupport.SeedServicioAsync(e.Context, "Corte", Monto);
            await e.RegistrarAsync(servicio.Id, dia: 3);

            var liquidacionId = await FinanzasTestSupport.PagarAsync(
                e.Context, e.TenantProvider, e.Funcionario, Inicio, Quincena,
                new DateTime(2026, 9, 16, 12, 0, 0), 5_000m);

            Assert.Equal(0m, (await e.ResumenAsync(Inicio, Quincena)).TotalPendienteGeneral);

            await ControllerTestSupport
                .CreateLiquidacionSemanalService(e.Context, e.TenantProvider)
                .RevertirPagoAsync(liquidacionId, "test", "corrección");

            var despues = await e.ResumenAsync(Inicio, Quincena);

            // La producción no se toca nunca: vuelve el pendiente completo, no un residuo.
            Assert.Equal(5_000m, despues.TotalAPagarColaboradoresGeneral);
            Assert.Equal(0m, despues.TotalPagadoAplicadoGeneral);
            Assert.Equal(5_000m, despues.TotalPendienteGeneral);
        }

        /// <summary>
        /// §30 — CORTE TEMPORAL COMPLETO. Cambian el IVA del negocio Y el porcentaje del
        /// colaborador; cada cobro conserva lo suyo, y los cuatro módulos lo interpretan igual.
        /// </summary>
        [Fact]
        public async Task CorteTemporal_DashboardIngresosExcelLiquidacion_InterpretanIgual()
        {
            using var e = await Escenario.CrearAsync(
                comisionSobre: ComisionCalculadaSobre.TotalCobrado, conTenant: true);

            var servicio = await FinanzasTestSupport.SeedServicioAsync(e.Context, "Corte", 7_000m);

            // Cobro A: IVA 13 %, comisión 50 %.
            await e.RegistrarAsync(servicio.Id, dia: 3, monto: 7_000m);

            await e.CambiarTarifaTenantAsync(4m);
            await e.CambiarPorcentajeAsync(55m);

            // Cobro B: IVA 4 %, comisión 55 %.
            await e.RegistrarAsync(servicio.Id, dia: 12, monto: 7_000m);

            // Bases: 7.000/1,13 = 6.194,69   y   7.000/1,04 = 6.730,77
            const decimal baseTotal = 6_194.69m + 6_730.77m;
            // Comisión sobre TOTAL cobrado: 7.000 × 50 % + 7.000 × 55 % = 3.500 + 3.850
            const decimal planilla = 7_350m;

            var liquidacion = await e.ResumenAsync(Inicio, Quincena);
            var ingresos = await e.IngresosAsync();
            var excel = await e.ExcelAsync();
            var dashboard = await e.DashboardAsync();

            Assert.Equal(baseTotal, liquidacion.TotalBaseVentaSinIvaGeneral);
            Assert.Equal(baseTotal, ingresos.TotalSinImpuestos);
            Assert.Equal(baseTotal, excel.Resumen.TotalSinImpuestos);
            Assert.Equal(baseTotal, dashboard.TotalSinImpuestos);

            Assert.Equal(planilla, liquidacion.TotalAPagarColaboradoresGeneral);
            Assert.Equal(planilla, ingresos.PagoColaboradores);
            Assert.Equal(planilla, excel.Resumen.PagoColaboradores);
            Assert.Equal(planilla, dashboard.TotalPagadoFuncionariosAnalitico);
        }

        private sealed class Escenario : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

            private Escenario(
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

            public static async Task<Escenario> CrearAsync(
                ComisionCalculadaSobre comisionSobre = ComisionCalculadaSobre.TotalCobrado,
                bool conTenant = false)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                if (conTenant)
                {
                    context.Tenants.Add(new Tenant
                    {
                        Id = tenantProvider.TenantId,
                        Nombre = "Northside Studio",
                        Activo = true,
                        PreciosIncluyenIva = true,
                        TarifaIvaPorDefecto = 13m
                    });
                    await context.SaveChangesAsync();
                }

                var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(
                    context, "Jordan", porcentajeServicio: 50m, comisionSobre: comisionSobre);

                return new Escenario(context, connection, tenantProvider, funcionario);
            }

            public Task<int> RegistrarAsync(int servicioId, int dia, decimal? monto = null) =>
                ControllerTestSupport.CreateCobroService(Context, TenantProvider)
                    .RegistrarAsync(new CobroCreateRequest
                    {
                        FechaCobro = new DateTime(2026, 9, dia, 10, 0, 0),
                        NombreCliente = "Cliente",
                        FuncionarioId = Funcionario.IdFuncionario,
                        ServicioId = servicioId,
                        Monto = monto ?? Monto,
                        MetodoPago = "EFECTIVO"
                    });

            public Task<PagosSemanaResumen> ResumenAsync(DateTime desde, DateTime hasta) =>
                ControllerTestSupport
                    .CreateLiquidacionSemanalService(Context, TenantProvider)
                    .ObtenerResumenSemanaAsync(desde, hasta);

            public Task<CobroIndexViewModel> IngresosAsync() =>
                ControllerTestSupport
                    .CreateCobroQueryService(Context, TenantProvider)
                    .BuildIndexViewModelAsync(FinanzasTestSupport.FiltroMes(2026, 9));

            public Task<CobroExportViewModel> ExcelAsync() =>
                ControllerTestSupport
                    .CreateCobroQueryService(Context, TenantProvider)
                    .BuildExportAsync(FinanzasTestSupport.FiltroMes(2026, 9));

            public Task<DashboardViewModel> DashboardAsync() =>
                ControllerTestSupport
                    .CreateDashboardFinancieroQueryService(Context, TenantProvider)
                    .BuildViewModelAsync(9, 2026);

            public async Task CambiarPorcentajeAsync(decimal porcentaje)
            {
                var funcionario = await Context.Funcionarios
                    .SingleAsync(f => f.IdFuncionario == Funcionario.IdFuncionario);
                funcionario.PorcentajeGanancia = porcentaje;
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            public async Task CambiarTarifaTenantAsync(decimal tarifa)
            {
                var tenant = await Context.Tenants.SingleAsync(t => t.Id == TenantProvider.TenantId);
                tenant.TarifaIvaPorDefecto = tarifa;
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }
    }
}
