using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Inversionistas
{
    /// <summary>
    /// Separación entre CICLO ABIERTO y CORTE EMITIDO, que es lo que la pantalla del inversionista
    /// mezclaba antes.
    ///
    /// <para>Escenario base (el mismo del acuerdo real que motivó el cambio):</para>
    /// <list type="bullet">
    ///   <item>Jorge, 40 %, corte el día 20.</item>
    ///   <item>Acuerdo vigente desde el 21/08/2026 (arranque de período, como exige el versionado).</item>
    /// </list>
    ///
    /// <para>El reloj de cada prueba se fija explícitamente: los cierres se miden en hora local del
    /// negocio, nunca en UTC.</para>
    /// </summary>
    public sealed class InvestorCycleSummaryTests
    {
        private static readonly DateOnly VigenteDesde = new(2026, 8, 21);
        private const int DiaCorte = 20;
        private const decimal Porcentaje = 40m;

        // ─────────────── Último corte vs. fecha teórica ───────────────

        [Fact]
        public async Task SinCortesEmitidos_NoInventaUltimoCorte_YAnunciaElPrimero()
        {
            // 27/08/2026: el acuerdo arrancó hace menos de una semana y todavía no cerró nada.
            using var fixture = await CycleFixture.CreateAsync(new DateTime(2026, 8, 27, 10, 0, 0));
            var investorId = await fixture.SeedJorgeAsync();

            var resumen = await fixture.Cycles.BuildSummaryAsync(investorId);

            Assert.NotNull(resumen);
            Assert.Null(resumen!.UltimoCorte);
            Assert.False(resumen.TieneCortes);

            // El PRIMER corte con corte 20 y acuerdo desde el 21/08 es el 20/09, no el 20/08.
            Assert.Equal(new DateOnly(2026, 9, 20), resumen.PrimerCorte);
            Assert.Contains("20/09/2026", resumen.EmptyStateDetalle);
        }

        [Fact]
        public async Task ElCorteTeoricoDelResolver_NoEsElUltimoCorteEmitido()
        {
            using var fixture = await CycleFixture.CreateAsync(new DateTime(2026, 8, 27, 10, 0, 0));
            var investorId = await fixture.SeedJorgeAsync();

            var acuerdo = await fixture.Context.InvestorAgreements.SingleAsync(a => a.InvestorId == investorId);

            // Aritmética de calendario: el "período cerrado anterior" termina el 20/08...
            var teorico = InvestorSettlementPeriodResolver.PreviousClosedPeriod(acuerdo, new DateOnly(2026, 8, 27));
            Assert.Equal(new DateOnly(2026, 8, 20), teorico.Fin);

            // ...pero ese corte NUNCA se emitió (ni siquiera existía el acuerdo). El resumen no lo
            // muestra como "último corte": esa era exactamente la mentira de la pantalla anterior.
            var resumen = await fixture.Cycles.BuildSummaryAsync(investorId);
            Assert.Null(resumen!.UltimoCorte);
        }

        // ─────────────── Ciclo actual ───────────────

        [Fact]
        public async Task CicloActual_AbreElDiaSiguienteAlCorte_YNoCuentaDiasFuturos()
        {
            using var fixture = await CycleFixture.CreateAsync(new DateTime(2026, 8, 27, 10, 0, 0));
            var investorId = await fixture.SeedJorgeAsync();

            // Antes del período (pertenece al corte anterior): no cuenta.
            await fixture.SeedCobroAsync(new DateTime(2026, 8, 15), 500_000m);
            // Dentro del período y ya ocurrido: sí cuenta.
            await fixture.SeedCobroAsync(new DateTime(2026, 8, 22), 60_495.58m);
            // Dentro del período pero en el FUTURO respecto de hoy: no puede contarse.
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 10), 900_000m);

            var ciclo = await fixture.Cycles.BuildCurrentCycleAsync(investorId);

            Assert.NotNull(ciclo);
            Assert.True(ciclo!.TieneAcuerdoVigente);

            // El ciclo abre el día siguiente al corte anterior y cierra el día de corte.
            Assert.Equal(new DateOnly(2026, 8, 21), ciclo.PeriodoInicio);
            Assert.Equal(new DateOnly(2026, 9, 20), ciclo.PeriodoFin);
            Assert.Equal(new DateOnly(2026, 9, 20), ciclo.ProximoCorte);

            // Tope del cálculo: hoy.
            Assert.Equal(new DateOnly(2026, 8, 27), ciclo.CalculadoAl);
            Assert.Equal(60_495.58m, ciclo.GananciaDistribuible);
            Assert.Equal(24_198.23m, ciclo.ParticipacionEstimada);
        }

        [Fact]
        public async Task EstimacionDelCicloActual_NoEntraEnElSaldoPendiente()
        {
            using var fixture = await CycleFixture.CreateAsync(new DateTime(2026, 8, 27, 10, 0, 0));
            var investorId = await fixture.SeedJorgeAsync();
            await fixture.SeedCobroAsync(new DateTime(2026, 8, 22), 60_495.58m);

            var resumen = await fixture.Cycles.BuildSummaryAsync(investorId);

            Assert.Equal(24_198.23m, resumen!.CicloActual!.ParticipacionEstimada);

            // Un período abierto no es deuda: el saldo sigue en cero.
            Assert.Equal(0m, resumen.SaldoPendienteTotal);
            Assert.Equal(0, resumen.EstadosConSaldo);
            Assert.Equal(0, resumen.EstadosEmitidos);
        }

        // ─────────────── Cortes emitidos y pagos ───────────────

        [Fact]
        public async Task CorteEmitido_EntraEnElSaldo_YElCicloSigueSeparado()
        {
            // 27/09/2026: el corte del 20/09 ya cerró.
            using var fixture = await CycleFixture.CreateAsync(new DateTime(2026, 9, 27, 10, 0, 0));
            var investorId = await fixture.SeedJorgeAsync();
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var statementId = await fixture.Statements.GenerateDraftAsync(
                investorId, new DateOnly(2026, 9, 20), "admin");
            await fixture.Statements.FinalizeAsync(statementId, "admin");

            var resumen = await fixture.Cycles.BuildSummaryAsync(investorId);

            Assert.NotNull(resumen!.UltimoCorte);
            Assert.Equal(new DateOnly(2026, 9, 20), resumen.UltimoCorte!.FechaCorte);
            Assert.Equal(250_000m, resumen.UltimoCorte.GananciaDistribuible);
            Assert.Equal(100_000m, resumen.UltimoCorte.ParticipacionCalculada);
            Assert.Equal(0m, resumen.UltimoCorte.TotalPagado);
            Assert.Equal(100_000m, resumen.UltimoCorte.SaldoPendiente);
            Assert.Null(resumen.PrimerCorte);

            // El monto del corte SÍ es deuda.
            Assert.Equal(100_000m, resumen.SaldoPendienteTotal);
            Assert.Equal(1, resumen.EstadosConSaldo);

            // Y el ciclo siguiente arranca limpio el día después del corte.
            Assert.Equal(new DateOnly(2026, 9, 21), resumen.CicloActual!.PeriodoInicio);
            Assert.Equal(new DateOnly(2026, 10, 20), resumen.CicloActual.PeriodoFin);
            Assert.Equal(0m, resumen.CicloActual.ParticipacionEstimada);
        }

        [Fact]
        public async Task PagoParcial_ActualizaPagadoYPendiente_SinTocarElCiclo()
        {
            using var fixture = await CycleFixture.CreateAsync(new DateTime(2026, 9, 27, 10, 0, 0));
            var investorId = await fixture.SeedJorgeAsync();
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var statementId = await fixture.Statements.GenerateDraftAsync(
                investorId, new DateOnly(2026, 9, 20), "admin");
            await fixture.Statements.FinalizeAsync(statementId, "admin");

            await fixture.Statements.RegisterPaymentAsync(
                new InvestorPaymentFormViewModel
                {
                    StatementId = statementId,
                    Fecha = new DateTime(2026, 9, 25),
                    Monto = 60_000m,
                    MetodoPago = "SINPE"
                },
                "admin",
                "admin@test.cr");

            var resumen = await fixture.Cycles.BuildSummaryAsync(investorId);

            Assert.Equal(100_000m, resumen!.UltimoCorte!.ParticipacionCalculada);
            Assert.Equal(60_000m, resumen.UltimoCorte.TotalPagado);
            Assert.Equal(40_000m, resumen.UltimoCorte.SaldoPendiente);
            Assert.Equal(InvestorStatementStatus.PartiallyPaid, resumen.UltimoCorte.Estado);
            Assert.Equal(InvestorVisualTone.Warning, resumen.UltimoCorte.Tono);

            Assert.Equal(40_000m, resumen.SaldoPendienteTotal);
            Assert.Equal(0m, resumen.CicloActual!.ParticipacionEstimada);
        }

        [Fact]
        public async Task UnBorrador_NoCuentaComoCorteEmitido_NiComoSaldo()
        {
            using var fixture = await CycleFixture.CreateAsync(new DateTime(2026, 9, 27, 10, 0, 0));
            var investorId = await fixture.SeedJorgeAsync();
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            // Generado pero SIN finalizar: sus montos todavía pueden cambiar.
            await fixture.Statements.GenerateDraftAsync(investorId, new DateOnly(2026, 9, 20), "admin");

            var resumen = await fixture.Cycles.BuildSummaryAsync(investorId);

            Assert.Null(resumen!.UltimoCorte);
            Assert.Equal(0m, resumen.SaldoPendienteTotal);
            Assert.Equal(0, resumen.EstadosEmitidos);
            Assert.Equal(1, resumen.Borradores);

            // Pero no se esconde: aparece en el historial reciente marcado como borrador.
            var reciente = Assert.Single(resumen.CortesRecientes);
            Assert.Equal(InvestorStatementStatus.Draft, reciente.Estado);
        }

        // ─────────────── Snapshot inmutable ───────────────

        [Fact]
        public async Task CorteFinalizado_ConservaSuSnapshot_AunqueCambienLosCobrosDelPeriodo()
        {
            using var fixture = await CycleFixture.CreateAsync(new DateTime(2026, 9, 27, 10, 0, 0));
            var investorId = await fixture.SeedJorgeAsync();
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var statementId = await fixture.Statements.GenerateDraftAsync(
                investorId, new DateOnly(2026, 9, 20), "admin");
            await fixture.Statements.FinalizeAsync(statementId, "admin");

            // Alguien corrige la historia DESPUÉS de emitir el corte: agrega un cobro y un gasto
            // dentro del mismo período ya cerrado.
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 8), 1_000_000m);
            await InvestorTestSupport.SeedEgresoAsync(
                fixture.Context, "Alquiler", new DateTime(2026, 9, 9), 300_000m);

            var detalle = await fixture.Statements.BuildDetailAsync(statementId);
            var resumen = await fixture.Cycles.BuildSummaryAsync(investorId);

            // El estado emitido NO se mueve: sigue mostrando exactamente lo que congeló.
            Assert.Equal(250_000m, detalle!.Desglose.GananciaDistribuible);
            Assert.Equal(100_000m, detalle.Desglose.ParticipacionCalculada);
            Assert.Equal(0m, detalle.Desglose.GastosElegibles);
            Assert.Equal(100_000m, resumen!.UltimoCorte!.ParticipacionCalculada);

            // El ciclo EN CURSO sí es live: ve el cobro nuevo del 8/09 sólo si cae en su rango.
            // (No cae: ese cobro pertenece al período ya cerrado.)
            Assert.Equal(new DateOnly(2026, 9, 21), resumen.CicloActual!.PeriodoInicio);
            Assert.Equal(0m, resumen.CicloActual.GananciaDistribuible);
        }

        // ─────────────── Navegación entre cortes ───────────────

        [Fact]
        public async Task Navegacion_EnlazaElCorteAnteriorYElSiguiente()
        {
            using var fixture = await CycleFixture.CreateAsync(new DateTime(2026, 11, 27, 10, 0, 0));
            var investorId = await fixture.SeedJorgeAsync();

            var septiembre = await fixture.EmitirCorteAsync(investorId, new DateOnly(2026, 9, 20));
            var octubre = await fixture.EmitirCorteAsync(investorId, new DateOnly(2026, 10, 20));
            var noviembre = await fixture.EmitirCorteAsync(investorId, new DateOnly(2026, 11, 20));

            var medio = await fixture.Statements.BuildDetailAsync(octubre);

            Assert.Equal(septiembre, medio!.Navegacion.AnteriorId);
            Assert.Equal(new DateOnly(2026, 9, 20), medio.Navegacion.AnteriorCorte);
            Assert.Equal(noviembre, medio.Navegacion.SiguienteId);
            Assert.Equal(new DateOnly(2026, 11, 20), medio.Navegacion.SiguienteCorte);

            // El más viejo no tiene anterior; el más nuevo no tiene siguiente.
            var primero = await fixture.Statements.BuildDetailAsync(septiembre);
            Assert.Null(primero!.Navegacion.AnteriorId);
            Assert.Equal(octubre, primero.Navegacion.SiguienteId);

            var ultimo = await fixture.Statements.BuildDetailAsync(noviembre);
            Assert.Equal(octubre, ultimo!.Navegacion.AnteriorId);
            Assert.Null(ultimo.Navegacion.SiguienteId);
        }

        // ─────────────── Aislamiento por tenant ───────────────

        [Fact]
        public async Task ElResumen_NoCruzaTenants()
        {
            using var fixture = await CycleFixture.CreateAsync(new DateTime(2026, 9, 27, 10, 0, 0));
            var investorId = await fixture.SeedJorgeAsync();

            // Mismo contexto, otro tenant activo: el inversionista deja de existir para él.
            fixture.TenantProvider.TenantId = Guid.NewGuid();

            var resumen = await fixture.Cycles.BuildSummaryAsync(investorId);
            var ciclo = await fixture.Cycles.BuildCurrentCycleAsync(investorId);

            Assert.Null(resumen);
            Assert.Null(ciclo);
        }

        // ─────────────── Fixture ───────────────

        private sealed class CycleFixture : IDisposable
        {
            public required ApplicationDbContext Context { get; init; }

            public required SqliteConnection Connection { get; init; }

            public required TestTenantProvider TenantProvider { get; init; }

            public required InvestorStatementService Statements { get; init; }

            public required InvestorCycleService Cycles { get; init; }

            public int FuncionarioId { get; init; }

            public static async Task<CycleFixture> CreateAsync(DateTime ahoraLocal)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
                var audit = new FakePlatformAuditService();
                var clock = new FixedBusinessDateTimeProvider(ahoraLocal);

                await InvestorTestSupport.SeedPolicyAsync(context);

                // Colaborador sin comisión: aísla el cálculo del motor de liquidaciones.
                var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(context, "Base");

                var investors = InvestorTestSupport.CreateInvestorService(context, audit, clock);
                var statements = InvestorTestSupport.CreateStatementService(
                    context, tenantProvider, audit, investors, clock);

                return new CycleFixture
                {
                    Context = context,
                    Connection = connection,
                    TenantProvider = tenantProvider,
                    Statements = statements,
                    Cycles = InvestorTestSupport.CreateCycleService(
                        context, tenantProvider, audit, investors, statements, clock),
                    FuncionarioId = funcionario.IdFuncionario
                };
            }

            /// <summary>Jorge: 40 %, corte el día 20, vigente desde el 21/08/2026.</summary>
            public async Task<int> SeedJorgeAsync()
            {
                var investor = new TenantInvestor
                {
                    Nombre = "Jorge",
                    Email = "jorge@test.cr",
                    Activo = true
                };

                Context.TenantInvestors.Add(investor);
                await Context.SaveChangesAsync();

                Context.InvestorAgreements.Add(new InvestorAgreement
                {
                    InvestorId = investor.Id,
                    ParticipacionPorcentaje = Porcentaje,
                    EffectiveFrom = VigenteDesde,
                    Frecuencia = InvestorPayoutFrequency.Mensual,
                    DiaCorte = DiaCorte,
                    TratamientoPerdidas = InvestorLossTreatment.NoDistribution,
                    Activo = true
                });

                await Context.SaveChangesAsync();
                return investor.Id;
            }

            /// <summary>Cobro exento de IVA: el ingreso neto es exactamente el monto.</summary>
            public Task SeedCobroAsync(DateTime fecha, decimal monto) =>
                InvestorTestSupport.SeedCobroSinIvaAsync(Context, FuncionarioId, fecha, monto);

            /// <summary>Genera y finaliza el corte que contiene la fecha indicada.</summary>
            public async Task<int> EmitirCorteAsync(int investorId, DateOnly referencia)
            {
                var id = await Statements.GenerateDraftAsync(investorId, referencia, "admin");
                await Statements.FinalizeAsync(id, "admin");
                return id;
            }

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }
    }
}
