using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Platform;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// Revertir un pago deshace el ACTO DE PAGO, nunca la producción.
    ///
    /// <para>
    /// Un pago correcto genera una operación financiera completa (liquidación + detalles +
    /// distribución mensual + egreso). Si se registró por error, la corrección válida es revertir
    /// esa operación entera —no editar el egreso por separado, que dejaría
    /// <c>Liquidacion.MontoTotal != Egreso.Monto</c>—.
    /// </para>
    /// </summary>
    public class ReversionPagoTests
    {
        private const int Anio = 2026;
        private const int Mes = 8;
        private const decimal MontoCobro = 100_000m;
        private const decimal ComisionDevengada = 44_247.79m;

        private static readonly DateTime SemanaInicio = new(Anio, Mes, 3);
        private static readonly DateTime SemanaFin = new(Anio, Mes, 9);
        private static readonly DateTime FechaCobro = new(Anio, Mes, 5, 9, 0, 0);
        private static readonly DateTime FechaPago = new(Anio, Mes, 10, 10, 0, 0);

        [Fact]
        public async Task RevertirPago_RestaMontoPagado_YAumentaPendienteExactamenteMismoMonto()
        {
            using var esc = await Escenario.ConPagoAsync();

            var pagado = await esc.ResumenDespuesDelPagoAsync();
            Assert.Equal(ComisionDevengada, pagado.MontoPagado);
            Assert.Equal(0m, pagado.MontoPendiente);

            var resultado = await esc.RevertirAsync("Registrado por error");

            Assert.Equal(ReversionPagoEstado.Revertida, resultado.Estado);
            Assert.Equal(ComisionDevengada, resultado.MontoRevertido);

            var revertido = await esc.ResumenAsync();
            Assert.Equal(0m, revertido.MontoPagado);
            Assert.Equal(ComisionDevengada, revertido.MontoPendiente);
            Assert.Equal(pagado.MontoPagado, revertido.MontoPendiente - pagado.MontoPendiente);
        }

        [Fact]
        public async Task RevertirPago_NoCambiaProduccion_NiComisionDevengada()
        {
            using var esc = await Escenario.ConPagoAsync();

            var antes = await esc.ResumenDespuesDelPagoAsync();
            await esc.RevertirAsync("Error de digitación");
            var despues = await esc.ResumenAsync();

            // Lo único que cambia es el acto de pago.
            Assert.Equal(antes.TotalServicios, despues.TotalServicios);
            Assert.Equal(antes.TotalProductos, despues.TotalProductos);
            Assert.Equal(antes.TotalGenerado, despues.TotalGenerado);
            Assert.Equal(antes.IvaVentaIncluido, despues.IvaVentaIncluido);
            Assert.Equal(antes.BaseVentaSinIva, despues.BaseVentaSinIva);
            Assert.Equal(antes.MontoColaborador, despues.MontoColaborador);
            Assert.Equal(antes.TotalAPagarColaborador, despues.TotalAPagarColaborador);
            Assert.Equal(ComisionDevengada, despues.TotalAPagarColaborador);
        }

        [Fact]
        public async Task RevertirPago_QuitaSalidaCaja_PeroNoCambiaResultadoAnalitico()
        {
            using var esc = await Escenario.ConPagoAsync();

            var antes = await esc.DashboardAsync();
            Assert.Equal(ComisionDevengada, antes.SalidasCajaMes);

            await esc.RevertirAsync("Pago duplicado");
            var despues = await esc.DashboardAsync();

            // La caja deja de contar ese pago…
            Assert.Equal(0m, despues.SalidasCajaMes);
            Assert.Empty(await esc.Context.Egresos.ToListAsync());

            // …y la ganancia NO se mueve: siempre fue devengada, nunca dependió de la caja.
            Assert.Equal(antes.ResultadoAnalitico, despues.ResultadoAnalitico);
            Assert.Equal(antes.Desglose!.GananciaDistribuible, despues.Desglose!.GananciaDistribuible);
            Assert.Equal(antes.TotalEgresosAnaliticos, despues.TotalEgresosAnaliticos);
            Assert.Equal(ComisionDevengada, despues.Desglose.LiquidacionesEquipo);
        }

        [Fact]
        public async Task RevertirPago_EliminaTodoElConjuntoDeRegistros()
        {
            using var esc = await Escenario.ConPagoAsync();

            Assert.Single(await esc.Context.LiquidacionesSemanales.ToListAsync());
            Assert.Single(await esc.Context.LiquidacionesSemanalesDetalle.ToListAsync());
            Assert.NotEmpty(await esc.Context.LiquidacionesSemanalesDistribucionMensual.ToListAsync());
            Assert.Single(await esc.Context.Egresos.ToListAsync());

            await esc.RevertirAsync("Limpieza");

            // Todo o nada: no puede quedar un egreso sin liquidación ni al revés.
            Assert.Empty(await esc.Context.LiquidacionesSemanales.ToListAsync());
            Assert.Empty(await esc.Context.LiquidacionesSemanalesDetalle.ToListAsync());
            Assert.Empty(await esc.Context.LiquidacionesSemanalesDistribucionMensual.ToListAsync());
            Assert.Empty(await esc.Context.Egresos.ToListAsync());
        }

        [Fact]
        public async Task RevertirPago_GuardaAuditoria()
        {
            using var esc = await Escenario.ConPagoAsync();
            var liquidacionId = esc.LiquidacionId;

            await esc.RevertirAsync("El cliente no había pagado todavía");

            var log = Assert.Single(await esc.Context.PlatformAuditLogs
                .Where(l => l.Action == PlatformAuditActions.EmployeeSettlementPaymentReverted)
                .ToListAsync());

            Assert.Equal(PlatformAuditEntityTypes.EmployeeSettlementPayment, log.EntityType);
            Assert.Equal(liquidacionId.ToString(), log.EntityId);
            Assert.Equal(esc.TenantId, log.TenantId);
            Assert.Equal("El cliente no había pagado todavía", log.Reason);

            // El snapshot permite responder cuánto era, de quién y de qué periodo.
            Assert.NotNull(log.BeforeJson);
            Assert.Contains("44247.79", log.BeforeJson!, StringComparison.Ordinal);
            Assert.Contains("Jordan", log.BeforeJson!, StringComparison.Ordinal);
            Assert.Contains("2026-08-03", log.BeforeJson!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RevertirPago_SinMotivo_EsRechazado()
        {
            using var esc = await Escenario.ConPagoAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                esc.RevertirAsync("   "));

            // Nada se tocó.
            Assert.Single(await esc.Context.LiquidacionesSemanales.ToListAsync());
            Assert.Single(await esc.Context.Egresos.ToListAsync());
        }

        [Fact]
        public async Task RevertirPago_DosVeces_EsIdempotente()
        {
            using var esc = await Escenario.ConPagoAsync();

            var primera = await esc.RevertirAsync("Doble click del usuario");
            var segunda = await esc.RevertirAsync("Doble click del usuario");

            Assert.Equal(ReversionPagoEstado.Revertida, primera.Estado);
            Assert.Equal(ReversionPagoEstado.YaRevertida, segunda.Estado);

            // Ni devuelve el pendiente dos veces ni deja dos bitácoras contradictorias.
            var resumen = await esc.ResumenAsync();
            Assert.Equal(ComisionDevengada, resumen.MontoPendiente);
            Assert.Equal(0m, resumen.MontoPagado);
            Assert.Single(await esc.Context.PlatformAuditLogs
                .Where(l => l.Action == PlatformAuditActions.EmployeeSettlementPaymentReverted)
                .ToListAsync());
        }

        [Fact]
        public async Task RevertirPago_IdInexistente_DevuelveNoEncontrada()
        {
            using var esc = await Escenario.ConPagoAsync();

            var resultado = await esc.RevertirAsync("Motivo", liquidacionId: esc.LiquidacionId + 9999);

            Assert.Equal(ReversionPagoEstado.NoEncontrada, resultado.Estado);
            Assert.Single(await esc.Context.LiquidacionesSemanales.ToListAsync());
        }

        [Fact]
        public async Task RevertirPago_OtroTenant_DebeSerRechazado()
        {
            using var esc = await Escenario.ConPagoAsync();

            // Otro negocio conoce el id y lo intenta. El filtro de tenant lo hace inexistente.
            var otroTenant = new TestTenantProvider { TenantId = Guid.NewGuid() };
            using var contextOtro = TestDbContextFactory.CreateSqliteContext(otroTenant, esc.Connection);
            var servicioOtro = ControllerTestSupport.CreateLiquidacionSemanalService(
                contextOtro, otroTenant, ControllerTestSupport.CreatePlatformAuditService(contextOtro));

            var resultado = await servicioOtro.RevertirPagoAsync(esc.LiquidacionId, "Intento cruzado", "intruso");

            Assert.Equal(ReversionPagoEstado.NoEncontrada, resultado.Estado);

            // El pago del tenant legítimo sigue intacto.
            var resumen = await esc.ResumenDespuesDelPagoAsync();
            Assert.Equal(ComisionDevengada, resumen.MontoPagado);
            Assert.Single(await esc.Context.LiquidacionesSemanales.ToListAsync());
            Assert.Single(await esc.Context.Egresos.ToListAsync());
        }

        [Fact]
        public async Task RevertirPago_Batch_ReversaTodoElAggregate()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var jordan = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var avery = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Avery");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);

            await FinanzasTestSupport.SeedCobroServicioAsync(context, jordan, servicio, FechaCobro, MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(context, avery, servicio, FechaCobro, MontoCobro);

            var auditService = ControllerTestSupport.CreatePlatformAuditService(context);
            var service = ControllerTestSupport.CreateLiquidacionSemanalService(context, tenantProvider, auditService);

            // Un solo pago para DOS colaboradores ⇒ un solo egreso por el total.
            var liquidacionId = await service.RegistrarPagoAsync(new RegistrarLiquidacionSemanalCommand
            {
                SemanaInicio = SemanaInicio,
                SemanaFin = SemanaFin,
                FechaPago = FechaPago,
                MetodoPago = "EFECTIVO",
                CreadoPor = "test",
                Detalles =
                {
                    new RegistrarLiquidacionSemanalDetalleCommand { FuncionarioId = jordan.IdFuncionario, MontoPagado = ComisionDevengada },
                    new RegistrarLiquidacionSemanalDetalleCommand { FuncionarioId = avery.IdFuncionario, MontoPagado = ComisionDevengada }
                }
            });

            var egreso = Assert.Single(await context.Egresos.ToListAsync());
            Assert.Equal(ComisionDevengada * 2, egreso.Monto);
            Assert.Equal(2, await context.LiquidacionesSemanalesDetalle.CountAsync());

            var resultado = await service.RevertirPagoAsync(liquidacionId, "Se pagó la semana equivocada", "admin");

            Assert.Equal(ReversionPagoEstado.Revertida, resultado.Estado);
            Assert.Equal(ComisionDevengada * 2, resultado.MontoRevertido);
            Assert.Contains("Jordan", resultado.Funcionarios);
            Assert.Contains("Avery", resultado.Funcionarios);

            // El lote completo se deshizo: ninguno queda pagado a medias.
            Assert.Empty(await context.Egresos.ToListAsync());
            Assert.Empty(await context.LiquidacionesSemanalesDetalle.ToListAsync());

            var resumen = await service.ObtenerResumenSemanaAsync(SemanaInicio, SemanaFin);
            foreach (var funcionarioId in new[] { jordan.IdFuncionario, avery.IdFuncionario })
            {
                var linea = resumen.Funcionarios.Single(f => f.FuncionarioId == funcionarioId);
                Assert.Equal(0m, linea.MontoPagado);
                Assert.Equal(ComisionDevengada, linea.MontoPendiente);
            }
        }

        [Fact]
        public async Task RevertirPago_PermiteVolverAPagarCorrectamente()
        {
            using var esc = await Escenario.ConPagoAsync();

            await esc.RevertirAsync("Monto equivocado");

            // Tras revertir, el pendiente está disponible otra vez y se puede pagar bien.
            var nuevoId = await FinanzasTestSupport.PagarAsync(
                esc.Context, esc.TenantProvider, esc.Funcionario,
                SemanaInicio, SemanaFin, FechaPago, ComisionDevengada,
                auditService: ControllerTestSupport.CreatePlatformAuditService(esc.Context));

            Assert.NotEqual(esc.LiquidacionId, nuevoId);

            var resumen = await esc.ResumenDespuesDelPagoAsync();
            Assert.Equal(ComisionDevengada, resumen.MontoPagado);
            Assert.Equal(0m, resumen.MontoPendiente);
            Assert.Single(await esc.Context.Egresos.ToListAsync());
        }

        /// <summary>Escenario mínimo: una producción de agosto ya pagada.</summary>
        private sealed class Escenario : IDisposable
        {
            public required ProyectoIdentity.Datos.ApplicationDbContext Context { get; init; }
            public required Microsoft.Data.Sqlite.SqliteConnection Connection { get; init; }
            public required TestTenantProvider TenantProvider { get; init; }
            public required Funcionario Funcionario { get; init; }
            public required int LiquidacionId { get; init; }
            public Guid TenantId => TenantProvider.TenantId;

            public static async Task<Escenario> ConPagoAsync()
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
                var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
                await FinanzasTestSupport.SeedCobroServicioAsync(
                    context, funcionario, servicio, FechaCobro, MontoCobro);

                var liquidacionId = await FinanzasTestSupport.PagarAsync(
                    context, tenantProvider, funcionario,
                    SemanaInicio, SemanaFin, FechaPago, ComisionDevengada,
                    auditService: ControllerTestSupport.CreatePlatformAuditService(context));

                return new Escenario
                {
                    Context = context,
                    Connection = connection,
                    TenantProvider = tenantProvider,
                    Funcionario = funcionario,
                    LiquidacionId = liquidacionId
                };
            }

            public Task<ReversionPagoResultado> RevertirAsync(string motivo, int? liquidacionId = null) =>
                ControllerTestSupport
                    .CreateLiquidacionSemanalService(
                        Context, TenantProvider, ControllerTestSupport.CreatePlatformAuditService(Context))
                    .RevertirPagoAsync(liquidacionId ?? LiquidacionId, motivo, "admin-test");

            /// <summary>Estado del colaborador en la semana (tras revertir = como antes de pagar).</summary>
            public async Task<PagoFuncionarioVM> ResumenAsync()
            {
                var resumen = await ControllerTestSupport
                    .CreateLiquidacionSemanalService(Context, TenantProvider)
                    .ObtenerResumenSemanaAsync(SemanaInicio, SemanaFin);

                return resumen.Funcionarios.Single(f => f.FuncionarioId == Funcionario.IdFuncionario);
            }

            public Task<PagoFuncionarioVM> ResumenDespuesDelPagoAsync() => ResumenAsync();

            public Task<LuxuryApp.Models.Finanzas.DashboardViewModel> DashboardAsync() =>
                ControllerTestSupport
                    .CreateDashboardFinancieroQueryService(Context, TenantProvider)
                    .BuildViewModelAsync(Mes, Anio);

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }
    }
}
