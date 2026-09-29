namespace LuxuryApp.Models.WhatsApp
{
    public static class WhatsAppMessageDirections
    {
        public const string Outbound = "Outbound";
        public const string Inbound = "Inbound";
        public const string Status = "Status";
    }

    public static class WhatsAppNotificationTypes
    {
        public const string Confirmation = "Confirmation";
        public const string Reminder3Hours = "Reminder3Hours";

        /// <summary>
        /// Aviso de cancelacion de una cita que provino de una reserva online
        /// (<c>luxurycloud_cancelacion_cita</c>). Es un envio puntual y sincronico: la cita se
        /// elimina, asi que NO lo procesa la cola de pendientes (que solo atiende
        /// <see cref="Confirmation"/> y <see cref="Reminder3Hours"/>).
        /// </summary>
        public const string Cancellation = "Cancellation";
        public const string Reply = "Reply";
        public const string Status = "Status";
    }

    public static class WhatsAppProviders
    {
        public const string Meta = "Meta";
    }

    public static class WhatsAppMessageStatuses
    {
        public const string Pending = "Pending";
        public const string Processing = "Processing";
        public const string Sent = "Sent";
        public const string Delivered = "Delivered";
        public const string Read = "Read";
        public const string Failed = "Failed";
        public const string Received = "Received";
        public const string Ignored = "Ignored";
        public const string SkippedTenantDisabled = "SkippedTenantDisabled";
        public const string SkippedDailyLimitExceeded = "SkippedDailyLimitExceeded";
        public const string SkippedInvalidPhone = "SkippedInvalidPhone";
        public const string SkippedConfiguration = "SkippedConfiguration";
        public const string SkippedNotEligible = "SkippedNotEligible";
        public const string SkippedConsentMissing = "SkippedConsentMissing";
        public const string SkippedSubscriptionRequired = "SkippedSubscriptionRequired";
        public const string SkippedMonthlyLimitExceeded = "SkippedMonthlyLimitExceeded";
        public const string SkippedUserDisabled = "SkippedUserDisabled";
        public const string Cancelled = "Cancelled";
    }

    public static class WhatsAppErrorCodes
    {
        public const string TenantDisabled = "TenantDisabled";
        public const string DailyLimitExceeded = "DailyLimitExceeded";
        public const string InvalidPhone = "InvalidPhone";
        public const string ConfigurationDisabled = "ConfigurationDisabled";
        public const string NotificationTypeDisabled = "NotificationTypeDisabled";
        public const string AppointmentNotEligible = "AppointmentNotEligible";
        public const string ConsentMissing = "ConsentMissing";
        public const string SubscriptionRequired = "SubscriptionRequired";
        public const string NoActiveWhatsAppAddon = "NoActiveWhatsAppAddon";
        public const string NoActiveBaseSubscription = "NoActiveBaseSubscription";

        /// <summary>
        /// El tenant tiene el paquete comercial (add-on) activo pero AÚN no configuró la integración
        /// de WhatsApp (no existe TenantWhatsAppSettings). Comprar el paquete NO habilita envíos:
        /// hay que entrar a "Configurar WhatsApp" y guardar. Es un aviso operativo, no un cobro fallido.
        /// </summary>
        public const string NotConfigured = "NotConfigured";
        public const string MonthlyLimitExceeded = "MonthlyLimitExceeded";
        public const string InsufficientBalance = "InsufficientBalance";
        public const string UserDisabled = "UserDisabled";
        public const string AppointmentExpired = "AppointmentExpired";
        public const string CitaCancellada = "CitaCancellada";

        /// <summary>
        /// La cita no nacio de una reserva online (<c>BookingRequest.ConvertedCitaId</c>), asi que
        /// no corresponde avisar la cancelacion por WhatsApp.
        /// </summary>
        public const string NotOnlineBooking = "NotOnlineBooking";

    }

    public static class WhatsAppConfirmationStates
    {
        public const string Pendiente = "Pendiente";
        public const string Confirmada = "Confirmada";
        public const string Cancelada = "Cancelada";
        public const string NoEnviada = "NoEnviada";
        public const string ErrorEnvio = "ErrorEnvio";
    }

    public static class WhatsAppConsentSources
    {
        public const string ClienteForm = "ClienteForm";
        public const string ClienteRegistrado = "ClienteRegistrado";
        public const string CitaManual = "CitaManual";
        public const string SinConsentimiento = "SinConsentimiento";
    }

    public static class WhatsAppConsentTextVersions
    {
        public const string WaOptInV1 = "wa_optin_v1";
    }
}
