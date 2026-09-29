namespace LuxuryApp.Models.SaaS
{
    public class ResultadoCheckoutViewModel
    {
        public string? Referencia { get; set; }
        public string? CodigoProveedor { get; set; }
        public string? DescripcionProveedor { get; set; }
        public string? NombrePlan { get; set; }
        public EstadoPagoProveedor? EstadoPago { get; set; }
        public EstadoSuscripcion? EstadoSuscripcion { get; set; }
        public DateTime? VigenciaHastaUtc { get; set; }
        public DateTime? ProximoCobroUtc { get; set; }
        public int? MaxFuncionarios { get; set; }

        // ── Scope-aware: retorno de un ADD-ON de WhatsApp (no del plan base) ──
        /// <summary>True si este retorno corresponde a la compra/renovación de un add-on de WhatsApp.</summary>
        public bool EsAddon { get; set; }

        /// <summary>Límite MENSUAL de mensajes del add-on (para el comprobante del paquete).</summary>
        public int? MensajesMensuales { get; set; }

        /// <summary>El plan BASE necesita atención (impago/gracia): se muestra en una sección aparte, no mezclado con el add-on.</summary>
        public bool BaseRequiereAtencion { get; set; }

        public string? BaseAtencionMensaje { get; set; }

        /// <summary>
        /// El pago quedó en REVISIÓN MANUAL en el proveedor/local: no se aplicó ningún cambio. Nunca
        /// se muestra como éxito (regresión: el retorno decía "Pago confirmado y suscripción
        /// activa" mostrando el plan BASE mientras el intento de add-on estaba en MANUAL_REVIEW).
        /// </summary>
        public bool EnRevisionManual { get; set; }

        /// <summary>
        /// No se pudo ligar este retorno a ningún intento de pago local. Es un ERROR explícito: sin
        /// pago correlacionado no se puede afirmar nada sobre el resultado, y mucho menos mostrar el
        /// estado del plan base como si fuera la confirmación de lo que el cliente acaba de comprar.
        /// </summary>
        public bool CorrelacionFallida { get; set; }

        public bool AccesoRestringido { get; set; }
        public bool PagoAprobadoPorProveedor { get; set; }
        public bool ConfirmadoPorWebhook { get; set; }
        public bool SuscripcionActiva { get; set; }
        public bool DebeAutoActualizar { get; set; }
        public int SegundosAutoActualizacion { get; set; }
        public string? UrlActualizacion { get; set; }
        public string? PrimaryActionLabel { get; set; }
        public string? PrimaryActionUrl { get; set; }
        public string? SecondaryActionLabel { get; set; }
        public string? SecondaryActionUrl { get; set; }
        public string MensajePrincipal { get; set; } = string.Empty;
        public string? MensajeSecundario { get; set; }
    }
}
