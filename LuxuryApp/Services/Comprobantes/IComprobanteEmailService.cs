using LuxuryApp.Models.Comprobantes;

namespace LuxuryApp.Services.Comprobantes
{
    public readonly record struct ComprobanteEnvioResult(bool Success, string? ResendEmailId, string? Error);

    /// <summary>
    /// Envía el comprobante por correo usando Resend (con PDF adjunto). No toca la base de datos:
    /// el orquestador (<see cref="IComprobanteCobroService"/>) persiste el estado resultante.
    /// </summary>
    public interface IComprobanteEmailService
    {
        Task<ComprobanteEnvioResult> EnviarComprobanteCobroAsync(
            ComprobanteCobro comprobante,
            byte[] pdf,
            string? urlPublica,
            CancellationToken cancellationToken = default);
    }
}
