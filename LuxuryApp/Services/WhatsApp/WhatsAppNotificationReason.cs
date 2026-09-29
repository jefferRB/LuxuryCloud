using LuxuryApp.Models.WhatsApp;

namespace LuxuryApp.Services.WhatsApp
{
    /// <summary>
    /// Por qué se envió o no una notificación de WhatsApp, en términos que la UI pueda usar sin
    /// volver a razonar sobre reglas de WhatsApp.
    ///
    /// <para>
    /// Existe porque "no se envió" agrupaba dos situaciones que NO son la misma y la UI las
    /// mostraba igual: el negocio no contrató el complemento (<see cref="AddonInactive"/>, un
    /// no-evento: el cliente jamás vio la casilla) versus el cliente sí pudo autorizar y no lo hizo
    /// (<see cref="ConsentMissing"/>, información útil para contactarlo a mano).
    /// </para>
    /// </summary>
    public enum WhatsAppNotificationReason
    {
        /// <summary>Se envió (o ya estaba enviado / en cola).</summary>
        Eligible,

        /// <summary>
        /// El negocio no tiene el complemento de WhatsApp. WhatsApp es un side effect opcional:
        /// aquí se sale en silencio, sin avisos ni advertencias.
        /// </summary>
        AddonInactive,

        /// <summary>Tiene el complemento pero todavía no guardó la configuración de WhatsApp.</summary>
        NotConfigured,

        /// <summary>Tiene el complemento y lo apagó (o apagó ese tipo de aviso).</summary>
        Disabled,

        /// <summary>El cliente pudo autorizar y no lo hizo.</summary>
        ConsentMissing,

        /// <summary>El teléfono del cliente no sirve para WhatsApp.</summary>
        CustomerPhoneMissing,

        /// <summary>Se agotó la cuota diaria o mensual del paquete.</summary>
        LimitReached,

        /// <summary>La cita no admite aviso (descanso, vencida, cancelada).</summary>
        NotApplicable,

        /// <summary>Meta rechazó el envío.</summary>
        ProviderFailed,

        Other
    }

    /// <summary>
    /// Traduce el <c>ErrorCode</c> que ya guardan los servicios de WhatsApp al motivo semántico.
    /// Único lugar donde se hace esa lectura, para que Reservas y Calendario no la interpreten
    /// cada uno a su manera.
    /// </summary>
    public static class WhatsAppNotificationReasons
    {
        public static WhatsAppNotificationReason FromErrorCode(string? errorCode) =>
            errorCode switch
            {
                // El negocio no contrató (o no puede usar) el complemento: no hay nada que avisar.
                WhatsAppErrorCodes.NoActiveWhatsAppAddon or
                WhatsAppErrorCodes.SubscriptionRequired or
                WhatsAppErrorCodes.NoActiveBaseSubscription => WhatsAppNotificationReason.AddonInactive,

                WhatsAppErrorCodes.NotConfigured => WhatsAppNotificationReason.NotConfigured,

                WhatsAppErrorCodes.TenantDisabled or
                WhatsAppErrorCodes.UserDisabled or
                WhatsAppErrorCodes.NotificationTypeDisabled or
                WhatsAppErrorCodes.ConfigurationDisabled => WhatsAppNotificationReason.Disabled,

                WhatsAppErrorCodes.ConsentMissing => WhatsAppNotificationReason.ConsentMissing,

                WhatsAppErrorCodes.InvalidPhone => WhatsAppNotificationReason.CustomerPhoneMissing,

                WhatsAppErrorCodes.DailyLimitExceeded or
                WhatsAppErrorCodes.MonthlyLimitExceeded or
                WhatsAppErrorCodes.InsufficientBalance => WhatsAppNotificationReason.LimitReached,

                WhatsAppErrorCodes.AppointmentNotEligible or
                WhatsAppErrorCodes.AppointmentExpired or
                WhatsAppErrorCodes.CitaCancellada => WhatsAppNotificationReason.NotApplicable,

                _ => WhatsAppNotificationReason.Other
            };

        /// <summary>
        /// Estado con el que se registra un envío omitido en <c>WhatsAppMessageLogs</c>. Vive junto
        /// al mapeo de motivos para que exista una sola lectura del <c>ErrorCode</c>.
        /// </summary>
        public static string ToSkippedStatus(string? errorCode) =>
            errorCode switch
            {
                WhatsAppErrorCodes.ConsentMissing => WhatsAppMessageStatuses.SkippedConsentMissing,
                WhatsAppErrorCodes.TenantDisabled => WhatsAppMessageStatuses.SkippedTenantDisabled,
                WhatsAppErrorCodes.DailyLimitExceeded => WhatsAppMessageStatuses.SkippedDailyLimitExceeded,
                WhatsAppErrorCodes.NoActiveWhatsAppAddon => WhatsAppMessageStatuses.SkippedSubscriptionRequired,
                WhatsAppErrorCodes.NoActiveBaseSubscription => WhatsAppMessageStatuses.SkippedSubscriptionRequired,
                WhatsAppErrorCodes.NotConfigured => WhatsAppMessageStatuses.SkippedConfiguration,
                WhatsAppErrorCodes.SubscriptionRequired => WhatsAppMessageStatuses.SkippedSubscriptionRequired,
                WhatsAppErrorCodes.MonthlyLimitExceeded => WhatsAppMessageStatuses.SkippedMonthlyLimitExceeded,
                WhatsAppErrorCodes.InsufficientBalance => WhatsAppMessageStatuses.SkippedMonthlyLimitExceeded,
                WhatsAppErrorCodes.UserDisabled => WhatsAppMessageStatuses.SkippedUserDisabled,
                WhatsAppErrorCodes.NotificationTypeDisabled => WhatsAppMessageStatuses.SkippedUserDisabled,
                WhatsAppErrorCodes.InvalidPhone => WhatsAppMessageStatuses.SkippedInvalidPhone,
                _ => WhatsAppMessageStatuses.SkippedConfiguration
            };

        /// <summary>
        /// Si el motivo debe pasar desapercibido para el negocio. Sin complemento contratado, todo
        /// lo de WhatsApp es ruido: nunca eligió esa funcionalidad.
        /// </summary>
        public static bool IsSilent(this WhatsAppNotificationReason reason) =>
            reason is WhatsAppNotificationReason.AddonInactive or WhatsAppNotificationReason.NotApplicable;
    }
}
