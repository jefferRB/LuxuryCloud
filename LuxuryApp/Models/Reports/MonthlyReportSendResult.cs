namespace LuxuryApp.Models.Reports
{
    public enum MonthlyReportSendOutcome
    {
        /// <summary>Todos los destinatarios recibieron el correo.</summary>
        Sent,

        /// <summary>Al menos un destinatario recibió el correo, pero hubo fallos u omitidos.</summary>
        PartiallySent,

        /// <summary>No se envió nada: ya estaba enviado (idempotencia) o la configuración no lo permite.</summary>
        Skipped,

        /// <summary>No se pudo enviar a ningún destinatario.</summary>
        Failed
    }

    /// <summary>Resultado de un envío (real o de prueba) del resumen mensual.</summary>
    public sealed record MonthlyReportSendResult(
        MonthlyReportSendOutcome Outcome,
        string Message,
        int SentCount = 0,
        int SkippedCount = 0,
        int FailedCount = 0)
    {
        public static MonthlyReportSendResult Skipped(string message) =>
            new(MonthlyReportSendOutcome.Skipped, message, SkippedCount: 1);

        public static MonthlyReportSendResult Failed(string message) =>
            new(MonthlyReportSendOutcome.Failed, message, FailedCount: 1);
    }
}
