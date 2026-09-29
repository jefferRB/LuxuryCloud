using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Tests.Support;
using Microsoft.Data.Sqlite;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Asociados
{
    /// <summary>
    /// KPI de participación del Dashboard financiero.
    ///
    /// <para>
    /// Lo importante acá no son los números en sí (esos ya los cubre
    /// <c>InvestorStatementServiceTests</c>): es que el KPI use el MISMO motor de ganancia que los
    /// estados de cuenta, que no exista cuando no hay participaciones, y que un pago hecho al
    /// inversionista no vuelva a reducir la ganancia.
    /// </para>
    /// </summary>
    public sealed class AssociateProfitAllocationServiceTests : IDisposable
    {
        private readonly Guid _tenantId = Guid.NewGuid();
        private readonly TestTenantProvider _tenantProvider;
        private readonly ApplicationDbContext _context;
        private readonly SqliteConnection _connection;
        private readonly FakePlatformAuditService _audit = new();

        public AssociateProfitAllocationServiceTests()
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
        public async Task SinParticipacionesVigentes_NoHayKpi()
        {
            var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(_context, "Ana");
            await InvestorTestSupport.SeedCobroSinIvaAsync(_context, funcionario.IdFuncionario, new DateTime(2026, 4, 10), 500_000m);

            var service = AssociateTestSupport.CreateAllocationService(_context, _tenantProvider, _audit);
            var kpi = await service.BuildMonthlyKpiAsync(4, 2026);

            // No hay bloque que mostrar: el dato no viaja al ViewModel ni al HTML.
            Assert.Null(kpi);
        }

        [Fact]
        public async Task ConUnInversionistaAl40_MuestraSuParticipacion()
        {
            var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(_context, "Ana");
            await InvestorTestSupport.SeedCobroSinIvaAsync(_context, funcionario.IdFuncionario, new DateTime(2026, 4, 10), 1_000_000m);

            await InvestorTestSupport.SeedInvestorAsync(
                _context, "Jairo", "jairo@test.local", 40m, new DateOnly(2026, 1, 1));

            var service = AssociateTestSupport.CreateAllocationService(_context, _tenantProvider, _audit);
            var kpi = await service.BuildMonthlyKpiAsync(4, 2026);

            Assert.NotNull(kpi);
            Assert.Equal(1_000_000m, kpi!.GananciaDistribuible);
            Assert.Equal(40m, kpi.ParticipacionPorcentaje);
            Assert.Equal(400_000m, kpi.ParticipacionMonto);
            Assert.Equal(600_000m, kpi.GananciaRestante);
            Assert.Equal(60m, kpi.PorcentajeRestante);
            Assert.Equal(1, kpi.CantidadAsociados);
        }

        [Fact]
        public async Task ConDosInversionistas_SumaAmbasParticipaciones()
        {
            var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(_context, "Ana");
            await InvestorTestSupport.SeedCobroSinIvaAsync(_context, funcionario.IdFuncionario, new DateTime(2026, 4, 10), 1_000_000m);

            await InvestorTestSupport.SeedInvestorAsync(
                _context, "Jairo", "jairo@test.local", 40m, new DateOnly(2026, 1, 1));
            await InvestorTestSupport.SeedInvestorAsync(
                _context, "Pedro", "pedro@test.local", 10m, new DateOnly(2026, 1, 1));

            var service = AssociateTestSupport.CreateAllocationService(_context, _tenantProvider, _audit);
            var kpi = await service.BuildMonthlyKpiAsync(4, 2026);

            // El KPI es del negocio, no de una persona: suma a todos los que participan.
            Assert.NotNull(kpi);
            Assert.Equal(50m, kpi!.ParticipacionPorcentaje);
            Assert.Equal(500_000m, kpi.ParticipacionMonto);
            Assert.Equal(2, kpi.CantidadAsociados);
        }

        [Fact]
        public async Task PagarAlInversionista_NoVuelveAReducirLaGanancia()
        {
            var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(_context, "Ana");
            await InvestorTestSupport.SeedCobroSinIvaAsync(_context, funcionario.IdFuncionario, new DateTime(2026, 4, 10), 1_000_000m);

            await InvestorTestSupport.SeedInvestorAsync(
                _context, "Jairo", "jairo@test.local", 40m, new DateOnly(2026, 1, 1));

            var service = AssociateTestSupport.CreateAllocationService(_context, _tenantProvider, _audit);
            var antes = await service.BuildMonthlyKpiAsync(4, 2026);

            // El negocio registra la salida de dinero hacia el inversionista como egreso, en la
            // categoría reservada. Si contara como gasto, pagarle reduciría su propia participación.
            var categoria = new Categoria
            {
                Nombre = InvestorDefaults.CategoriaDistribucionInversionistas,
                Detalle = "Salidas de dinero hacia inversionistas",
                Activo = true
            };

            _context.Categorias.Add(categoria);
            await _context.SaveChangesAsync();

            _context.Egresos.Add(new Egreso
            {
                Detalle = "Distribución abril",
                Monto = 400_000m,
                FechaEgreso = new DateTime(2026, 4, 30),
                CategoriaId = categoria.Id,
                MetodoPago = "SINPE"
            });

            await _context.SaveChangesAsync();

            var despues = await service.BuildMonthlyKpiAsync(4, 2026);

            Assert.NotNull(antes);
            Assert.NotNull(despues);
            Assert.Equal(antes!.GananciaDistribuible, despues!.GananciaDistribuible);
            Assert.Equal(400_000m, despues.ParticipacionMonto);
        }

        [Fact]
        public async Task ConPerdida_NoRepartNadaPeroMuestraElNumeroReal()
        {
            var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(_context, "Ana");
            await InvestorTestSupport.SeedCobroSinIvaAsync(_context, funcionario.IdFuncionario, new DateTime(2026, 4, 10), 100_000m);

            var categoria = new Categoria { Nombre = "Alquiler", Detalle = "Local", Activo = true };
            _context.Categorias.Add(categoria);
            await _context.SaveChangesAsync();

            _context.Egresos.Add(new Egreso
            {
                Detalle = "Alquiler abril",
                Monto = 300_000m,
                FechaEgreso = new DateTime(2026, 4, 5),
                CategoriaId = categoria.Id,
                MetodoPago = "EFECTIVO"
            });

            await _context.SaveChangesAsync();

            await InvestorTestSupport.SeedInvestorAsync(
                _context, "Jairo", "jairo@test.local", 40m, new DateOnly(2026, 1, 1));

            var service = AssociateTestSupport.CreateAllocationService(_context, _tenantProvider, _audit);
            var kpi = await service.BuildMonthlyKpiAsync(4, 2026);

            Assert.NotNull(kpi);
            Assert.Equal(-200_000m, kpi!.GananciaDistribuible);

            // En pérdida no se reparte: nadie "debe" plata por participar.
            Assert.Equal(0m, kpi.ParticipacionMonto);
        }

        [Fact]
        public async Task AcuerdoQueNoCubreElPeriodo_NoEntraEnElKpi()
        {
            var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(_context, "Ana");
            await InvestorTestSupport.SeedCobroSinIvaAsync(_context, funcionario.IdFuncionario, new DateTime(2026, 4, 10), 1_000_000m);

            // Participación que terminó en marzo: abril ya no le corresponde.
            await InvestorTestSupport.SeedInvestorAsync(
                _context,
                "Jairo",
                "jairo@test.local",
                40m,
                new DateOnly(2026, 1, 1),
                effectiveTo: new DateOnly(2026, 3, 31));

            var service = AssociateTestSupport.CreateAllocationService(_context, _tenantProvider, _audit);
            var kpi = await service.BuildMonthlyKpiAsync(4, 2026);

            Assert.Null(kpi);
        }
    }
}
