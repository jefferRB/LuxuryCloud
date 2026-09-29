namespace LuxuryApp.Models.SaaS
{
    /// <summary>
    /// Configuración del cliente admin de TiloPay Repeat (getSuscriptorRepeat, recurrentUrl,
    /// pause/reactive/delete/editSuscriptorRepeat). Deshabilitado por defecto: mientras
    /// <see cref="Enabled"/> sea false, LuxuryCloud NO llama a estos endpoints y el flujo de
    /// compra recurrente se comporta exactamente como hoy. Se activa solo tras validar en sandbox.
    /// </summary>
    public sealed class OpcionesTilopayRepeatAdmin
    {
        public bool Enabled { get; set; }

        /// <summary>
        /// Habilita el blindaje anti-duplicado en el checkout (consulta suscriptor existente
        /// antes de crear un hosted link nuevo). Requiere <see cref="Enabled"/>.
        /// </summary>
        public bool BlockDuplicateCheckout { get; set; } = true;

        /// <summary>
        /// Habilita la cancelación automática del suscriptor viejo del proveedor tras un upgrade.
        /// Requiere <see cref="Enabled"/>. En false, el upgrade solo alerta (comportamiento previo).
        /// </summary>
        public bool AutoCancelOldSubscriberOnUpgrade { get; set; } = true;

        /// <summary>Reintentos ante consistencia eventual del proveedor al resolver suscriptor.</summary>
        public int ResolveRetryCount { get; set; } = 2;

        /// <summary>Espera base entre reintentos de resolución (ms).</summary>
        public int ResolveRetryBaseDelayMs { get; set; } = 800;

        /// <summary>
        /// Máximo de intentos de resolución por reconciliación antes de marcar el caso como
        /// "pendiente persistente" para revisión manual en BillingHealth.
        /// </summary>
        public int MaxReconciliationResolveAttempts { get; set; } = 6;

        /// <summary>Timeout dedicado para las llamadas admin (segundos).</summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Campo de la respuesta de recurrentUrl que se usa para actualizar el método de pago:
        /// "url_renew" (default, renueva el suscriptor existente) o "url_register" (modo controlado
        /// para diagnóstico: TiloPay registra un flujo nuevo). Cualquier otro valor cae a "url_renew".
        /// </summary>
        public string RecurrentUrlPreferredField { get; set; } = "url_renew";
    }
}
