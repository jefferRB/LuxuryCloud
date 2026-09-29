namespace LuxuryApp.Models.Platform
{
    /// <summary>
    /// Registro append-only de acciones internas del SuperAdmin sobre la plataforma.
    /// No implementa <c>ITenantEntity</c> a propósito: es una bitácora cross-tenant que
    /// queda fuera del Row-Level Security. Nunca debe exponerse un endpoint de borrado.
    /// </summary>
    public class PlatformAuditLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>UserId del SuperAdmin que ejecutó la acción.</summary>
        public string ActorUserId { get; set; } = string.Empty;

        /// <summary>Snapshot del correo/usuario del actor al momento de la acción.</summary>
        public string ActorEmail { get; set; } = string.Empty;

        /// <summary>Acción ejecutada. Ver <see cref="PlatformAuditActions"/>.</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>Tipo de entidad afectada. Ver <see cref="PlatformAuditEntityTypes"/>.</summary>
        public string EntityType { get; set; } = string.Empty;

        /// <summary>Id de la entidad afectada (string para soportar Guid o Id de Identity).</summary>
        public string? EntityId { get; set; }

        public Guid? TenantId { get; set; }

        /// <summary>Snapshot del nombre del tenant al momento de la acción.</summary>
        public string? TenantName { get; set; }

        public string? TargetUserId { get; set; }

        /// <summary>Snapshot del correo del usuario objetivo.</summary>
        public string? TargetUserEmail { get; set; }

        public string? BeforeJson { get; set; }

        public string? AfterJson { get; set; }

        public string? Reason { get; set; }

        public string? IpAddress { get; set; }

        public string? UserAgent { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Acciones auditables de la consola de plataforma.</summary>
    public static class PlatformAuditActions
    {
        /// <summary>Reversión de un pago de liquidación de colaboradores (movimiento de dinero).</summary>
        public const string EmployeeSettlementPaymentReverted = "EmployeeSettlementPaymentReverted";

        public const string UserDeactivated = "UserDeactivated";
        public const string UserReactivated = "UserReactivated";
        public const string DangerousActionPasswordFailed = "DangerousActionPasswordFailed";
        public const string DangerousActionBlocked = "DangerousActionBlocked";
        public const string TenantCommercialAccessUpdated = "TenantCommercialAccessUpdated";
        public const string TenantPendingRegistrationExpired = "TenantPendingRegistrationExpired";
        public const string WhatsAppSettingsUpdated = "WhatsAppSettingsUpdated";
        public const string MetaDiagnosticExecuted = "MetaDiagnosticExecuted";

        /// <summary>MFA TOTP habilitado/deshabilitado/recuperado para una cuenta de plataforma.</summary>
        public const string MfaEnabled = "MfaEnabled";
        public const string MfaDisabled = "MfaDisabled";
        public const string MfaRecoveryCodeUsed = "MfaRecoveryCodeUsed";

        /// <summary>Activación/desactivación de un código promocional (S11).</summary>
        public const string PromotionalCodeToggled = "PromotionalCodeToggled";

        /// <summary>Captura manual del snapshot comercial mensual (AD-4).</summary>
        public const string CommercialSnapshotCaptured = "CommercialSnapshotCaptured";

        /// <summary>
        /// Alerta automática: un upgrade de plan se aplicó y quedó una suscripción recurrente
        /// anterior viva en el proveedor que debe cancelarse manualmente (TiloPay no tiene API).
        /// </summary>
        public const string PlanUpgradeRequiresProviderCancellation = "PlanUpgradeRequiresProviderCancellation";

        /// <summary>
        /// Alerta automática: un webhook de pago quedó en revisión manual o sin correlación.
        /// Puede haber dinero cobrado en el proveedor sin activar; revisar en
        /// Platform/RecurringCheckouts (conciliación interna).
        /// </summary>
        public const string PaymentWebhookRequiresManualReview = "PaymentWebhookRequiresManualReview";

        /// <summary>Cierre de un pase de reconciliación de Billing (resumen en AfterJson).</summary>
        public const string BillingReconciliationCompleted = "BillingReconciliationCompleted";

        /// <summary>Reparación automática segura aplicada por la reconciliación (ej. pago confirmado sin activación).</summary>
        public const string BillingAutoRepairApplied = "BillingAutoRepairApplied";

        /// <summary>Limpieza segura aplicada por la reconciliación (ej. Pendiente abandonado expirado).</summary>
        public const string BillingReconciliationCleanup = "BillingReconciliationCleanup";

        /// <summary>Hallazgo que requiere decisión humana; la reconciliación nunca toca datos ambiguos.</summary>
        public const string BillingReconciliationAlert = "BillingReconciliationAlert";

        // ── Resolución y gestión del suscriptor recurrente de TiloPay ──
        public const string ProviderSubscriberResolved = "ProviderSubscriberResolved";
        public const string ProviderSubscriberResolutionPending = "ProviderSubscriberResolutionPending";
        public const string ProviderSubscriberResolutionAmbiguous = "ProviderSubscriberResolutionAmbiguous";
        public const string ProviderSubscriberResolutionFailed = "ProviderSubscriberResolutionFailed";
        public const string RecurrentUrlGenerated = "RecurrentUrlGenerated";
        public const string CheckoutBlockedExistingProviderSubscriber = "CheckoutBlockedExistingProviderSubscriber";

        /// <summary>
        /// Checkout bloqueado porque no se pudo verificar el suscriptor en TiloPay (API caído/erróneo)
        /// Y existe señal local de suscripción previa: bloquear evita crear un suscriptor duplicado.
        /// </summary>
        public const string CheckoutBlockedProviderVerificationUnavailable = "CheckoutBlockedProviderVerificationUnavailable";
        public const string ProviderSubscriptionPaused = "ProviderSubscriptionPaused";
        public const string ProviderSubscriptionReactivated = "ProviderSubscriptionReactivated";
        public const string ProviderSubscriptionDeleted = "ProviderSubscriptionDeleted";
        public const string ProviderSubscriptionDeleteFailed = "ProviderSubscriptionDeleteFailed";
        public const string UpgradeOldProviderSubscriptionCancellationRequired = "UpgradeOldProviderSubscriptionCancellationRequired";
        public const string UpgradeOldProviderSubscriptionCancellationCompleted = "UpgradeOldProviderSubscriptionCancellationCompleted";
        public const string UpgradeOldProviderSubscriptionCancellationFailed = "UpgradeOldProviderSubscriptionCancellationFailed";

        // ── Cambio de plan base (estrategia B) ──
        /// <summary>Cambio de plan bloqueado: la suscripción activa no tiene id_suscriptor del proveedor, imposible cancelar el viejo con seguridad.</summary>
        public const string PlanChangeBlockedMissingCurrentProviderSubscription = "PlanChangeBlockedMissingCurrentProviderSubscription";

        /// <summary>
        /// Intento REAL contra TiloPay para cancelar el suscriptor viejo de un cambio aplicado.
        /// Solo se registra cuando efectivamente se llamó al proveedor: es la única acción que
        /// consume presupuesto de reintentos. Los skips tienen sus propias acciones.
        /// </summary>
        public const string PlanChangeOldSubscriberCancellationRetried = "PlanChangeOldSubscriberCancellationRetried";

        /// <summary>
        /// Skip: la cancelación automática está apagada, así que NO se llamó a TiloPay. No consume
        /// presupuesto: cuando se encienda AutoCancel, el intent debe poder intentar de inmediato.
        /// </summary>
        public const string PlanChangeOldSubscriberCancellationSkippedAutoCancelDisabled = "PlanChangeOldSubscriberCancellationSkippedAutoCancelDisabled";

        /// <summary>Skip: el intent es elegible pero está en cooldown de backoff. Incluye nextEligibleUtc y el detalle del estado.</summary>
        public const string PlanChangeOldSubscriberCancellationSkippedBackoff = "PlanChangeOldSubscriberCancellationSkippedBackoff";

        /// <summary>
        /// Skip: faltan datos para un intento verificable (plan viejo o suscriptor nuevo). No se
        /// llama a TiloPay porque no se podría verificar la baja, y un 200 sin verificar no basta.
        /// </summary>
        public const string PlanChangeOldSubscriberCancellationSkippedNotEligible = "PlanChangeOldSubscriberCancellationSkippedNotEligible";

        /// <summary>Retry forzado por soporte desde la consola de plataforma: ignora el backoff, nunca la elegibilidad.</summary>
        public const string PlanChangeOldSubscriberCancellationForcedRetry = "PlanChangeOldSubscriberCancellationForcedRetry";

        /// <summary>Cambio de plan bloqueado: hay un cambio previo aplicado cuyo suscriptor viejo aún no se canceló (riesgo de múltiples rebajos).</summary>
        public const string PlanChangeBlockedPendingOldCancellation = "PlanChangeBlockedPendingOldCancellation";

        /// <summary>Cambio de plan bloqueado: el destino tiene menos cupo que los funcionarios activos.</summary>
        public const string PlanChangeBlockedDowngradeFuncionarioLimit = "PlanChangeBlockedDowngradeFuncionarioLimit";

        /// <summary>
        /// CRÍTICO: TiloPay respondió éxito a la baja del suscriptor viejo, pero la verificación
        /// posterior (getSuscriptorRepeat) lo muestra todavía Activo o no pudo confirmarse.
        /// El viejo podría seguir rebajando; queda pendiente y se reintenta con backoff.
        /// </summary>
        public const string PlanChangeOldSubscriberCancellationVerificationFailed = "PlanChangeOldSubscriberCancellationVerificationFailed";

        /// <summary>Cambio de plan bloqueado en el checkout porque la cancelación automática del viejo está deshabilitada.</summary>
        public const string PlanChangeBlockedAutoCancellationDisabled = "PlanChangeBlockedAutoCancellationDisabled";

        /// <summary>Pago nuevo confirmado pero sin id_suscriptor nuevo resuelto: NO se aplica el plan local.</summary>
        public const string PlanChangeBlockedMissingNewProviderSubscription = "PlanChangeBlockedMissingNewProviderSubscription";

        /// <summary>
        /// El id_suscriptor nuevo llegó DESPUÉS de confirmar el pago (TiloPay no lo manda en el
        /// webhook) y el cambio de plan se aplicó al resolverse. Cierra el hueco entre "pago
        /// confirmado" y "plan aplicado" sin esperar a la reconciliación.
        /// </summary>
        public const string PlanChangeAppliedAfterLateSubscriberResolution = "PlanChangeAppliedAfterLateSubscriberResolution";

        /// <summary>Lo mismo, pero reparado por el pase de reconciliación (el webhook no llegó a aplicarlo).</summary>
        public const string PlanChangeConfirmedPaymentWithLateSubscriberRepaired = "PlanChangeConfirmedPaymentWithLateSubscriberRepaired";

        /// <summary>
        /// Pago confirmado pero el plan destino no permite decidir con seguridad quién es el
        /// suscriptor nuevo (varios activos, status desconocido, o el activo no coincide con el
        /// del pago). NO se aplica el cambio: decide soporte.
        /// </summary>
        public const string PlanChangeLateSubscriberRequiresManualReview = "PlanChangeLateSubscriberRequiresManualReview";

        /// <summary>Reparación de un cambio de plan que quedó en estado inconsistente (suscriptor viejo/fechas/IDs).</summary>
        public const string PlanChangeInconsistentStateRepaired = "PlanChangeInconsistentStateRepaired";

        /// <summary>
        /// El plan destino tiene un suscriptor viejo pero INACTIVO (Delete/Cancelado): no bloquea.
        /// Es el rastro de que se ignoró a propósito y se abrió un checkout nuevo, no un duplicado.
        /// </summary>
        public const string PlanChangeIgnoredInactiveTargetProviderSubscriber = "PlanChangeIgnoredInactiveTargetProviderSubscriber";

        /// <summary>
        /// Cambio de plan bloqueado: el plan DESTINO ya tiene un suscriptor ACTIVO del mismo email.
        /// Pagar crearía un segundo suscriptor cobrando el mismo plan.
        /// </summary>
        public const string PlanChangeBlockedExistingActiveTargetSubscriber = "PlanChangeBlockedExistingActiveTargetSubscriber";

        /// <summary>
        /// Checkout bloqueado: algún suscriptor del plan destino trae un status que no sabemos
        /// clasificar. No se asume libre; decide soporte.
        /// </summary>
        public const string CheckoutBlockedUnknownTargetSubscriberStatus = "CheckoutBlockedUnknownTargetSubscriberStatus";

        /// <summary>
        /// Un PlanChangeIntent Pending que quedó huérfano porque el checkout no llegó a abrirse
        /// (bloqueo del proveedor) se cerró para no trabar el siguiente intento. Nunca toca Applied.
        /// </summary>
        public const string PlanChangePendingIntentExpiredAfterBlockedCheckout = "PlanChangePendingIntentExpiredAfterBlockedCheckout";

        /// <summary>
        /// Checkout de cambio de plan abandonado (abierto y nunca pagado) expirado por antigüedad.
        /// Sin dinero de por medio: no toca la suscripción ni al proveedor, solo libera el cupo.
        /// </summary>
        public const string PlanChangePendingCheckoutExpired = "PlanChangePendingCheckoutExpired";

        /// <summary>Un checkout de cambio abandonado fue reemplazado porque el cliente inició otro cambio.</summary>
        public const string PlanChangePendingCheckoutSuperseded = "PlanChangePendingCheckoutSuperseded";

        // ── Sincronización de la fecha real de cobro del proveedor ──

        /// <summary>
        /// El expire real de TiloPay es POSTERIOR a la fecha local: se extendió FechaFin/próximo
        /// cobro para no marcar moroso antes de tiempo. Incluye fecha local, expire del proveedor,
        /// sufijo del suscriptor y plan.
        /// </summary>
        public const string BillingProviderExpiryReconciled = "BillingProviderExpiryReconciled";

        /// <summary>
        /// El expire real de TiloPay es ANTERIOR a la fecha local. NO se acorta el acceso
        /// automáticamente (podría quitar servicio ya pagado): solo se alerta para revisión.
        /// </summary>
        public const string BillingProviderExpiryEarlierThanLocal = "BillingProviderExpiryEarlierThanLocal";

        // ── Ciclo de vida de la suscripción: cancelar / pausar / reactivar ──
        // Toda operación money-critical: cada acción del proveedor se VERIFICA contra
        // getSuscriptorRepeat (un HTTP 200 nunca basta) y queda auditada con datos sanitizados.

        /// <summary>El cliente/soporte solicitó cancelar la renovación (cancel-at-period-end). Aún no verificado.</summary>
        public const string SubscriptionCancellationRequested = "SubscriptionCancellationRequested";

        /// <summary>VERIFICADO contra TiloPay: el suscriptor quedó inactivo (Delete/Eliminado). No habrá nuevos cobros.</summary>
        public const string SubscriptionProviderCancellationVerified = "SubscriptionProviderCancellationVerified";

        /// <summary>
        /// (Obsoleta) Se emitía al solicitar Y al finalizar; se dividió en Scheduled + Finalized.
        /// Se conserva para no romper auditorías históricas ya persistidas.
        /// </summary>
        public const string SubscriptionCancellationAtPeriodEndApplied = "SubscriptionCancellationAtPeriodEndApplied";

        /// <summary>Al SOLICITAR: la cancelación de renovación quedó programada; el acceso sigue hasta la fecha efectiva.</summary>
        public const string SubscriptionCancellationScheduledAtPeriodEnd = "SubscriptionCancellationScheduledAtPeriodEnd";

        /// <summary>Al VENCER el período: la cancelación programada se cerró localmente (Estado no activo). Sin HTTP a TiloPay.</summary>
        public const string SubscriptionCancellationAtPeriodEndFinalized = "SubscriptionCancellationAtPeriodEndFinalized";

        /// <summary>El cliente/soporte pidió reactivar una RENOVACIÓN cancelada aún vigente (distinto de reactivar pausa).</summary>
        public const string SubscriptionRenewalReactivationRequested = "SubscriptionRenewalReactivationRequested";

        /// <summary>VERIFICADO: la renovación cancelada volvió a Active en TiloPay; se limpió CancelAtPeriodEnd.</summary>
        public const string SubscriptionRenewalReactivationVerified = "SubscriptionRenewalReactivationVerified";

        /// <summary>No se pudo reactivar la renovación de forma segura: NO se limpió la cancelación. Revisión manual.</summary>
        public const string SubscriptionRenewalReactivationFailedManualReview = "SubscriptionRenewalReactivationFailedManualReview";

        // ── Recuperación de pago: pago fallido / gracia / notificación / suspensión / tarjeta ──
        // Toda operación local (sin HTTP en transacción); suspensión SOLO si AutoSuspendAfterGrace.

        /// <summary>Pago recurrente fallido: se abrió incidente y período de gracia (acceso se mantiene).</summary>
        public const string SubscriptionPaymentFailedGraceStarted = "SubscriptionPaymentFailedGraceStarted";

        /// <summary>Pago recurrente exitoso posterior: el incidente abierto se resolvió y se limpió la gracia.</summary>
        public const string SubscriptionPaymentRecoveryResolved = "SubscriptionPaymentRecoveryResolved";

        /// <summary>
        /// Un repeat_payment_success SIN pending local (renovación/regularización vía url_renew) se
        /// correlacionó con la suscripción existente (plan/email + verificación en el proveedor) y
        /// activó/renovó la suscripción, cerrando el incidente. Distinto del éxito con pending normal.
        /// </summary>
        public const string PaymentRecoveryResolvedByWebhookSuccess = "PaymentRecoveryResolvedByWebhookSuccess";

        /// <summary>
        /// La reconciliación sanó una suscripción base local en gracia/morosa con EVIDENCIA de un
        /// período nuevo pagado (expire del proveedor avanzado sobre el fallo, o pago confirmado
        /// posterior al fallo; ver PaymentRecoveryRenewalEvidence): cerró el incidente, reactivó y
        /// alineó fechas con el expire del proveedor. "Active" solo NUNCA basta.
        /// </summary>
        public const string PaymentRecoveryResolvedByProviderRenewal = "PaymentRecoveryResolvedByProviderRenewal";

        /// <summary>
        /// Soporte reabrió un incidente que la reconciliación había cerrado como "renovado por el
        /// proveedor" SIN evidencia de pago (bug previo: Active + mismo expire). Restaura la gracia
        /// ORIGINAL del incidente (no la extiende) y deja que el pase normal de gracia decida.
        /// </summary>
        public const string PaymentRecoveryUnverifiedResolutionReopened = "PaymentRecoveryUnverifiedResolutionReopened";

        /// <summary>
        /// Trazabilidad financiera: un EventoPago de pago recurrente exitoso (repeat_payment_success)
        /// que había quedado SinRelacion se reconcilió contra la suscripción ya renovada/verificada en
        /// el proveedor. El evento pasó a ReconciliadoPorProveedor y, si no existía, se registró el
        /// PagoSuscripcion (Confirmado) del cobro para que el ingreso quede auditado localmente. NO
        /// extiende la suscripción (ya estaba renovada) ni duplica pagos (idempotente por transactionId).
        /// </summary>
        public const string PaymentEventReconciledByProviderRenewal = "PaymentEventReconciledByProviderRenewal";

        /// <summary>La gracia venció sin pago. Con AutoSuspend=true se suspende; con false solo se marca.</summary>
        public const string SubscriptionPaymentGraceExpired = "SubscriptionPaymentGraceExpired";

        /// <summary>Gracia vencida con AutoSuspendAfterGrace=FALSE: NO se cortó acceso, solo alerta (dry-run).</summary>
        public const string SubscriptionPaymentGraceExpiredDryRun = "SubscriptionPaymentGraceExpiredDryRun";

        /// <summary>Suspensión por impago aplicada tras vencer la gracia (solo con AutoSuspendAfterGrace=true).</summary>
        public const string SubscriptionSuspendedForNonPayment = "SubscriptionSuspendedForNonPayment";

        /// <summary>Se generó una URL de actualización de método de pago (recurrentUrl). NUNCA se loguea la URL.</summary>
        public const string PaymentMethodUpdateUrlGenerated = "PaymentMethodUpdateUrlGenerated";

        /// <summary>
        /// Se generó la recurrentUrl SOLO con el contrato de fallback (id_plan+aliases): el enlace es
        /// sospechoso (el contrato primario id_plan falló). Distinto del éxito normal para no ocultarlo.
        /// </summary>
        public const string PaymentMethodUpdateUrlGeneratedWithFallback = "PaymentMethodUpdateUrlGeneratedWithFallback";

        /// <summary>No se pudo generar la URL de actualización de método de pago (o falló la validación de dominio).</summary>
        public const string PaymentMethodUpdateUrlFailed = "PaymentMethodUpdateUrlFailed";

        /// <summary>Notificación de recuperación de pago enviada al cliente (inicio de gracia o recordatorio).</summary>
        public const string PaymentRecoveryNotificationSent = "PaymentRecoveryNotificationSent";

        /// <summary>Falló el envío de una notificación de recuperación de pago.</summary>
        public const string PaymentRecoveryNotificationFailed = "PaymentRecoveryNotificationFailed";

        /// <summary>
        /// Dry-run de notificación de recuperación: con SendEmailNotifications=false NO se envía correo,
        /// solo se deja rastro de que la etapa (inicio/recordatorio/suspensión) se habría notificado.
        /// </summary>
        public const string PaymentRecoveryNotificationDryRun = "PaymentRecoveryNotificationDryRun";

        /// <summary>Pago fallido que no se pudo correlacionar de forma segura (email/plan ambiguo): revisión manual.</summary>
        public const string PaymentRecoveryManualReview = "PaymentRecoveryManualReview";

        /// <summary>SuperAdmin cerró manualmente un incidente de recuperación de pago (con confirmación).</summary>
        public const string PaymentRecoveryManuallyResolved = "PaymentRecoveryManuallyResolved";

        /// <summary>SuperAdmin marcó un incidente de recuperación como ignorado/no accionable (con motivo).</summary>
        public const string PaymentRecoveryIgnored = "PaymentRecoveryIgnored";

        /// <summary>Idempotente: el suscriptor ya estaba inactivo en el proveedor al pedir cancelar.</summary>
        public const string SubscriptionCancellationAlreadyProviderInactive = "SubscriptionCancellationAlreadyProviderInactive";

        /// <summary>CRÍTICO: TiloPay respondió 200 pero la verificación mostró el suscriptor aún Activo (o no se pudo verificar). NO se marcó cancelada.</summary>
        public const string SubscriptionCancellationFailedManualReview = "SubscriptionCancellationFailedManualReview";

        /// <summary>Cancelación INMEDIATA (solo SuperAdmin): corta acceso de una vez tras verificar la baja.</summary>
        public const string SubscriptionImmediateCancellationRequested = "SubscriptionImmediateCancellationRequested";

        /// <summary>Soporte/SuperAdmin solicitó pausar la suscripción en el proveedor. Aún no verificado.</summary>
        public const string SubscriptionPauseRequested = "SubscriptionPauseRequested";

        /// <summary>VERIFICADO contra TiloPay: el suscriptor quedó Pausado (status 3).</summary>
        public const string SubscriptionProviderPauseVerified = "SubscriptionProviderPauseVerified";

        /// <summary>Idempotente: el suscriptor ya estaba pausado en el proveedor al pedir pausar.</summary>
        public const string SubscriptionPauseAlreadyProviderPaused = "SubscriptionPauseAlreadyProviderPaused";

        /// <summary>CRÍTICO: TiloPay respondió 200 pero la verificación no confirmó Pausado. NO se marcó pausada.</summary>
        public const string SubscriptionPauseFailedManualReview = "SubscriptionPauseFailedManualReview";

        /// <summary>Soporte/SuperAdmin solicitó reactivar la suscripción. Aún no verificado.</summary>
        public const string SubscriptionReactivateRequested = "SubscriptionReactivateRequested";

        /// <summary>VERIFICADO contra TiloPay: el suscriptor volvió a Activo (status 1).</summary>
        public const string SubscriptionProviderReactivateVerified = "SubscriptionProviderReactivateVerified";

        /// <summary>Idempotente: el suscriptor ya estaba Activo en el proveedor al pedir reactivar.</summary>
        public const string SubscriptionReactivateAlreadyProviderActive = "SubscriptionReactivateAlreadyProviderActive";

        /// <summary>
        /// Reactivación NO segura: el suscriptor está Eliminado (no reactivable) o no se pudo
        /// verificar el resultado. Se deja a revisión manual; preferir hosted checkout nuevo.
        /// </summary>
        public const string SubscriptionReactivateFailedManualReview = "SubscriptionReactivateFailedManualReview";

        /// <summary>
        /// Drift detectado por la reconciliación entre el estado local y el del proveedor
        /// (p.ej. local CancelAtPeriodEnd pero proveedor Activo = riesgo de seguir cobrando).
        /// </summary>
        public const string SubscriptionProviderStatusMismatch = "SubscriptionProviderStatusMismatch";

        /// <summary>
        /// Sincronización manual (SuperAdmin) del estado del proveedor vía getSuscriptorRepeat.
        /// Solo lectura contra TiloPay: refresca ProviderStatusRaw/LastSynced y las banderas de
        /// pausa/baja, sin cambiar el acceso ni el estado local de la suscripción.
        /// </summary>
        public const string SubscriptionProviderStatusSynced = "SubscriptionProviderStatusSynced";

        // ── Ciclo de vida del ADD-ON de WhatsApp (independiente del plan base) ──
        // Espeja las reglas money-critical del base: toda baja del suscriptor del add-on se VERIFICA
        // contra getSuscriptorRepeat (un 200 nunca basta) y, si no se puede, queda pendiente + alerta.
        // El add-on NUNCA toca el estado del plan base y viceversa.

        /// <summary>El cliente/soporte/cascada solicitó cancelar la renovación del add-on de WhatsApp.</summary>
        public const string AddonCancellationRequested = "AddonCancellationRequested";

        /// <summary>Cancelación de renovación del add-on PROGRAMADA: el uso sigue hasta el fin del período ya pagado.</summary>
        public const string AddonCancellationScheduledAtPeriodEnd = "AddonCancellationScheduledAtPeriodEnd";

        /// <summary>VERIFICADO contra TiloPay: el suscriptor del add-on quedó inactivo. No habrá nuevos cobros del add-on.</summary>
        public const string AddonProviderCancellationVerified = "AddonProviderCancellationVerified";

        /// <summary>Idempotente: el suscriptor del add-on ya estaba inactivo en TiloPay al pedir cancelar.</summary>
        public const string AddonProviderCancellationAlreadyInactive = "AddonProviderCancellationAlreadyInactive";

        /// <summary>CRÍTICO: no se pudo cancelar/verificar la baja del suscriptor del add-on (API apagado, 200 sin verificar o sigue Activo). Riesgo de doble cobro del add-on. Reintento con backoff.</summary>
        public const string AddonProviderCancellationFailedManualReview = "AddonProviderCancellationFailedManualReview";

        /// <summary>Strategy B del add-on: tras confirmar el add-on NUEVO se canceló el suscriptor ANTERIOR (huérfano de upgrade/downgrade) en TiloPay.</summary>
        public const string AddonUpgradeOldSubscriberCancellationCompleted = "AddonUpgradeOldSubscriberCancellationCompleted";

        /// <summary>CRÍTICO: no se pudo cancelar el suscriptor ANTERIOR del add-on tras un cambio de paquete. Riesgo de doble cobro. Queda pendiente con backoff.</summary>
        public const string AddonUpgradeOldSubscriberCancellationFailed = "AddonUpgradeOldSubscriberCancellationFailed";

        /// <summary>Reconciliación: add-on de WhatsApp ACTIVO con el plan base cancelado/vencido. Requiere revisión (cancelar el add-on o el estado del base).</summary>
        public const string AddonActiveWithoutActiveBaseAlert = "AddonActiveWithoutActiveBaseAlert";

        /// <summary>Reconciliación: drift entre el add-on local y el proveedor (activo local sin suscriptor cobrable, o al revés).</summary>
        public const string AddonProviderStateMismatch = "AddonProviderStateMismatch";

        /// <summary>Pago recurrente del add-on fallido: se abrió incidente + gracia del add-on (no toca el plan base).</summary>
        public const string AddonPaymentFailedGraceStarted = "AddonPaymentFailedGraceStarted";

        /// <summary>Pago recurrente del add-on exitoso posterior: el incidente del add-on se resolvió y se limpió su gracia.</summary>
        public const string AddonPaymentRecoveryResolved = "AddonPaymentRecoveryResolved";

        // ── Sondeo del proveedor tras un webhook de add-on RECHAZADO ──
        // Rechazar el webhook protege el estado local, pero NO deshace lo que TiloPay ya hizo. Sin
        // estas señales el sistema quedaba en falso verde con dos add-ons cobrables en el proveedor.

        /// <summary>CRÍTICO: tras rechazar un webhook de add-on, TiloPay quedó con DOS suscriptores de add-on cobrables para el mismo tenant. Doble cobro real o inminente.</summary>
        public const string AddonProviderDoubleActiveAfterRejectedWebhook = "AddonProviderDoubleActiveAfterRejectedWebhook";

        /// <summary>Reconciliación: auditoría periódica del proveedor detectó más de un suscriptor de add-on cobrable para el tenant.</summary>
        public const string AddonProviderDoubleActiveDetected = "AddonProviderDoubleActiveDetected";

        /// <summary>El sondeo del proveedor no fue concluyente (API caído o status desconocido): NO se asume que el proveedor esté sano.</summary>
        public const string AddonProviderAuditInconclusive = "AddonProviderAuditInconclusive";

        /// <summary>Webhook recurrente rechazado por captura: autorización sin captura, anulación o reverso. NO es un cobro real.</summary>
        public const string RecurringPaymentRejectedNotCaptured = "RecurringPaymentRejectedNotCaptured";

        /// <summary>Add-on: el id_suscriptor se resolvió tarde en un cambio de paquete; se adoptó el nuevo y el anterior quedó pendiente de baja verificada.</summary>
        public const string AddonSubscriberAdoptedAfterLateResolution = "AddonSubscriberAdoptedAfterLateResolution";

        /// <summary>Reparación MANUAL del estado local de un add-on (script operativo de reparación). Nunca confirma dinero: la baja del proveedor va por el flujo verificado.</summary>
        public const string AddonProviderRepairApplied = "AddonProviderRepairApplied";

        // ── Asociados del negocio ──
        // Quién entra al sistema, con qué permisos y con qué participación son decisiones
        // sensibles: todas quedan registradas con actor, tenant y momento.

        public const string AssociateCreated = "AssociateCreated";
        public const string AssociateUpdated = "AssociateUpdated";

        /// <summary>Alta o baja lógica del asociado. Nunca borra su histórico financiero.</summary>
        public const string AssociateStateChanged = "AssociateStateChanged";

        /// <summary>Se creó la cuenta de acceso del asociado (invitación o contraseña temporal).</summary>
        public const string AssociateAccessGranted = "AssociateAccessGranted";

        /// <summary>El asociado ya no puede iniciar sesión. Sus datos y participación se conservan.</summary>
        public const string AssociateAccessBlocked = "AssociateAccessBlocked";

        public const string AssociateAccessRestored = "AssociateAccessRestored";
        public const string AssociateAccessInvitationResent = "AssociateAccessInvitationResent";
        public const string AssociateAccessEmailChanged = "AssociateAccessEmailChanged";

        /// <summary>Cambió el conjunto de permisos concedidos (antes y después en el registro).</summary>
        public const string AssociatePermissionsUpdated = "AssociatePermissionsUpdated";

        // ── Inversionistas y distribución de ganancias ──
        // Todo lo que mueve dinero o congela un número queda auditado: la participación de un
        // inversionista es un compromiso económico del negocio.

        public const string InvestorCreated = "InvestorCreated";
        public const string InvestorUpdated = "InvestorUpdated";

        /// <summary>Cambio de porcentaje/frecuencia: se cerró el acuerdo anterior y nació una versión nueva.</summary>
        public const string InvestorAgreementChanged = "InvestorAgreementChanged";

        public const string InvestorPolicyUpdated = "InvestorPolicyUpdated";
        public const string InvestorStatementGenerated = "InvestorStatementGenerated";
        public const string InvestorStatementRecalculated = "InvestorStatementRecalculated";

        /// <summary>El snapshot quedó congelado: a partir de acá editar cobros o gastos no lo cambia.</summary>
        public const string InvestorStatementFinalized = "InvestorStatementFinalized";

        public const string InvestorStatementVoided = "InvestorStatementVoided";

        /// <summary>Reapertura explícita de un estado finalizado. Acción excepcional y siempre con motivo.</summary>
        public const string InvestorStatementReopened = "InvestorStatementReopened";

        public const string InvestorStatementSent = "InvestorStatementSent";
        public const string InvestorStatementResent = "InvestorStatementResent";
        public const string InvestorStatementAdjustmentAdded = "InvestorStatementAdjustmentAdded";
        public const string InvestorStatementAdjustmentRemoved = "InvestorStatementAdjustmentRemoved";
        public const string InvestorPaymentRegistered = "InvestorPaymentRegistered";

        /// <summary>Corrección de un pago mal registrado (monto negativo compensatorio) con motivo obligatorio.</summary>
        public const string InvestorPaymentReversed = "InvestorPaymentReversed";

        // ── Bloqueos recurrentes de horario ──

        public const string RecurringScheduleRuleCreated = "RecurringScheduleRuleCreated";

        /// <summary>Edición de una regla vigente: se cerró la versión anterior y se creó una nueva hacia el futuro.</summary>
        public const string RecurringScheduleRuleVersioned = "RecurringScheduleRuleVersioned";

        public const string RecurringScheduleRuleUpdated = "RecurringScheduleRuleUpdated";
        public const string RecurringScheduleRulePaused = "RecurringScheduleRulePaused";
        public const string RecurringScheduleRuleResumed = "RecurringScheduleRuleResumed";
        public const string RecurringScheduleRuleEnded = "RecurringScheduleRuleEnded";

        /// <summary>La regla se activó sabiendo que hay citas existentes que coinciden. Las citas NO se tocaron.</summary>
        public const string RecurringScheduleRuleActivatedWithConflicts = "RecurringScheduleRuleActivatedWithConflicts";

        public const string RecurringScheduleExceptionCreated = "RecurringScheduleExceptionCreated";
        public const string RecurringScheduleExceptionRemoved = "RecurringScheduleExceptionRemoved";
    }

    public static class PlatformAuditEntityTypes
    {
        public const string User = "User";
        public const string Tenant = "Tenant";
        public const string Subscription = "Subscription";
        public const string Billing = "Billing";
        public const string PromotionalCode = "PromotionalCode";
        public const string CommercialSnapshot = "CommercialSnapshot";
        public const string WhatsAppAddon = "WhatsAppAddon";
        public const string Associate = "Associate";
        public const string Investor = "Investor";
        public const string InvestorPolicy = "InvestorPolicy";
        public const string InvestorStatement = "InvestorStatement";
        public const string RecurringScheduleRule = "RecurringScheduleRule";

        /// <summary>Operación de pago a colaboradores (LiquidacionSemanal + su Egreso).</summary>
        public const string EmployeeSettlementPayment = "EmployeeSettlementPayment";
    }
}
