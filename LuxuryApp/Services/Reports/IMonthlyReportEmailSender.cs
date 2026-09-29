namespace LuxuryApp.Services.Reports
{
    public readonly record struct MonthlyReportEmailSendAttempt(
        bool Success,
        string? ProviderMessageId,
        string? Error);

    /// <summary>
    /// Envío del correo del resumen mensual vía Resend. No toca la base de datos: el
    /// orquestador (<see cref="IMonthlyBusinessReportService"/>) persiste el log resultante.
    /// Separado en interfaz propia para poder probar el orquestador sin proveedor real.
    /// </summary>
    public interface IMonthlyReportEmailSender
    {
        Task<MonthlyReportEmailSendAttempt> SendAsync(
            string recipientEmail,
            string subject,
            string htmlBody,
            string textBody,
            string idempotencyKey,
            Guid tenantId,
            CancellationToken cancellationToken = default);
    }
}
