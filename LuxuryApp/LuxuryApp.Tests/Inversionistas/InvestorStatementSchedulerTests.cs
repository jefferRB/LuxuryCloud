using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Inversionistas
{
    /// <summary>
    /// Cierre automático de cortes.
    ///
    /// <para>Lo que estas pruebas protegen:</para>
    /// <list type="bullet">
    ///   <item>Nunca cerrar al inicio del propio día de corte.</item>
    ///   <item>Recuperar los cortes perdidos si el worker estuvo caído, en orden.</item>
    ///   <item>Idempotencia: correr dos veces no duplica nada.</item>
    ///   <item>Generar y enviar son pasos independientes.</item>
    /// </list>
    ///
    /// <para>Escenario base: Jorge, 40 %, corte el día 20, acuerdo desde el 21/08/2026.</para>
    /// </summary>
    public sealed class InvestorStatementSchedulerTests
    {
        // ─────────────── Interruptores ───────────────

        [Fact]
        public async Task ConElInterruptorMaestroApagado_NoGeneraNada()
        {
            using var fixture = await SchedulerFixture.CreateAsync(
                new DateTime(2026, 9, 21, 10, 0, 0),
                schedulerEnabled: false);

            await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var resultado = await fixture.RunAsync();

            Assert.Equal(InvestorStatementScheduleOutcome.SchedulerDisabled, resultado.Outcome);
            Assert.Equal(0, await fixture.Context.InvestorStatements.CountAsync());
        }

        [Fact]
        public async Task SiElNegocioNoActivoLaGeneracionAutomatica_NoGeneraNada()
        {
            using var fixture = await SchedulerFixture.CreateAsync(
                new DateTime(2026, 9, 21, 10, 0, 0),
                generacionAutomatica: false);

            await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var resultado = await fixture.RunAsync();

            Assert.Equal(InvestorStatementScheduleOutcome.NotEnabled, resultado.Outcome);
            Assert.Equal(0, await fixture.Context.InvestorStatements.CountAsync());
        }

        [Fact]
        public async Task AntesDeLaHoraConfigurada_NoGenera()
        {
            using var fixture = await SchedulerFixture.CreateAsync(
                new DateTime(2026, 9, 21, 6, 0, 0),
                horaGeneracion: 8);

            await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var resultado = await fixture.RunAsync();

            Assert.Equal(InvestorStatementScheduleOutcome.NotDue, resultado.Outcome);
            Assert.Equal(0, await fixture.Context.InvestorStatements.CountAsync());
        }

        // ─────────────── Cierre del período ───────────────

        [Fact]
        public async Task ElDiaDelCorte_TodaviaNoCierra()
        {
            // 20/09: el día de corte pertenece al período que cierra, así que el período recién
            // termina cuando ESE día termina. Cerrar a las 10 a. m. perdería las ventas del día.
            using var fixture = await SchedulerFixture.CreateAsync(new DateTime(2026, 9, 20, 10, 0, 0));

            await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var resultado = await fixture.RunAsync();

            Assert.Equal(InvestorStatementScheduleOutcome.NothingPending, resultado.Outcome);
            Assert.Equal(0, await fixture.Context.InvestorStatements.CountAsync());
        }

        [Fact]
        public async Task ElDiaSiguienteAlCorte_GeneraElEstadoYaCongelado()
        {
            using var fixture = await SchedulerFixture.CreateAsync(new DateTime(2026, 9, 21, 10, 0, 0));

            await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var resultado = await fixture.RunAsync();

            Assert.Equal(InvestorStatementScheduleOutcome.Generated, resultado.Outcome);
            Assert.Equal(1, resultado.Generados);

            var statement = await fixture.Context.InvestorStatements.SingleAsync();

            Assert.Equal(new DateOnly(2026, 8, 21), statement.PeriodoInicio);
            Assert.Equal(new DateOnly(2026, 9, 20), statement.PeriodoFin);
            Assert.Equal(new DateOnly(2026, 9, 20), statement.FechaCorte);
            Assert.Equal(250_000m, statement.GananciaDistribuible);
            Assert.Equal(100_000m, statement.ParticipacionCalculada);

            // Un período cerrado se congela: dejarlo en borrador lo expondría a recalcularse.
            Assert.Equal(InvestorStatementStatus.Finalized, statement.Estado);
            Assert.NotNull(statement.FinalizadoAtUtc);
        }

        [Fact]
        public async Task ConDiasDeEspera_NoGeneraHastaQuePasenEsosDias()
        {
            using var fixture = await SchedulerFixture.CreateAsync(
                new DateTime(2026, 9, 22, 10, 0, 0),
                diasEspera: 3);

            await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            Assert.Equal(
                InvestorStatementScheduleOutcome.NothingPending,
                (await fixture.RunAsync()).Outcome);
            Assert.Equal(0, await fixture.Context.InvestorStatements.CountAsync());

            // Al tercer día sí.
            var resultado = await fixture.RunAsync(new DateTime(2026, 9, 23, 10, 0, 0));

            Assert.Equal(1, resultado.Generados);
        }

        // ─────────────── Resiliencia ───────────────

        [Fact]
        public async Task SiElWorkerEstuvoCaido_GeneraTodosLosCortesPerdidosEnOrden()
        {
            // Último corte esperado: 20/09. El acuerdo arrancó el 21/05 y nunca se generó nada.
            using var fixture = await SchedulerFixture.CreateAsync(new DateTime(2026, 9, 25, 10, 0, 0));

            await fixture.SeedJorgeAsync(new DateOnly(2026, 5, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 6, 10), 100_000m);
            await fixture.SeedCobroAsync(new DateTime(2026, 7, 10), 200_000m);
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var resultado = await fixture.RunAsync();

            Assert.Equal(4, resultado.Generados);

            var cortes = await fixture.Context.InvestorStatements
                .OrderBy(statement => statement.PeriodoInicio)
                .ToListAsync();

            Assert.Equal(
                new[]
                {
                    new DateOnly(2026, 6, 20),
                    new DateOnly(2026, 7, 20),
                    new DateOnly(2026, 8, 20),
                    new DateOnly(2026, 9, 20)
                },
                cortes.Select(corte => corte.FechaCorte));

            // Cada corte solo ve el dinero de SU rango.
            Assert.Equal(100_000m, cortes[0].GananciaDistribuible);
            Assert.Equal(200_000m, cortes[1].GananciaDistribuible);
            Assert.Equal(0m, cortes[2].GananciaDistribuible);
            Assert.Equal(250_000m, cortes[3].GananciaDistribuible);

            // El período en curso (21/09 → 20/10) NO se genera: todavía está abierto.
            Assert.DoesNotContain(cortes, corte => corte.PeriodoFin == new DateOnly(2026, 10, 20));
        }

        [Fact]
        public async Task CorrerDosVeces_NoDuplicaCortes()
        {
            using var fixture = await SchedulerFixture.CreateAsync(new DateTime(2026, 9, 25, 10, 0, 0));

            await fixture.SeedJorgeAsync(new DateOnly(2026, 5, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var primera = await fixture.RunAsync();
            var segunda = await fixture.RunAsync();

            Assert.Equal(4, primera.Generados);
            Assert.Equal(0, segunda.Generados);
            Assert.Equal(InvestorStatementScheduleOutcome.NothingPending, segunda.Outcome);
            Assert.Equal(4, await fixture.Context.InvestorStatements.CountAsync());
        }

        [Fact]
        public async Task UnBorradorManualDelPeriodo_NoSeToca()
        {
            using var fixture = await SchedulerFixture.CreateAsync(new DateTime(2026, 9, 21, 10, 0, 0));

            var investorId = await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            // Alguien ya venía trabajando ese corte a mano.
            var manual = await fixture.Statements.GenerateDraftAsync(
                investorId, new DateOnly(2026, 9, 20), "admin");

            var resultado = await fixture.RunAsync();

            Assert.Equal(0, resultado.Generados);

            var statement = await fixture.Context.InvestorStatements.SingleAsync();
            Assert.Equal(manual, statement.Id);

            // El worker NO lo finaliza a la fuerza: sigue siendo el borrador de esa persona.
            Assert.Equal(InvestorStatementStatus.Draft, statement.Estado);
        }

        // ─────────────── Generar ≠ enviar ───────────────

        [Fact]
        public async Task ConLosEnviosApagados_GeneraPeroNoManda()
        {
            using var fixture = await SchedulerFixture.CreateAsync(
                new DateTime(2026, 9, 21, 10, 0, 0),
                sendEmails: false,
                envioAutomatico: true);

            await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var resultado = await fixture.RunAsync();

            Assert.Equal(1, resultado.Generados);
            Assert.Equal(0, resultado.Enviados);
            Assert.Empty(fixture.Emails.Enviados);
            Assert.Equal(1, await fixture.Context.InvestorStatements.CountAsync());
        }

        [Fact]
        public async Task ConEnviosYEnvioAutomatico_GeneraYManda()
        {
            using var fixture = await SchedulerFixture.CreateAsync(
                new DateTime(2026, 9, 21, 10, 0, 0),
                sendEmails: true,
                envioAutomatico: true);

            await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var resultado = await fixture.RunAsync();

            Assert.Equal(1, resultado.Generados);
            Assert.Equal(1, resultado.Enviados);
            Assert.Single(fixture.Emails.Enviados);
        }

        [Fact]
        public async Task SinEnvioAutomatico_NoMandaAunqueLosEnviosEstenActivos()
        {
            using var fixture = await SchedulerFixture.CreateAsync(
                new DateTime(2026, 9, 21, 10, 0, 0),
                sendEmails: true,
                envioAutomatico: false);

            await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var resultado = await fixture.RunAsync();

            Assert.Equal(1, resultado.Generados);
            Assert.Empty(fixture.Emails.Enviados);
        }

        [Fact]
        public async Task UnCorreoQueFalla_NoDeshaceElCorteGenerado()
        {
            using var fixture = await SchedulerFixture.CreateAsync(
                new DateTime(2026, 9, 21, 10, 0, 0),
                sendEmails: true,
                envioAutomatico: true);

            fixture.Emails.Falla = true;

            await fixture.SeedJorgeAsync(new DateOnly(2026, 8, 21));
            await fixture.SeedCobroAsync(new DateTime(2026, 9, 5), 250_000m);

            var resultado = await fixture.RunAsync();

            // El estado existe y quedó congelado; solo el envío falló.
            Assert.Equal(1, resultado.Generados);
            Assert.Equal(0, resultado.Enviados);
            Assert.Equal(1, resultado.EnviosFallidos);

            var statement = await fixture.Context.InvestorStatements.SingleAsync();
            Assert.Equal(InvestorStatementStatus.Finalized, statement.Estado);
        }

        // ─────────────── Fixture ───────────────

        private sealed class SchedulerFixture : IDisposable
        {
            public required ApplicationDbContext Context { get; init; }

            public required SqliteConnection Connection { get; init; }

            public required TestTenantProvider TenantProvider { get; init; }

            public required FakePlatformAuditService Audit { get; init; }

            public required InvestorStatementService Statements { get; init; }

            public required FakeStatementEmailService Emails { get; init; }

            public required InvestorStatementSchedulerOptions Options { get; init; }

            public int FuncionarioId { get; init; }

            private DateTime AhoraLocal { get; set; }

            private bool EnvioAutomaticoDelAcuerdo { get; set; }

            public static async Task<SchedulerFixture> CreateAsync(
                DateTime ahoraLocal,
                bool schedulerEnabled = true,
                bool generacionAutomatica = true,
                bool sendEmails = false,
                bool envioAutomatico = false,
                int diasEspera = 1,
                int horaGeneracion = 8)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
                var audit = new FakePlatformAuditService();
                var clock = new FixedBusinessDateTimeProvider(ahoraLocal);

                await InvestorTestSupport.SeedPolicyAsync(context, policy =>
                {
                    policy.GeneracionAutomatica = generacionAutomatica;
                    policy.EnvioAutomatico = envioAutomatico;
                    policy.DiasEsperaGeneracion = diasEspera;
                    policy.HoraGeneracion = horaGeneracion;
                });

                var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(context, "Base");

                return new SchedulerFixture
                {
                    Context = context,
                    Connection = connection,
                    TenantProvider = tenantProvider,
                    Audit = audit,
                    Statements = InvestorTestSupport.CreateStatementService(
                        context, tenantProvider, audit, investorService: null, clock),
                    Emails = new FakeStatementEmailService(),
                    Options = new InvestorStatementSchedulerOptions
                    {
                        SchedulerEnabled = schedulerEnabled,
                        SendEmails = sendEmails
                    },
                    FuncionarioId = funcionario.IdFuncionario,
                    AhoraLocal = ahoraLocal,
                    EnvioAutomaticoDelAcuerdo = envioAutomatico
                };
            }

            /// <summary>Jorge: 40 %, corte el día 20.</summary>
            public async Task<int> SeedJorgeAsync(DateOnly vigenteDesde)
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
                    ParticipacionPorcentaje = 40m,
                    EffectiveFrom = vigenteDesde,
                    Frecuencia = InvestorPayoutFrequency.Mensual,
                    DiaCorte = 20,
                    EnvioAutomatico = EnvioAutomaticoDelAcuerdo,
                    Activo = true
                });

                await Context.SaveChangesAsync();
                return investor.Id;
            }

            public Task SeedCobroAsync(DateTime fecha, decimal monto) =>
                InvestorTestSupport.SeedCobroSinIvaAsync(Context, FuncionarioId, fecha, monto);

            public Task<InvestorStatementScheduleResult> RunAsync(DateTime? ahoraLocal = null)
            {
                var reloj = ahoraLocal ?? AhoraLocal;

                // El scheduler se arma con el mismo reloj de la corrida: los cierres se miden en
                // hora local del negocio.
                var clock = new FixedBusinessDateTimeProvider(reloj);
                var investors = InvestorTestSupport.CreateInvestorService(Context, Audit, clock);

                var scheduler = new InvestorStatementScheduler(
                    Context,
                    investors,
                    InvestorTestSupport.CreateStatementService(
                        Context, TenantProvider, Audit, investors, clock),
                    Emails,
                    new TestOptionsMonitor<InvestorStatementSchedulerOptions>(Options),
                    NullLogger<InvestorStatementScheduler>.Instance);

                return scheduler.ProcessTenantAsync(TenantProvider.TenantId, reloj);
            }

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }

        /// <summary>Doble del envío: acá solo interesa SI se manda, no cómo se renderiza.</summary>
        private sealed class FakeStatementEmailService : IInvestorStatementEmailService
        {
            public List<int> Enviados { get; } = new();

            public bool Falla { get; set; }

            public Task<InvestorStatementSendResult> SendAsync(
                int statementId,
                string? userId,
                CancellationToken cancellationToken = default)
            {
                if (Falla)
                {
                    return Task.FromResult(InvestorStatementSendResult.Failed("Correo caído."));
                }

                Enviados.Add(statementId);
                return Task.FromResult(InvestorStatementSendResult.Sent("Enviado."));
            }

            public Task<InvestorStatementSendResult> ResendAsync(
                int statementId,
                string? userId,
                CancellationToken cancellationToken = default) =>
                SendAsync(statementId, userId, cancellationToken);

            public Task<InvestorStatementSendResult> SendTestAsync(
                int statementId,
                string recipientEmail,
                string? userId,
                CancellationToken cancellationToken = default) =>
                SendAsync(statementId, userId, cancellationToken);

            public Task<(byte[] Content, string FileName)?> BuildPdfAsync(
                int statementId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<(byte[], string)?>(null);
        }
    }
}
