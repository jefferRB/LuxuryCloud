using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// Idempotencia del botón "Pagar".
    ///
    /// <para>
    /// El agujero que cierran estos tests: validar el pendiente NO impide el doble pago. Con
    /// pendiente ₡44.247,79 y un pago PARCIAL de ₡20.000, dos POST pasan la validación
    /// (20.000 ≤ 44.247,79 y luego 20.000 ≤ 24.247,79) y se paga el doble de lo que el usuario
    /// quiso. Lo único que lo impide es una clave de intención con índice único.
    /// </para>
    /// </summary>
    public class IdempotenciaPagoTests
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
        public async Task RegistrarPago_MismaIdempotencyKey_DosPOST_CreaUnSoloPago()
        {
            var (context, connection, tenantProvider, funcionario) = await SeedAsync();
            using var c = context;
            using var cn = connection;

            var key = Guid.NewGuid();

            var primero = await PagarAsync(context, tenantProvider, funcionario, ComisionDevengada, key);
            var segundo = await PagarAsync(context, tenantProvider, funcionario, ComisionDevengada, key);

            Assert.Equal(primero, segundo);
            Assert.Single(await context.LiquidacionesSemanales.ToListAsync());
            Assert.Single(await context.Egresos.ToListAsync());
            Assert.Single(await context.LiquidacionesSemanalesDetalle.ToListAsync());

            var resumen = await ResumenAsync(context, tenantProvider, funcionario);
            Assert.Equal(ComisionDevengada, resumen.MontoPagado);
            Assert.Equal(0m, resumen.MontoPendiente);
        }

        [Fact]
        public async Task RegistrarPagoParcial_MismaKey_CreaUnSoloEgreso()
        {
            var (context, connection, tenantProvider, funcionario) = await SeedAsync();
            using var c = context;
            using var cn = connection;

            var key = Guid.NewGuid();
            const decimal parcial = 20_000m;

            // Doble click sobre un pago PARCIAL: el segundo cabría de sobra en el pendiente.
            await PagarAsync(context, tenantProvider, funcionario, parcial, key);
            await PagarAsync(context, tenantProvider, funcionario, parcial, key);

            var egreso = Assert.Single(await context.Egresos.ToListAsync());
            Assert.Equal(parcial, egreso.Monto);

            var resumen = await ResumenAsync(context, tenantProvider, funcionario);
            Assert.Equal(parcial, resumen.MontoPagado);
            Assert.Equal(ComisionDevengada - parcial, resumen.MontoPendiente);
        }

        [Fact]
        public async Task RegistrarPago_RetryTrasCommit_NoDuplica()
        {
            var (context, connection, tenantProvider, funcionario) = await SeedAsync();
            using var c = context;
            using var cn = connection;

            var key = Guid.NewGuid();
            var id = await PagarAsync(context, tenantProvider, funcionario, ComisionDevengada, key);

            // Un reintento de la ExecutionStrategy (COMMIT ocurrido, ACK perdido) vuelve a ejecutar
            // exactamente la misma intención, incluso desde otro contexto/petición.
            using var otroContexto = TestDbContextFactory.CreateSqliteContext(tenantProvider, connection);
            var reintento = await PagarAsync(otroContexto, tenantProvider, funcionario, ComisionDevengada, key);

            Assert.Equal(id, reintento);
            Assert.Single(await context.LiquidacionesSemanales.ToListAsync());
            Assert.Single(await context.Egresos.ToListAsync());
        }

        [Fact]
        public async Task RegistrarPagosConKeysDistintas_FuncionanNormalmente()
        {
            var (context, connection, tenantProvider, funcionario) = await SeedAsync();
            using var c = context;
            using var cn = connection;

            // Dos intenciones DISTINTAS (dos pagos parciales reales) deben registrarse ambas.
            var primero = await PagarAsync(context, tenantProvider, funcionario, 20_000m, Guid.NewGuid());
            var segundo = await PagarAsync(context, tenantProvider, funcionario, 10_000m, Guid.NewGuid());

            Assert.NotEqual(primero, segundo);
            Assert.Equal(2, await context.LiquidacionesSemanales.CountAsync());
            Assert.Equal(30_000m, await context.Egresos.SumAsync(e => e.Monto));

            var resumen = await ResumenAsync(context, tenantProvider, funcionario);
            Assert.Equal(30_000m, resumen.MontoPagado);
            Assert.Equal(ComisionDevengada - 30_000m, resumen.MontoPendiente);
        }

        [Fact]
        public async Task RegistrarPago_SinIdempotencyKey_ConservaComportamientoAnterior()
        {
            var (context, connection, tenantProvider, funcionario) = await SeedAsync();
            using var c = context;
            using var cn = connection;

            // Compatibilidad: sin clave (llamadas antiguas) el flujo sigue funcionando igual.
            var primero = await PagarAsync(context, tenantProvider, funcionario, 10_000m, idempotencyKey: null);
            var segundo = await PagarAsync(context, tenantProvider, funcionario, 10_000m, idempotencyKey: null);

            Assert.NotEqual(primero, segundo);
            Assert.Equal(2, await context.LiquidacionesSemanales.CountAsync());
        }

        [Fact]
        public async Task IdempotencyKey_MismoGuid_DistintoTenant_NoColisiona()
        {
            var (context, connection, tenantProvider, funcionario) = await SeedAsync();
            using var c = context;
            using var cn = connection;

            // Mismo GUID en dos negocios distintos: el índice es (TenantId, IdempotencyKey).
            var key = Guid.NewGuid();
            var idA = await PagarAsync(context, tenantProvider, funcionario, ComisionDevengada, key);

            var tenantB = new TestTenantProvider { TenantId = Guid.NewGuid() };
            using var contextB = TestDbContextFactory.CreateSqliteContext(tenantB, connection);
            var funcionarioB = await FinanzasTestSupport.SeedFuncionarioAsync(contextB, "Otro negocio");
            var servicioB = await FinanzasTestSupport.SeedServicioAsync(contextB, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(contextB, funcionarioB, servicioB, FechaCobro, MontoCobro);

            var idB = await PagarAsync(contextB, tenantB, funcionarioB, ComisionDevengada, key);

            Assert.NotEqual(idA, idB);
            Assert.Equal(2, await contextB.LiquidacionesSemanales.IgnoreQueryFilters().CountAsync());
        }

        private static Task<int> PagarAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            Funcionario funcionario,
            decimal monto,
            Guid? idempotencyKey) =>
            ControllerTestSupport
                .CreateLiquidacionSemanalService(context, tenantProvider)
                .RegistrarPagoAsync(new RegistrarLiquidacionSemanalCommand
                {
                    SemanaInicio = SemanaInicio,
                    SemanaFin = SemanaFin,
                    FechaPago = FechaPago,
                    MetodoPago = "EFECTIVO",
                    CreadoPor = "test",
                    IdempotencyKey = idempotencyKey,
                    Detalles =
                    {
                        new RegistrarLiquidacionSemanalDetalleCommand
                        {
                            FuncionarioId = funcionario.IdFuncionario,
                            MontoPagado = monto
                        }
                    }
                });

        private static async Task<PagoFuncionarioVM> ResumenAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            Funcionario funcionario)
        {
            var resumen = await ControllerTestSupport
                .CreateLiquidacionSemanalService(context, tenantProvider)
                .ObtenerResumenSemanaAsync(SemanaInicio, SemanaFin);

            return resumen.Funcionarios.Single(f => f.FuncionarioId == funcionario.IdFuncionario);
        }

        private static async Task<(ProyectoIdentity.Datos.ApplicationDbContext Context,
            Microsoft.Data.Sqlite.SqliteConnection Connection,
            TestTenantProvider TenantProvider,
            Funcionario Funcionario)> SeedAsync()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(context, funcionario, servicio, FechaCobro, MontoCobro);

            return (context, connection, tenantProvider, funcionario);
        }
    }
}
