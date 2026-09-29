namespace LuxuryApp.Models.Comprobantes
{
    /// <summary>
    /// Estado del envío del comprobante por correo. Se persiste como string
    /// para legibilidad en BD y estabilidad ante reordenamientos del enum.
    /// </summary>
    public enum ComprobanteEstadoEnvio
    {
        /// <summary>Creado pero aún no enviado (o pendiente de reintento).</summary>
        Pending,

        /// <summary>Enviado correctamente a través de Resend.</summary>
        Sent,

        /// <summary>El envío falló; ver <c>ErrorEnvio</c>. Permite reintento.</summary>
        Failed,

        /// <summary>Cancelado / anulado manualmente. No se reintenta.</summary>
        Cancelled
    }

    /// <summary>
    /// Tipo de línea del comprobante. Strings centralizados para no repetir literales.
    /// </summary>
    public static class ComprobanteTipoLinea
    {
        public const string Servicio = "Servicio";
        public const string Producto = "Producto";
        public const string Otro = "Otro";
    }

    /// <summary>
    /// Tipo de comprobante. En esta fase SIEMPRE es interno (no fiscal).
    /// Queda como constante para preparar futuros tipos (p. ej. fiscal/Hacienda).
    /// </summary>
    public static class ComprobanteTipo
    {
        public const string ComprobanteInterno = "ComprobanteInterno";
    }
}
