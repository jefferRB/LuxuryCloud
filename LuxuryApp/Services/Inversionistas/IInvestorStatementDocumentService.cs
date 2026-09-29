using LuxuryApp.Models.Inversionistas;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Construye el documento del estado de cuenta (correo y PDF) a partir del snapshot congelado.
    /// Es el único lugar que decide qué datos del negocio viajan al inversionista.
    /// </summary>
    public interface IInvestorStatementDocumentService
    {
        Task<InvestorStatementDocument?> BuildAsync(int statementId, CancellationToken cancellationToken = default);
    }

    /// <summary>Genera el PDF del estado de cuenta reutilizando la infraestructura QuestPDF existente.</summary>
    public interface IInvestorStatementPdfService
    {
        byte[] Generar(InvestorStatementDocument document);
    }

    /// <summary>Plantilla HTML/texto del correo. Sin estado → singleton y testeable.</summary>
    public interface IInvestorStatementEmailRenderer
    {
        string RenderHtml(InvestorStatementDocument document);

        string RenderText(InvestorStatementDocument document);
    }

    public sealed record InvestorStatementEmailSendAttempt(bool Success, string? ProviderMessageId, string? Error);

    /// <summary>Transporte del correo (Resend), con clave de idempotencia y PDF adjunto.</summary>
    public interface IInvestorStatementEmailSender
    {
        Task<InvestorStatementEmailSendAttempt> SendAsync(
            InvestorStatementDocument document,
            string recipientEmail,
            string subject,
            string htmlBody,
            string textBody,
            byte[]? pdf,
            string idempotencyKey,
            CancellationToken cancellationToken = default);
    }
}
