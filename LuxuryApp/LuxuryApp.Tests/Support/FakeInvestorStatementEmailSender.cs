using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Inversionistas;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Transporte de correo en memoria: registra cada intento sin salir a Resend. Permite afirmar
    /// idempotencia (cuántas veces se intentó enviar de verdad) y contenido del snapshot.
    /// </summary>
    internal sealed class FakeInvestorStatementEmailSender : IInvestorStatementEmailSender
    {
        public List<SentEmail> Sent { get; } = new();

        /// <summary>Si es false, el proveedor "rechaza" el correo (para probar el camino de error).</summary>
        public bool ShouldSucceed { get; set; } = true;

        public Task<InvestorStatementEmailSendAttempt> SendAsync(
            InvestorStatementDocument document,
            string recipientEmail,
            string subject,
            string htmlBody,
            string textBody,
            byte[]? pdf,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentEmail(
                document.StatementId,
                recipientEmail,
                subject,
                htmlBody,
                textBody,
                pdf?.Length ?? 0,
                idempotencyKey,
                document.ParticipacionCalculada));

            return Task.FromResult(ShouldSucceed
                ? new InvestorStatementEmailSendAttempt(true, $"fake-{Sent.Count}", null)
                : new InvestorStatementEmailSendAttempt(false, null, "Resend: fake_error"));
        }

        internal sealed record SentEmail(
            int StatementId,
            string Recipient,
            string Subject,
            string Html,
            string Text,
            int PdfBytes,
            string IdempotencyKey,
            decimal ParticipacionEnviada);
    }
}
