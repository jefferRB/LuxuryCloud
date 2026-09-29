using LuxuryApp.Models.Funcionarios;

namespace LuxuryApp.Services.Funcionarios
{
    public interface ILiquidacionSemanalService
    {
        Task<PagosSemanaResumen> ObtenerResumenSemanaAsync(DateTime fechaReferencia, CancellationToken cancellationToken = default);
        Task<PagosSemanaResumen> ObtenerResumenSemanaAsync(DateTime inicioSemana, DateTime finSemana, CancellationToken cancellationToken = default);
        Task<int> RegistrarPagoAsync(RegistrarLiquidacionSemanalCommand command, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deshace por completo una operación de pago: liquidación, detalles, distribución mensual
        /// y el egreso asociado desaparecen en UNA transacción, dejando antes constancia en la
        /// bitácora (quién, cuándo, cuánto, a quién, por qué).
        ///
        /// <para>
        /// <b>Un pago de liquidación no se edita: se revierte y se vuelve a registrar.</b> Editar el
        /// egreso por separado dejaría <c>Liquidacion.MontoTotal != Egreso.Monto</c>.
        /// </para>
        ///
        /// <para>
        /// Solo se deshace el ACTO DE PAGO. La producción del periodo (servicios, productos, IVA y
        /// comisión devengada) no cambia: el dinero simplemente vuelve a quedar pendiente.
        /// </para>
        ///
        /// <para>
        /// Revierte el LOTE completo. Si el pago incluyó a varios colaboradores comparten un único
        /// egreso por el total, así que revertir solo una parte rompería el cuadre.
        /// </para>
        ///
        /// <para>Idempotente: un segundo intento responde <see cref="ReversionPagoEstado.YaRevertida"/>.</para>
        /// </summary>
        Task<ReversionPagoResultado> RevertirPagoAsync(
            int liquidacionId,
            string motivo,
            string? revertidoPorUserId,
            CancellationToken cancellationToken = default);

        /// <summary>Diagnóstico de atribución de pagos al rango (incluidos/excluidos y por qué).</summary>
        Task<IReadOnlyList<PagoAtribucionDiagnostico>> ObtenerDiagnosticoPagosAsync(
            DateTime inicioSemana, DateTime finSemana, CancellationToken cancellationToken = default);
    }
}
