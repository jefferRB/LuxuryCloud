using LuxuryApp.Models.Comprobantes;

namespace LuxuryApp.Services.Comprobantes
{
    /// <summary>
    /// Genera la representación PDF (tipo factura) de un comprobante interno.
    /// </summary>
    public interface IComprobantePdfService
    {
        /// <summary>
        /// Construye el PDF a partir del comprobante y sus líneas (que deben venir cargadas).
        /// </summary>
        byte[] Generar(ComprobanteCobro comprobante);
    }
}
