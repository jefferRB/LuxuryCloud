using LuxuryApp.Models.Reports;

namespace LuxuryApp.Services.Reports
{
    /// <summary>
    /// Plantilla del correo del Resumen Ejecutivo Mensual. HTML de tabla 600px con CSS
    /// inline (sin CSS externo ni JavaScript) + versión en texto plano.
    /// </summary>
    public interface IMonthlyReportEmailRenderer
    {
        /// <param name="dashboardUrl">URL absoluta al dashboard; null/vacío oculta el botón.</param>
        string RenderHtml(MonthlyBusinessReportViewModel report, string? dashboardUrl);

        string RenderText(MonthlyBusinessReportViewModel report, string? dashboardUrl);
    }
}
