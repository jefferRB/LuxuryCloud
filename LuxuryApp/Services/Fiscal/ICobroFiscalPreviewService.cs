namespace LuxuryApp.Services.Fiscal
{
    /// <summary>
    /// Desglose fiscal (informativo) para el modal de cobro. El cálculo autoritativo se hace al
    /// registrar el cobro/emitir el comprobante; este servicio solo previsualiza usando el mismo
    /// motor fiscal central, para que NO exista lógica de IVA duplicada en el frontend.
    /// </summary>
    public interface ICobroFiscalPreviewService
    {
        /// <summary>
        /// Previsualiza el desglose de un monto para una cita concreta (resuelve la config fiscal
        /// del servicio de la cita, o del tenant si es personalizado). Devuelve null si la cita no
        /// existe o no pertenece al tenant actual.
        /// </summary>
        Task<CobroFiscalPreview?> PreviewCitaAsync(int citaId, decimal monto, CancellationToken cancellationToken = default);
    }

    public sealed record CobroFiscalPreview
    {
        public decimal Total { get; init; }
        public decimal BaseSinIva { get; init; }
        public decimal IvaIncluido { get; init; }
        public decimal TarifaIva { get; init; }
        public bool PrecioIncluyeIva { get; init; }
        public bool AplicaIva { get; init; }
        /// <summary>"Servicio" o "Producto" (una cita siempre cobra un servicio).</summary>
        public string TipoLinea { get; init; } = "Servicio";
    }
}
