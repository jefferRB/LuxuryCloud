using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Inversionistas
{
    /// <summary>
    /// Estados de cuenta con día de corte: qué rango cubren, qué congelan y qué pasa cuando el
    /// corte cambia después.
    ///
    /// <para>
    /// El reloj de prueba está fijo en el 26/05/2026 (hora local del negocio), así que los
    /// periodos "ya cerrados" se miden contra esa fecha, nunca contra UTC.
    /// </para>
    /// </summary>
    public sealed class InvestorCutoffStatementTests : IDisposable
    {
        private readonly Guid _tenantId = Guid.NewGuid();
        private readonly TestTenantProvider _tenantProvider;
        private readonly ApplicationDbContext _context;
        private readonly SqliteConnection _connection;
        private readonly FakePlatformAuditService _audit = new();

        public InvestorCutoffStatementTests()
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
        public async Task ConCorte20_ElEstadoCubreDel21AlDia20_YCongelaLaFechaDeCorte()
        {
            var investorId = await SeedInversionistaConCorteAsync(40m, diaCorte: 20);
            await SeedCobroAsync(new DateTime(2026, 4, 25), 100_000m);

            var service = InvestorTestSupport.CreateStatementService(_context, _tenantProvider, _audit);

            // Cualquier día dentro del periodo sirve como referencia.
            var statementId = await service.GenerateDraftAsync(
                investorId,
                new DateOnly(2026, 5, 10),
                userId: "admin-1");

            var statement = await _context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(new DateOnly(2026, 4, 21), statement.PeriodoInicio);
            Assert.Equal(new DateOnly(2026, 5, 20), statement.PeriodoFin);

            // El corte queda congelado junto al porcentaje.
            Assert.Equal(20, statement.DiaCorte);
            Assert.Equal(new DateOnly(2026, 5, 20), statement.FechaCorte);
        }

        [Fact]
        public async Task ElMovimientoDelDiaDeCorte_EntraEnElPeriodoQueCierra()
        {
            var investorId = await SeedInversionistaConCorteAsync(100m, diaCorte: 20);

            // Un cobro el 20 (día de corte) y otro el 21 (ya del periodo siguiente).
            await SeedCobroAsync(new DateTime(2026, 5, 20), 50_000m);
            await SeedCobroAsync(new DateTime(2026, 5, 21), 70_000m);

            var service = InvestorTestSupport.CreateStatementService(_context, _tenantProvider, _audit);

            var cierraEl20 = await service.GenerateDraftAsync(investorId, new DateOnly(2026, 5, 20), "admin-1");
            var statement = await _context.InvestorStatements.SingleAsync(s => s.Id == cierraEl20);

            Assert.Equal(new DateOnly(2026, 5, 20), statement.PeriodoFin);

            // Solo el cobro del día 20 entró: el del 21 pertenece al periodo siguiente.
            Assert.Equal(50_000m, statement.IngresosCobrados);
        }

        [Fact]
        public async Task CambiarElDiaDeCorte_NoModificaLosEstadosYaEmitidos()
        {
            var investorId = await SeedInversionistaConCorteAsync(40m, diaCorte: 20);
            await SeedCobroAsync(new DateTime(2026, 4, 25), 100_000m);

            var statementService = InvestorTestSupport.CreateStatementService(_context, _tenantProvider, _audit);
            var statementId = await statementService.GenerateDraftAsync(
                investorId,
                new DateOnly(2026, 5, 10),
                "admin-1");

            await statementService.FinalizeAsync(statementId, "admin-1");

            var antes = await _context.InvestorStatements.AsNoTracking().SingleAsync(s => s.Id == statementId);

            // El negocio cambia el corte al día 15 desde el próximo periodo.
            var investorService = InvestorTestSupport.CreateInvestorService(_context, _audit);
            await investorService.UpdateAsync(
                investorId,
                new InvestorFormViewModel
                {
                    Id = investorId,
                    Nombre = "Jorge",
                    Email = "jorge@test.local",
                    Activo = true,
                    ParticipacionPorcentaje = 35m,
                    DiaCorte = 15,
                    Frecuencia = InvestorPayoutFrequency.Mensual,
                    // Con corte 15 el próximo periodo abre el día 16.
                    EffectiveFrom = new DateTime(2026, 5, 16)
                },
                "admin-1");

            var despues = await _context.InvestorStatements.AsNoTracking().SingleAsync(s => s.Id == statementId);

            // Ni las fechas, ni el corte, ni el porcentaje, ni los montos del estado histórico.
            Assert.Equal(antes.PeriodoInicio, despues.PeriodoInicio);
            Assert.Equal(antes.PeriodoFin, despues.PeriodoFin);
            Assert.Equal(antes.FechaCorte, despues.FechaCorte);
            Assert.Equal(20, despues.DiaCorte);
            Assert.Equal(40m, despues.ParticipacionPorcentaje);
            Assert.Equal(antes.ParticipacionCalculada, despues.ParticipacionCalculada);

            // Y el acuerdo quedó versionado: el viejo cerrado, el nuevo abierto.
            var acuerdos = await _context.InvestorAgreements
                .AsNoTracking()
                .OrderBy(a => a.EffectiveFrom)
                .ToListAsync();

            Assert.Equal(2, acuerdos.Count);
            Assert.Equal(20, acuerdos[0].DiaCorte);
            Assert.Equal(new DateOnly(2026, 5, 15), acuerdos[0].EffectiveTo);
            Assert.Equal(15, acuerdos[1].DiaCorte);
            Assert.Null(acuerdos[1].EffectiveTo);
        }

        [Fact]
        public async Task CambiarElCorte_AMitadDePeriodo_EsRechazadoConLaFechaCorrecta()
        {
            var investorId = await SeedInversionistaConCorteAsync(40m, diaCorte: 20);
            var investorService = InvestorTestSupport.CreateInvestorService(_context, _audit);

            var ex = await Assert.ThrowsAsync<LuxuryApp.Services.Inversionistas.InvestorValidationException>(() =>
                investorService.UpdateAsync(
                    investorId,
                    new InvestorFormViewModel
                    {
                        Id = investorId,
                        Nombre = "Jorge",
                        Email = "jorge@test.local",
                        Activo = true,
                        ParticipacionPorcentaje = 35m,
                        DiaCorte = 20,
                        Frecuencia = InvestorPayoutFrequency.Mensual,
                        // El día 1 ya no abre un periodo cuando el corte es el 20.
                        EffectiveFrom = new DateTime(2026, 6, 1)
                    },
                    "admin-1"));

            // El mensaje tiene que decir exactamente qué fecha usar: con corte 20, el día 21.
            Assert.Contains("21/06/2026", ex.Message);
        }

        [Fact]
        public async Task GenerarDosVecesElMismoPeriodo_NoDuplicaElEstado()
        {
            var investorId = await SeedInversionistaConCorteAsync(40m, diaCorte: 20);
            await SeedCobroAsync(new DateTime(2026, 4, 25), 100_000m);

            var service = InvestorTestSupport.CreateStatementService(_context, _tenantProvider, _audit);

            // Dos pasadas del mismo cierre (lo que haría un worker al reintentar).
            var primera = await service.GenerateDraftAsync(investorId, new DateOnly(2026, 5, 10), "worker");
            var segunda = await service.GenerateDraftAsync(investorId, new DateOnly(2026, 5, 3), "worker");

            Assert.Equal(primera, segunda);
            Assert.Single(await _context.InvestorStatements.ToListAsync());
        }

        [Fact]
        public async Task AcuerdoSinDiaDeCorte_ConservaElMesCalendario()
        {
            var investorId = await SeedInversionistaConCorteAsync(40m, diaCorte: null);
            await SeedCobroAsync(new DateTime(2026, 4, 15), 100_000m);

            var service = InvestorTestSupport.CreateStatementService(_context, _tenantProvider, _audit);
            var statementId = await service.GenerateDraftAsync(investorId, new DateOnly(2026, 4, 15), "admin-1");

            var statement = await _context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(new DateOnly(2026, 4, 1), statement.PeriodoInicio);
            Assert.Equal(new DateOnly(2026, 4, 30), statement.PeriodoFin);
            Assert.Null(statement.DiaCorte);
            Assert.Equal(new DateOnly(2026, 4, 30), statement.FechaCorte);
        }

        [Fact]
        public async Task LaVistaPrevia_SinReferencia_MuestraElUltimoPeriodoYaCerrado()
        {
            // Hoy es 26/05/2026 y el corte es el 20: el último periodo cerrado es 21/04 → 20/05.
            var investorId = await SeedInversionistaConCorteAsync(40m, diaCorte: 20);

            var service = InvestorTestSupport.CreateStatementService(_context, _tenantProvider, _audit);
            var preview = await service.PreviewAsync(investorId, referencia: null);

            Assert.Equal(new DateOnly(2026, 4, 21), preview.PeriodoInicio);
            Assert.Equal(new DateOnly(2026, 5, 20), preview.PeriodoFin);
            Assert.Equal(new DateOnly(2026, 5, 20), preview.FechaCorte);
            Assert.Equal("Corte mensual · día 20", preview.CorteTexto);
        }

        // ─────────────── Semillas ───────────────

        private async Task<int> SeedInversionistaConCorteAsync(decimal porcentaje, int? diaCorte)
        {
            var investor = new TenantInvestor
            {
                Nombre = "Jorge",
                Email = "jorge@test.local",
                Activo = true
            };

            _context.TenantInvestors.Add(investor);
            await _context.SaveChangesAsync();

            _context.InvestorAgreements.Add(new InvestorAgreement
            {
                InvestorId = investor.Id,
                ParticipacionPorcentaje = porcentaje,
                EffectiveFrom = new DateOnly(2026, 1, 1),
                Frecuencia = InvestorPayoutFrequency.Mensual,
                DiaCorte = diaCorte,
                Activo = true
            });

            await _context.SaveChangesAsync();
            return investor.Id;
        }

        private async Task SeedCobroAsync(DateTime fecha, decimal monto)
        {
            var funcionario = await _context.Funcionarios.FirstOrDefaultAsync()
                ?? await InvestorTestSupport.SeedFuncionarioAsync(_context, "Ana");

            await InvestorTestSupport.SeedCobroSinIvaAsync(_context, funcionario.IdFuncionario, fecha, monto);
        }
    }
}
