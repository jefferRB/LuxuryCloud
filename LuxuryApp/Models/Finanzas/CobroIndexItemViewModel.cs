namespace LuxuryApp.Models.Finanzas
{
    public sealed class CobroIndexItemViewModel
    {
        public int IdCobro { get; init; }
        public DateTime FechaCobro { get; init; }
        public string NombreCliente { get; init; } = string.Empty;
        public string FuncionarioNombre { get; init; } = string.Empty;
        public string Detalle { get; init; } = string.Empty;
        public decimal Monto { get; init; }
        public string MetodoPago { get; init; } = string.Empty;
        public bool EsServicio { get; init; }
        public bool EsProducto => !EsServicio;

        // Estado del comprobante asociado (null = sin comprobante).
        public int? ComprobanteId { get; init; }
        public Comprobantes.ComprobanteEstadoEnvio? ComprobanteEstado { get; init; }
        public string? ComprobanteToken { get; init; }
        public string? ComprobanteNumero { get; init; }
        public string? ComprobanteEmail { get; init; }
        public System.DateTime? ComprobanteSentAt { get; init; }

        public bool TieneComprobante => ComprobanteId.HasValue;
        public bool ComprobanteEnviado => ComprobanteEstado == Comprobantes.ComprobanteEstadoEnvio.Sent;
        public bool ComprobantePendiente =>
            ComprobanteEstado == Comprobantes.ComprobanteEstadoEnvio.Pending ||
            ComprobanteEstado == Comprobantes.ComprobanteEstadoEnvio.Failed;
    }
}
