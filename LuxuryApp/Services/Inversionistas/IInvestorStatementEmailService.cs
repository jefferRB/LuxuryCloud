namespace LuxuryApp.Services.Inversionistas
{
    public enum InvestorStatementSendOutcome
    {
        Sent = 0,
        Skipped = 1,
        Failed = 2
    }

    public sealed record InvestorStatementSendResult(
        InvestorStatementSendOutcome Outcome,
        string Message)
    {
        public bool Success => Outcome == InvestorStatementSendOutcome.Sent;

        public static InvestorStatementSendResult Sent(string message) =>
            new(InvestorStatementSendOutcome.Sent, message);

        public static InvestorStatementSendResult Skipped(string message) =>
            new(InvestorStatementSendOutcome.Skipped, message);

        public static InvestorStatementSendResult Failed(string message) =>
            new(InvestorStatementSendOutcome.Failed, message);
    }

    /// <summary>
    /// Envío del estado de participación al inversionista. Siempre parte del snapshot congelado:
    /// un estado en borrador no se puede enviar.
    /// </summary>
    public interface IInvestorStatementEmailService
    {
        /// <summary>
        /// Envío real. Idempotente: si ya hay un envío exitoso para ese estado y destinatario,
        /// no se repite (hay que usar <see cref="ResendAsync"/> explícitamente).
        /// </summary>
        Task<InvestorStatementSendResult> SendAsync(
            int statementId,
            string? userId,
            CancellationToken cancellationToken = default);

        /// <summary>Reenvío manual explícito: abre una secuencia nueva y vuelve a enviar el mismo snapshot.</summary>
        Task<InvestorStatementSendResult> ResendAsync(
            int statementId,
            string? userId,
            CancellationToken cancellationToken = default);

        /// <summary>Prueba a un correo indicado por el administrador. Las pruebas sí pueden repetirse.</summary>
        Task<InvestorStatementSendResult> SendTestAsync(
            int statementId,
            string recipientEmail,
            string? userId,
            CancellationToken cancellationToken = default);

        /// <summary>PDF del estado para descargar desde la UI.</summary>
        Task<(byte[] Content, string FileName)?> BuildPdfAsync(
            int statementId,
            CancellationToken cancellationToken = default);
    }
}
