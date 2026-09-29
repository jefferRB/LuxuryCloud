using LuxuryApp.Services.Reports;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Sender de resumen mensual que no toca Resend. Registra cada llamada y permite
    /// simular fallos del proveedor para probar el logging de errores.
    /// </summary>
    internal sealed class FakeMonthlyReportEmailSender : IMonthlyReportEmailSender
    {
        public sealed record SentEmail(
            string Recipient,
            string Subject,
            string HtmlBody,
            string TextBody,
            string IdempotencyKey,
            Guid TenantId);

        public bool Succeed { get; set; } = true;

        public string FailureError { get; set; } = "Fallo simulado del proveedor.";

        public List<SentEmail> Attempts { get; } = new();

        public Task<MonthlyReportEmailSendAttempt> SendAsync(
            string recipientEmail,
            string subject,
            string htmlBody,
            string textBody,
            string idempotencyKey,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(new SentEmail(recipientEmail, subject, htmlBody, textBody, idempotencyKey, tenantId));

            return Task.FromResult(Succeed
                ? new MonthlyReportEmailSendAttempt(true, $"resend-fake-{Attempts.Count}", null)
                : new MonthlyReportEmailSendAttempt(false, null, FailureError));
        }
    }
}
