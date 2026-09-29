using LuxuryApp.Models.Comprobantes;

namespace LuxuryApp.Services.Comprobantes
{
    /// <summary>
    /// Orquesta el ciclo de vida del comprobante interno: creación a partir de un cobro,
    /// envío por correo y reenvío. Toda consulta es tenant-safe (filtro global de EF). El
    /// envío es best-effort: si el correo falla, el comprobante queda <c>Failed</c> y se
    /// puede reintentar; el cobro nunca queda inconsistente.
    /// </summary>
    public interface IComprobanteCobroService
    {
        /// <summary>
        /// Crea (si no existe) el comprobante para el cobro y lo envía. Idempotente por CobroId:
        /// si ya existe un comprobante enviado, no crea otro. <paramref name="funcionarioScopeId"/>
        /// (portal): si viene, exige que el cobro pertenezca a ese funcionario.
        /// </summary>
        Task<ComprobanteCobro?> CrearYEnviarDesdeCobroAsync(
            int cobroId,
            string emailDestino,
            bool guardarEmailEnCliente,
            string? createdByUserId,
            int? funcionarioScopeId,
            CancellationToken cancellationToken = default);

        /// <summary>Reenvía un comprobante existente. Devuelve el comprobante actualizado o null si no aplica.</summary>
        Task<ComprobanteCobro?> ReenviarAsync(
            int comprobanteId,
            int? funcionarioScopeId,
            CancellationToken cancellationToken = default);

        /// <summary>Carga un comprobante del tenant actual (con líneas) para verlo/descargarlo en la app.</summary>
        Task<ComprobanteCobro?> ObtenerParaAppAsync(
            int comprobanteId,
            int? funcionarioScopeId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resuelve un comprobante por su token público SIN contexto de tenant (ruta pública).
        /// Ignora el filtro global de tenant; el token largo aleatorio es la única llave.
        /// </summary>
        Task<ComprobanteCobro?> ObtenerPorTokenPublicoAsync(
            string token,
            CancellationToken cancellationToken = default);

        /// <summary>Genera el PDF del comprobante (para adjuntar o descargar).</summary>
        byte[] GenerarPdf(ComprobanteCobro comprobante);
    }
}
