using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.Inversionistas
{
    /// <summary>
    /// Envío del estado de participación: idempotencia, reenvío explícito, contenido derivado del
    /// snapshot y privacidad del correo.
    /// </summary>
    public class InvestorStatementEmailTests
    {
        private static readonly DateOnly PeriodoJunio = new(2026, 6, 1);

        [Fact]
        public async Task Enviar_DosVeces_SoloMandaElCorreoUnaVez()
        {
            using var fixture = await EmailFixture.CreateAsync();

            var primero = await fixture.Emails.SendAsync(fixture.StatementId, "admin", default);
            var segundo = await fixture.Emails.SendAsync(fixture.StatementId, "admin", default);

            Assert.Equal(InvestorStatementSendOutcome.Sent, primero.Outcome);
            Assert.Equal(InvestorStatementSendOutcome.Skipped, segundo.Outcome);
            Assert.Single(fixture.Sender.Sent);

            var logs = await fixture.Context.InvestorStatementEmailLogs.ToListAsync();
            Assert.Equal(1, logs.Count(log => log.Status == InvestorStatementEmailStatus.Sent));
            Assert.Equal(1, logs.Count(log => log.Status == InvestorStatementEmailStatus.Skipped));
        }

        [Fact]
        public async Task Reenviar_EsExplicitoYUsaUnaClaveDeIdempotenciaNueva()
        {
            using var fixture = await EmailFixture.CreateAsync();

            await fixture.Emails.SendAsync(fixture.StatementId, "admin", default);
            var reenvio = await fixture.Emails.ResendAsync(fixture.StatementId, "admin", default);

            Assert.Equal(InvestorStatementSendOutcome.Sent, reenvio.Outcome);
            Assert.Equal(2, fixture.Sender.Sent.Count);
            Assert.NotEqual(fixture.Sender.Sent[0].IdempotencyKey, fixture.Sender.Sent[1].IdempotencyKey);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.InvestorStatementResent));
        }

        [Fact]
        public async Task Enviar_MarcaElEstadoComoEnviado()
        {
            using var fixture = await EmailFixture.CreateAsync();

            await fixture.Emails.SendAsync(fixture.StatementId, "admin", default);

            var statement = await fixture.Context.InvestorStatements
                .SingleAsync(s => s.Id == fixture.StatementId);

            Assert.Equal(InvestorStatementStatus.Sent, statement.Estado);
            Assert.NotNull(statement.EnviadoAtUtc);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.InvestorStatementSent));
        }

        [Fact]
        public async Task Enviar_UnBorrador_EsRechazado()
        {
            using var fixture = await EmailFixture.CreateAsync(finalizar: false);

            var resultado = await fixture.Emails.SendAsync(fixture.StatementId, "admin", default);

            Assert.Equal(InvestorStatementSendOutcome.Failed, resultado.Outcome);
            Assert.Empty(fixture.Sender.Sent);
        }

        [Fact]
        public async Task Enviar_UsaElSnapshotCongeladoAunqueCambienLosCobros()
        {
            using var fixture = await EmailFixture.CreateAsync();

            // El negocio registra más ingresos DESPUÉS de finalizar: el correo no debe reflejarlos.
            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, new DateTime(2026, 6, 15), 9_000_000m);

            await fixture.Emails.SendAsync(fixture.StatementId, "admin", default);

            var enviado = Assert.Single(fixture.Sender.Sent);
            Assert.Equal(450_000m, enviado.ParticipacionEnviada);
        }

        [Fact]
        public async Task Correo_NoIncluyeNombresDeClientesNiDeColaboradores()
        {
            using var fixture = await EmailFixture.CreateAsync();

            await fixture.Emails.SendAsync(fixture.StatementId, "admin", default);

            var enviado = Assert.Single(fixture.Sender.Sent);

            // "Cliente Test" es el nombre que usa la semilla de cobros; "Base", el del colaborador.
            Assert.DoesNotContain("Cliente Test", enviado.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Cliente Test", enviado.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Socio Northside", enviado.Html);
            Assert.Contains("Estado de participación", enviado.Subject);
        }

        [Fact]
        public async Task Prueba_SePuedeRepetirYNoMarcaElEstadoComoEnviado()
        {
            using var fixture = await EmailFixture.CreateAsync();

            await fixture.Emails.SendTestAsync(fixture.StatementId, "prueba@test.cr", "admin", default);
            await fixture.Emails.SendTestAsync(fixture.StatementId, "prueba@test.cr", "admin", default);

            Assert.Equal(2, fixture.Sender.Sent.Count);
            Assert.All(fixture.Sender.Sent, envio => Assert.StartsWith("[Prueba]", envio.Subject));

            var statement = await fixture.Context.InvestorStatements
                .SingleAsync(s => s.Id == fixture.StatementId);

            Assert.Equal(InvestorStatementStatus.Finalized, statement.Estado);
            Assert.Null(statement.EnviadoAtUtc);
        }

        [Fact]
        public async Task Enviar_CuandoElProveedorFalla_RegistraElErrorSaneado()
        {
            using var fixture = await EmailFixture.CreateAsync();
            fixture.Sender.ShouldSucceed = false;

            var resultado = await fixture.Emails.SendAsync(fixture.StatementId, "admin", default);

            Assert.Equal(InvestorStatementSendOutcome.Failed, resultado.Outcome);

            var log = await fixture.Context.InvestorStatementEmailLogs.SingleAsync();
            Assert.Equal(InvestorStatementEmailStatus.Failed, log.Status);
            Assert.Equal("Resend: fake_error", log.ErrorMessage);

            // Reintentos acotados: el servicio prueba más de una vez con la MISMA clave.
            Assert.Equal(2, fixture.Sender.Sent.Count);
            Assert.Equal(fixture.Sender.Sent[0].IdempotencyKey, fixture.Sender.Sent[1].IdempotencyKey);
        }

        [Fact]
        public async Task Pdf_SeGeneraDesdeElSnapshotYLlevaLaLeyendaInterna()
        {
            using var fixture = await EmailFixture.CreateAsync();

            var pdf = await fixture.Emails.BuildPdfAsync(fixture.StatementId, default);

            Assert.NotNull(pdf);
            Assert.True(pdf!.Value.Content.Length > 0);
            Assert.EndsWith(".pdf", pdf.Value.FileName);
            Assert.Contains(
                "no constituye un comprobante fiscal",
                InvestorStatementPdfService.LeyendaInterna,
                StringComparison.OrdinalIgnoreCase);
        }

        private sealed class EmailFixture : IDisposable
        {
            public required ProyectoIdentity.Datos.ApplicationDbContext Context { get; init; }

            public required Microsoft.Data.Sqlite.SqliteConnection Connection { get; init; }

            public required FakePlatformAuditService Audit { get; init; }

            public required FakeInvestorStatementEmailSender Sender { get; init; }

            public required InvestorStatementEmailService Emails { get; init; }

            public int StatementId { get; init; }

            public int FuncionarioId { get; init; }

            public static async Task<EmailFixture> CreateAsync(bool finalizar = true)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
                var audit = new FakePlatformAuditService();

                await InvestorTestSupport.SeedPolicyAsync(context);
                var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(context, "Base");

                await InvestorTestSupport.SeedCobroSinIvaAsync(
                    context, funcionario.IdFuncionario, new DateTime(2026, 6, 10), 1_000_000m);

                var investorId = await InvestorTestSupport.SeedInvestorAsync(
                    context, "Socio Northside", "partner@northside-studio.example", 45m, new DateOnly(2026, 1, 1));

                var statements = InvestorTestSupport.CreateStatementService(context, tenantProvider, audit);
                var statementId = await statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);

                if (finalizar)
                {
                    await statements.FinalizeAsync(statementId, "admin", default);
                }

                var sender = new FakeInvestorStatementEmailSender();

                var emails = new InvestorStatementEmailService(
                    context,
                    new InvestorStatementDocumentService(
                        context,
                        new TenantDisplayNameService(context, tenantProvider, new HttpContextAccessor())),
                    new InvestorStatementPdfService(),
                    new InvestorStatementEmailRenderer(),
                    sender,
                    statements,
                    audit,
                    NullLogger<InvestorStatementEmailService>.Instance);

                return new EmailFixture
                {
                    Context = context,
                    Connection = connection,
                    Audit = audit,
                    Sender = sender,
                    Emails = emails,
                    StatementId = statementId,
                    FuncionarioId = funcionario.IdFuncionario
                };
            }

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }
    }
}
