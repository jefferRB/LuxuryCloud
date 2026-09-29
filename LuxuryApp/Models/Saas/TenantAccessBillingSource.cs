namespace LuxuryApp.Models.SaaS
{
    /// <summary>
    /// Quien paga el acceso BASE del tenant. Deliberadamente separado de
    /// <see cref="WhatsAppAddonBillingSource"/>: el plan base y el add-on de WhatsApp pueden
    /// tener origenes distintos (un tenant puede pagar ambos por TiloPay y otro tener base exento y
    /// add-on ManualGrant).
    ///
    /// Solo <see cref="ProviderRecurring"/> representa dinero recurrente del proveedor. Un
    /// acceso manual/exento NUNCA debe contarse como riesgo de dinero ni disparar llamadas
    /// a TiloPay.
    /// </summary>
    public enum TenantAccessBillingSource
    {
        /// <summary>Sin acceso comercial o sin fuente identificable.</summary>
        None = 0,

        /// <summary>Suscripcion pagada al proveedor (TiloPay), con o sin recurrencia activa.</summary>
        ProviderRecurring = 1,

        /// <summary>Acceso otorgado por plataforma: exento, interno, cortesia o canje. No se cobra.</summary>
        Manual = 2,

        /// <summary>Acceso temporal por codigo promocional / trial.</summary>
        PromotionalGrant = 3,

        /// <summary>Suscripcion historica sin datos de proveedor recurrente.</summary>
        Legacy = 4
    }
}
