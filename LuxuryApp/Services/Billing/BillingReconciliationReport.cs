namespace LuxuryApp.Services.Billing
{
    /// <summary>Resultado de un pase de reconciliación. Se persiste serializado en PlatformAuditLog.</summary>
    public sealed record BillingReconciliationReport
    {
        public DateTime StartedUtc { get; init; }
        public DateTime FinishedUtc { get; set; }

        /// <summary>Pagos confirmados sin activación reparados automáticamente.</summary>
        public int OrphanPaymentsRepaired { get; set; }

        /// <summary>Pagos confirmados sin activación que NO era seguro reparar (alerta).</summary>
        public int OrphanPaymentsAlerted { get; set; }

        /// <summary>Suscripciones base activas con próximo cobro vencido (alerta, nunca se tocan).</summary>
        public int OverdueRenewalsAlerted { get; set; }

        /// <summary>Add-ons WhatsApp activos con próximo cobro vencido (alerta).</summary>
        public int OverdueAddonsAlerted { get; set; }

        /// <summary>Intentos Pendiente abandonados marcados Expirado (limpieza segura).</summary>
        public int StalePendingsExpired { get; set; }

        /// <summary>Intentos en ManualReview viejos sin resolver (alerta, nunca se tocan).</summary>
        public int StaleManualReviewsAlerted { get; set; }

        /// <summary>Eventos de pago atascados en Recibido/Error (alerta).</summary>
        public int StuckEventsAlerted { get; set; }

        /// <summary>Alertas omitidas por cooldown (ya alertadas recientemente).</summary>
        public int AlertsSuppressedByCooldown { get; set; }

        /// <summary>id_suscriptor faltante resuelto y persistido en este pase.</summary>
        public int SubscriberIdsResolved { get; set; }

        /// <summary>Casos donde el id_suscriptor sigue sin resolver (NotFound tras reintentos).</summary>
        public int SubscriberIdsPending { get; set; }

        /// <summary>Casos ambiguos (varios suscriptores por email): requieren revisión manual.</summary>
        public int SubscriberIdsAmbiguous { get; set; }

        /// <summary>id_suscriptor copiado localmente desde un pago confirmado (sin llamar al API).</summary>
        public int SubscriberIdsBackfilledLocally { get; set; }

        /// <summary>Intentos REALES contra TiloPay para cancelar el suscriptor viejo. Los skips NO cuentan aquí.</summary>
        public int OldSubscriberCancellationsRetried { get; set; }

        /// <summary>Suscriptores viejos cuya baja quedó VERIFICADA en este pase.</summary>
        public int OldSubscriberCancellationsCompleted { get; set; }

        /// <summary>Intents saltados porque la cancelación automática está apagada (no consumen presupuesto).</summary>
        public int OldCancellationSkippedAutoCancelDisabled { get; set; }

        /// <summary>Intents saltados por cooldown de backoff o por el tope diario por intent.</summary>
        public int OldCancellationSkippedBackoff { get; set; }

        /// <summary>Intents saltados por faltar datos para un intento verificable (revisión manual).</summary>
        public int OldCancellationSkippedNotEligible { get; set; }

        /// <summary>Cambios de plan con estado inconsistente reparados (subscriber viejo/IDs/fechas).</summary>
        public int PlanChangesRepaired { get; set; }

        /// <summary>Cambios PAGADOS que seguían sin aplicar por resolución tardía del suscriptor y se aplicaron en este pase.</summary>
        public int LatePlanChangesApplied { get; set; }

        /// <summary>Cambios pagados que NO se pudieron aplicar con seguridad (ambigüedad en el proveedor).</summary>
        public int LatePlanChangesManualReview { get; set; }

        /// <summary>Cambios pagados que siguen esperando (el destino aún no muestra suscriptor activo).</summary>
        public int LatePlanChangesLeftPending { get; set; }

        /// <summary>
        /// Checkouts de cambio de plan abandonados (sin dinero) expirados en este pase.
        /// A propósito NO cuenta para <see cref="HasFindings"/>: que un cliente se arrepienta de un
        /// cambio es higiene esperada, no un hallazgo que alguien deba revisar.
        /// </summary>
        public int PlanChangeCheckoutsExpired { get; set; }

        /// <summary>Suscripciones cuyo expire del proveedor se leyó y guardó en este pase.</summary>
        public int ProviderExpiriesSynced { get; set; }

        /// <summary>Suscripciones cuya vigencia local se EXTENDIÓ a la fecha real (posterior) del proveedor.</summary>
        public int ProviderExpiriesReconciled { get; set; }

        /// <summary>Suscripciones donde el expire del proveedor es ANTERIOR al local: solo alerta, no se acorta.</summary>
        public int ProviderExpiryEarlierAlerts { get; set; }

        /// <summary>Suscripciones con CancelAtPeriodEnd cuyo período pagado terminó y se cerraron a Cancelada localmente.</summary>
        public int CancelAtPeriodEndFinalized { get; set; }

        /// <summary>Desajustes local↔proveedor de ciclo de vida (cancelación/pausa) alertados en este pase.</summary>
        public int ProviderStatusMismatchesAlerted { get; set; }

        // ── Add-on de WhatsApp ──

        /// <summary>Intentos REALES contra TiloPay para cancelar un suscriptor de add-on pendiente.</summary>
        public int AddonCancellationsRetried { get; set; }

        /// <summary>Suscriptores de add-on cuya baja quedó VERIFICADA en este pase.</summary>
        public int AddonCancellationsCompleted { get; set; }

        /// <summary>Add-ons con cancelación de suscriptor pendiente que NO se pudo llamar (API apagado): alerta.</summary>
        public int AddonCancellationsPendingProviderDisabled { get; set; }

        /// <summary>Add-ons de WhatsApp ACTIVOS con el plan base cancelado/vencido (alerta, nunca se tocan).</summary>
        public int AddonsWithoutActiveBaseAlerted { get; set; }

        /// <summary>Tenants a los que se les auditó el estado real de add-ons en TiloPay en este pase.</summary>
        public int AddonProviderAudits { get; set; }

        /// <summary>CRÍTICO: tenants con más de un suscriptor de add-on cobrable en TiloPay (doble cobro).</summary>
        public int AddonProviderDoubleActiveDetected { get; set; }

        /// <summary>Auditorías que no pudieron concluir (API caído o status desconocido): no se declara sano.</summary>
        public int AddonProviderAuditsInconclusive { get; set; }

        /// <summary>
        /// Suscripciones base en gracia/morosa que se SANARON porque el proveedor está Active y
        /// renovado (el webhook success había quedado SinRelacion): incidente cerrado + reactivadas.
        /// </summary>
        public int RecoveredSubscriptionsHealed { get; set; }

        /// <summary>
        /// Eventos de pago recurrente exitoso que estaban SinRelacion y se reconciliaron contra la
        /// suscripción ya renovada por el proveedor (trazabilidad financiera): evento marcado
        /// ReconciliadoPorProveedor + PagoSuscripcion Confirmado registrado si no existía.
        /// </summary>
        public int RenewalSuccessEventsReconciled { get; set; }

        public double DurationMs => (FinishedUtc - StartedUtc).TotalMilliseconds;

        public bool HasFindings =>
            OrphanPaymentsRepaired > 0 ||
            OrphanPaymentsAlerted > 0 ||
            OverdueRenewalsAlerted > 0 ||
            OverdueAddonsAlerted > 0 ||
            StalePendingsExpired > 0 ||
            StaleManualReviewsAlerted > 0 ||
            StuckEventsAlerted > 0 ||
            SubscriberIdsResolved > 0 ||
            SubscriberIdsBackfilledLocally > 0 ||
            OldSubscriberCancellationsRetried > 0 ||
            // Un skip por AutoCancel apagado o por datos incompletos SÍ es un hallazgo: hay un
            // suscriptor viejo vivo que nadie está cancelando. El skip por backoff no lo es:
            // es el funcionamiento normal entre intentos.
            OldCancellationSkippedAutoCancelDisabled > 0 ||
            OldCancellationSkippedNotEligible > 0 ||
            PlanChangesRepaired > 0 ||
            LatePlanChangesApplied > 0 ||
            LatePlanChangesManualReview > 0 ||
            LatePlanChangesLeftPending > 0 ||
            // Extender vigencia a la fecha real del proveedor no es un problema (es lo correcto),
            // pero un expire ANTERIOR sí amerita revisión: puede ser un corte que evitamos a tiempo.
            ProviderExpiryEarlierAlerts > 0 ||
            // Un drift local↔proveedor de ciclo de vida (p.ej. CancelAtPeriodEnd pero sigue Activo)
            // es un hallazgo de dinero. La finalización de período (CancelAtPeriodEndFinalized) NO lo
            // es: es higiene esperada, el cierre normal de un período pagado.
            ProviderStatusMismatchesAlerted > 0 ||
            // Un suscriptor de add-on que nadie está cancelando (API apagado) es dinero en riesgo;
            // un add-on activo sin plan base es un estado que debe resolverse. Ambos son hallazgos.
            AddonCancellationsPendingProviderDisabled > 0 ||
            AddonsWithoutActiveBaseAlerted > 0 ||
            // Sanar una recuperación falsa es un hallazgo: hubo un webhook success que quedó
            // SinRelacion y dejó la suscripción en gracia siendo que el proveedor ya había renovado.
            RecoveredSubscriptionsHealed > 0 ||
            // Reconciliar un success huérfano también es un hallazgo: hubo un cobro real que quedó
            // sin ligar a su suscripción y ahora se cerró la traza financiera.
            RenewalSuccessEventsReconciled > 0;
    }
}
