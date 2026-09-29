namespace LuxuryApp.Services.WhatsApp
{
    public sealed class MetaWhatsAppOptions
    {
        public const string SectionName = "MetaWhatsApp";

        public bool Enabled { get; set; }

        public string GraphApiVersion { get; set; } = "v25.0";

        public string BaseUrl { get; set; } = "https://graph.facebook.com";

        public string PhoneNumberId { get; set; } = string.Empty;

        public string WhatsAppBusinessAccountId { get; set; } = string.Empty;

        public string AccessToken { get; set; } = string.Empty;

        public string AppSecret { get; set; } = string.Empty;

        public string WebhookVerifyToken { get; set; } = string.Empty;

        public string DefaultCountryCode { get; set; } = "506";

        public string ConfirmationTemplateName { get; set; } = "luxurycloud_confirmacion_cita_v1";

        public string ReminderTemplateName { get; set; } = "luxurycloud_recordatorio_cita_3h_v1";

        /// <summary>
        /// Aviso de cancelacion de citas nacidas de una reserva online. Categoria Utility, mismo
        /// idioma ("es") que el resto de plantillas.
        /// </summary>
        public string CancellationTemplateName { get; set; } = "luxurycloud_cancelacion_cita";

        public int ReminderLeadTimeMinutes { get; set; } = 180;

        public bool SendConfirmationOnCreate { get; set; } = true;

        public bool SendReminderBeforeAppointment { get; set; } = true;

        public int RequestTimeoutSeconds { get; set; } = 15;
    }
}
