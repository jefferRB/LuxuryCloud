namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Configuración del pase diario de reconciliación de Billing. Los defaults son seguros
    /// para producción: detección y alertas siempre, reparación automática solo para casos
    /// determinísticos (pago confirmado sin activación), nunca sobre datos ambiguos.
    /// </summary>
    public sealed class BillingReconciliationOptions
    {
        /// <summary>Apaga por completo el pase (el worker queda registrado pero inerte).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Permite la reparación automática de pagos confirmados sin activación.
        /// En false, esos casos generan alerta en vez de repararse.
        /// </summary>
        public bool AutoRepairEnabled { get; set; } = true;

        /// <summary>Frecuencia del pase. 24 = diario.</summary>
        public int IntervalHours { get; set; } = 24;

        /// <summary>Espera inicial tras el arranque de la app antes del primer pase.</summary>
        public int InitialDelayMinutes { get; set; } = 3;

        /// <summary>Ventana hacia atrás para buscar pagos confirmados sin activación.</summary>
        public int ConfirmedPaymentLookbackDays { get; set; } = 14;

        /// <summary>
        /// Tolerancia sobre FechaProximoCobroUtc antes de alertar renovación vencida
        /// (TiloPay puede cobrar con horas de diferencia; 36h evita falsos positivos).
        /// </summary>
        public int OverdueRenewalToleranceHours { get; set; } = 36;

        /// <summary>Días tras los cuales un intento Pendiente sin webhook se considera abandonado.</summary>
        public int StalePendingDays { get; set; } = 7;

        /// <summary>
        /// Horas tras las cuales un checkout de CAMBIO DE PLAN sin pagar se da por abandonado y se
        /// expira junto con su intento. Mucho más corto que <see cref="StalePendingDays"/> a
        /// propósito: un cambio de plan bloquea el cupo de "un Pending por tenant", así que dejarlo
        /// 7 días le impide al cliente reintentar. No hay riesgo: solo expira intentos SIN ninguna
        /// señal de dinero (ver <see cref="PlanChangeCheckoutAbandonmentRules"/>).
        /// </summary>
        public int PlanChangePendingCheckoutExpirationHours { get; set; } = 24;

        /// <summary>Horas tras las cuales un ManualReview sin resolver genera alerta (nunca se toca).</summary>
        public int StaleManualReviewHours { get; set; } = 24;

        /// <summary>Minutos tras los cuales un EventoPago Recibido/Error se considera atascado.</summary>
        public int StuckEventMinutes { get; set; } = 60;

        /// <summary>Horas de silencio antes de repetir la MISMA alerta sobre la misma entidad.</summary>
        public int AlertCooldownHours { get; set; } = 20;

        /// <summary>
        /// Habilita el worker de ALTA frecuencia que reintenta la cancelación del suscriptor viejo
        /// tras un cambio de plan. El riesgo de doble cobro no debe esperar al pase diario.
        /// </summary>
        public bool OldCancellationRetryEnabled { get; set; } = true;

        /// <summary>Intervalo (minutos) del worker de reintento de cancelación vieja. Clamp [5, 720].</summary>
        public int OldCancellationRetryMinutes { get; set; } = 20;

        /// <summary>
        /// Habilita el worker LIVIANO de ciclo de vida que cierra localmente las cancelaciones
        /// vencidas (CancelAtPeriodEnd cuyo período ya terminó). Corre al arranque y luego cada
        /// <see cref="LifecycleFinalizationMinutes"/>: no se depende del pase diario de 24 h para
        /// que la BD deje de figurar Activa tras el vencimiento. Solo local (cero HTTP a TiloPay).
        /// </summary>
        public bool LifecycleFinalizationWorkerEnabled { get; set; } = true;

        /// <summary>Intervalo (minutos) del worker de finalización de ciclo de vida. Clamp [5, 120].</summary>
        public int LifecycleFinalizationMinutes { get; set; } = 30;

        /// <summary>Espera inicial (minutos) del worker de finalización antes del primer pase. Clamp [0, 10].</summary>
        public int LifecycleFinalizationInitialDelayMinutes { get; set; } = 1;

        /// <summary>
        /// Diferencia mínima (horas) entre el expire del proveedor y la fecha local para actuar.
        /// Por debajo de esto se considera la MISMA fecha (TiloPay cobra con horas de diferencia) y
        /// solo se guarda el dato del proveedor, sin extender ni alertar. 12h absorbe el ruido normal
        /// sin dejar pasar una diferencia real (las observadas tras un cambio de plan son de ~30 días).
        /// </summary>
        public int ProviderExpiryReconcileMinDifferenceHours { get; set; } = 12;

        /// <summary>
        /// Tope de intentos REALES contra TiloPay por PlanChangeIntent y por ventana de 24h.
        /// Es un cinturón de seguridad contra un loop de llamadas, NO el regulador principal: el
        /// backoff (<see cref="PlanChangeCancellationBackoff"/>) ya espacia los intentos y en la
        /// práctica nunca llega a este tope. Se cuenta POR INTENT y solo desde el último reinicio
        /// de presupuesto, así que una reparación siempre habilita un intento inmediato.
        /// </summary>
        public int OldCancellationRetryMaxAttemptsPerIntentPerDay { get; set; } = 12;

        /// <summary>
        /// Habilita el cierre LOCAL (sin HTTP) de las suscripciones con CancelAtPeriodEnd cuando su
        /// período pagado termina. Seguro por defecto: solo pasa a Cancelada tras la fecha efectiva.
        /// </summary>
        public bool CancelAtPeriodEndFinalizationEnabled { get; set; } = true;

        /// <summary>
        /// Habilita la detección de drift entre el estado local y el del proveedor (cancelación/pausa)
        /// vía getSuscriptorRepeat en el pase diario. Solo alerta, nunca suspende/cancela por su cuenta.
        /// </summary>
        public bool LifecycleProviderStatusSyncEnabled { get; set; } = true;

        /// <summary>
        /// Tope de tenants a los que se les audita el estado REAL de add-ons en TiloPay por pase.
        /// La auditoría cuesta una llamada getSuscriptorRepeat por cada plan de add-on configurado,
        /// así que el tope acota la cuota del API admin. 0 apaga la fase (nunca recomendado en
        /// producción: es lo único que detecta el doble suscriptor del proveedor). Clamp [0, 200].
        /// </summary>
        public int MaxAddonProviderAuditsPerRun { get; set; } = 50;
    }
}
