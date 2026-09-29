using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Inversionistas
{
    /// <summary>
    /// Invariante central del dinero de LuxuryCloud:
    ///
    /// <para><b>mismo tenant + mismo rango de fechas + mismos datos ⇒ mismo resultado</b>,
    /// sin importar si quien pregunta es el Dashboard financiero o el estado de cuenta de un
    /// inversionista.</para>
    ///
    /// <para>
    /// Antes no era cierto: el Dashboard dividía el total del mes entre 1,13, restaba las
    /// liquidaciones por lo PAGADO y no excluía la categoría de distribución a inversionistas.
    /// Estas pruebas existen para que esa divergencia no pueda volver.
    /// </para>
    /// </summary>
    public sealed class SharedProfitCalculationTests : IDisposable
    {
        private readonly Guid _tenantId = Guid.NewGuid();
        private readonly TestTenantProvider _tenantProvider;
        private readonly ApplicationDbContext _context;
        private readonly SqliteConnection _connection;
        private readonly FakePlatformAuditService _audit = new();

        public SharedProfitCalculationTests()
        {
            _tenantProvider = new TestTenantProvider { TenantId = _tenantId };
            (_context, _connection) = TestDbContextFactory.CreateSqliteContext(_tenantProvider);
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Fact]
        public async Task MismoRango_DashboardEInversionista_DanExactamenteElMismoDesglose()
        {
            await SeedNegocioConMovimientosAsync();

            // Consumidor 1: el Dashboard financiero, sobre el mes calendario de abril.
            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(_context, _tenantProvider);
            var vm = await dashboard.BuildViewModelAsync(4, 2026);

            // Consumidor 2: el motor, invocado con el MISMO rango que usa el inversionista.
            var motor = InvestorTestSupport.CreateCalculationService(_context, _tenantProvider);
            var policy = await InvestorTestSupport.CreateInvestorService(_context, _audit).GetPolicyAsync();

            var breakdown = await motor.CalculateAsync(
                new DateOnly(2026, 4, 1),
                new DateOnly(2026, 4, 30),
                policy);

            // Campo por campo, hasta el último decimal.
            Assert.NotNull(vm.Desglose);
            Assert.Equal(breakdown.TotalCobrado, vm.Desglose!.TotalCobrado);
            Assert.Equal(breakdown.IvaCobrado, vm.Desglose.IvaCobrado);
            Assert.Equal(breakdown.IngresosNetos, vm.Desglose.IngresosNetos);
            Assert.Equal(breakdown.GastosOperativos, vm.Desglose.GastosOperativos);
            Assert.Equal(breakdown.LiquidacionesEquipo, vm.Desglose.LiquidacionesEquipo);
            Assert.Equal(breakdown.GananciaDistribuible, vm.Desglose.GananciaDistribuible);

            // Y los números que el Dashboard muestra en pantalla salen de ese mismo desglose.
            Assert.Equal(breakdown.IngresosNetos, vm.TotalSinImpuestos);
            Assert.Equal(breakdown.IvaCobrado, vm.TotalImpuestos);
            Assert.Equal(breakdown.LiquidacionesEquipo, vm.TotalPagadoFuncionariosAnalitico);
            Assert.Equal(breakdown.GananciaDistribuible, vm.ResultadoAnalitico);
        }

        [Fact]
        public async Task LaSerieAnualDelGrafico_UsaElMismoCalculoQueElTitularDelMes()
        {
            await SeedNegocioConMovimientosAsync();

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(_context, _tenantProvider);
            var vm = await dashboard.BuildViewModelAsync(4, 2026);

            // La barra de abril del gráfico y el número grande de arriba no pueden contradecirse.
            Assert.Equal(vm.ResultadoAnalitico, vm.ResultadoAnaliticoPorMes[3]);
        }

        [Fact]
        public async Task RangosDISTINTOS_PuedenDarNumerosDistintos_YEstaBien()
        {
            await SeedNegocioConMovimientosAsync();

            // Un cobro que cae fuera del mes calendario pero dentro del corte del inversionista.
            var funcionario = await _context.Funcionarios.FirstAsync();
            await InvestorTestSupport.SeedCobroSinIvaAsync(
                _context,
                funcionario.IdFuncionario,
                new DateTime(2026, 3, 25),
                500_000m);

            var motor = InvestorTestSupport.CreateCalculationService(_context, _tenantProvider);
            var policy = await InvestorTestSupport.CreateInvestorService(_context, _audit).GetPolicyAsync();

            var mesCalendario = await motor.CalculateAsync(
                new DateOnly(2026, 4, 1),
                new DateOnly(2026, 4, 30),
                policy);

            // Periodo contractual con corte el 20: del 21/03 al 20/04.
            var periodoDeCorte = await motor.CalculateAsync(
                new DateOnly(2026, 3, 21),
                new DateOnly(2026, 4, 20),
                policy);

            // No tienen por qué coincidir: son rangos distintos. Lo que NO puede pasar es que el
            // mismo rango dé dos resultados.
            Assert.NotEqual(mesCalendario.IngresosNetos, periodoDeCorte.IngresosNetos);
        }

        [Fact]
        public async Task Ganancia3858Con40Porciento_Da1543Con36()
        {
            // Caso exacto del enunciado: 36 000 cobrados con IVA incluido.
            var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(_context, "Ana", porcentajeGanancia: 50m);
            await InvestorTestSupport.SeedCobroConIvaAsync(
                _context,
                funcionario.IdFuncionario,
                new DateTime(2026, 4, 10),
                36_000m);

            // Gasto calibrado para que la ganancia distribuible caiga exactamente en 3 858,41:
            //   31 858,41 (base) − 15 929,20 (50 % de comisión) − 12 070,80 = 3 858,41
            // La comisión es 15 929,20 y no ...,21 porque el redondeo de la aplicación es
            // half-even: 15 929,205 cae al par.
            await SeedGastoAsync("Alquiler", new DateTime(2026, 4, 12), 12_070.80m);

            var motor = InvestorTestSupport.CreateCalculationService(_context, _tenantProvider);
            var policy = await InvestorTestSupport.CreateInvestorService(_context, _audit).GetPolicyAsync();

            var breakdown = await motor.CalculateAsync(
                new DateOnly(2026, 4, 1),
                new DateOnly(2026, 4, 30),
                policy);

            Assert.Equal(36_000m, breakdown.TotalCobrado);
            Assert.Equal(4_141.59m, breakdown.IvaCobrado);
            Assert.Equal(31_858.41m, breakdown.IngresosNetos);
            Assert.Equal(15_929.20m, breakdown.LiquidacionesEquipo);
            Assert.Equal(12_070.80m, breakdown.GastosOperativos);
            Assert.Equal(3_858.41m, breakdown.GananciaDistribuible);

            // 40 % de 3 858,41 = 1 543,364 → 1 543,36 con el redondeo de la aplicación (half-even).
            var participacion = LuxuryApp.Models.Fiscal.FiscalMath.Redondear(
                breakdown.GananciaDistribuible * 40m / 100m);

            Assert.Equal(1_543.36m, participacion);
        }

        [Fact]
        public async Task PagarAlInversionista_NoReduceLaGananciaEnNingunoDeLosDosConsumidores()
        {
            await SeedNegocioConMovimientosAsync();

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(_context, _tenantProvider);
            var antes = await dashboard.BuildViewModelAsync(4, 2026);

            // Salida de dinero hacia el inversionista, en la categoría reservada.
            await SeedGastoAsync(
                InvestorDefaults.CategoriaDistribucionInversionistas,
                new DateTime(2026, 4, 28),
                250_000m);

            var despues = await dashboard.BuildViewModelAsync(4, 2026);

            // El Dashboard tampoco la cuenta como gasto: si lo hiciera, la ganancia bajaría y con
            // ella la participación del propio inversionista (recursividad).
            Assert.Equal(antes.ResultadoAnalitico, despues.ResultadoAnalitico);
            Assert.Equal(antes.TotalEgresosAnaliticos, despues.TotalEgresosAnaliticos);

            // Y sí aparece en la vista de CAJA, que es donde corresponde.
            Assert.True(despues.SalidasCajaMes > antes.SalidasCajaMes);
        }

        // ─────────────── Semillas ───────────────

        private async Task SeedNegocioConMovimientosAsync()
        {
            var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(_context, "Ana", porcentajeGanancia: 50m);

            await InvestorTestSupport.SeedCobroConIvaAsync(
                _context, funcionario.IdFuncionario, new DateTime(2026, 4, 3), 120_000m);
            await InvestorTestSupport.SeedCobroSinIvaAsync(
                _context, funcionario.IdFuncionario, new DateTime(2026, 4, 18), 80_000m);

            await SeedGastoAsync("Alquiler", new DateTime(2026, 4, 5), 45_000m);
            await SeedGastoAsync("Insumos", new DateTime(2026, 4, 22), 12_500m);
        }

        private async Task SeedGastoAsync(string categoria, DateTime fecha, decimal monto)
        {
            var existente = await _context.Categorias.FirstOrDefaultAsync(c => c.Nombre == categoria);

            if (existente is null)
            {
                existente = new Categoria { Nombre = categoria, Detalle = categoria, Activo = true };
                _context.Categorias.Add(existente);
                await _context.SaveChangesAsync();
            }

            _context.Egresos.Add(new Egreso
            {
                Detalle = $"{categoria} {fecha:dd/MM}",
                Monto = monto,
                FechaEgreso = fecha,
                CategoriaId = existente.Id,
                MetodoPago = "EFECTIVO"
            });

            await _context.SaveChangesAsync();
        }
    }
}
