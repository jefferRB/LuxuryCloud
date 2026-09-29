using LuxuryApp.Models.Comprobantes;

namespace LuxuryApp.Services.Comprobantes
{
    /// <summary>
    /// Renderiza el cuerpo del correo del comprobante (HTML responsive con CSS inline y
    /// versión de texto plano). Todo el texto dinámico se codifica para evitar XSS.
    /// </summary>
    public interface IComprobanteHtmlRenderer
    {
        string RenderEmailHtml(ComprobanteCobro comprobante, string? urlPublica);
        string RenderEmailText(ComprobanteCobro comprobante, string? urlPublica);
    }
}
