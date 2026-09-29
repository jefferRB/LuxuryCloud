using LuxuryApp.Models.Reports;

namespace LuxuryApp.Services.Reports
{
    /// <summary>
    /// Genera y envía el Resumen Ejecutivo Mensual del negocio reutilizando los servicios
    /// del Dashboard Financiero e Información. Todo envío queda registrado en
    /// <see cref="TenantMonthlyReportEmailLog"/>.
    /// <para>
    /// Fase 1: solo envíos manuales (prueba y envío real explícito). El envío real es
    /// idempotente por tenant/año/mes/correo. Fase 2 podrá invocar
    /// <see cref="SendMonthlyReportAsync"/> desde un scheduler sin cambios de contrato.
    /// </para>
    /// </summary>
    public interface IMonthlyBusinessReportService
    {
        /// <summary>
        /// Construye el reporte del mes indicado. <paramref name="tenantId"/> debe coincidir
        /// con el tenant del contexto actual (guard anti cross-tenant). Nunca explota por
        /// meses sin actividad: devuelve un reporte con <c>TieneActividad = false</c>.
        /// </summary>
        Task<MonthlyBusinessReportViewModel> GenerateAsync(
            Guid tenantId,
            int year,
            int month,
            CancellationToken cancellationToken = default);

        /// <summary>Renderiza el HTML del correo (estilos inline, compatible Gmail/Outlook/móvil).</summary>
        Task<string> RenderEmailHtmlAsync(
            MonthlyBusinessReportViewModel report,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Envía una PRUEBA al correo indicado (o del usuario que la dispara). Las pruebas
        /// pueden repetirse; cada intento queda registrado con IsTest = true.
        /// </summary>
        Task<MonthlyReportSendResult> SendTestAsync(
            Guid tenantId,
            int year,
            int month,
            string recipientEmail,
            string triggeredByUserId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Envío REAL a los administradores/dueños del tenant (y correos adicionales
        /// configurados). Requiere configuración activa (IsEnabled). Idempotente: un
        /// correo ya enviado para el mismo tenant/año/mes se omite con Skipped.
        /// </summary>
        Task<MonthlyReportSendResult> SendMonthlyReportAsync(
            Guid tenantId,
            int year,
            int month,
            string triggeredByUserId,
            CancellationToken cancellationToken = default);
    }
}
