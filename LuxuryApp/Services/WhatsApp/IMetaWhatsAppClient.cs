namespace LuxuryApp.Services.WhatsApp
{
    public interface IMetaWhatsAppClient
    {
        string? NormalizePhoneNumber(string? phoneNumber);

        bool IsValidPhoneNumber(string? phoneNumber);

        Task<MetaWhatsAppSendResult> SendConfirmationTemplateAsync(
            string recipientPhone,
            string customerName,
            string businessName,
            string appointmentDate,
            string appointmentTime,
            string professionalName,
            CancellationToken cancellationToken = default);

        Task<MetaWhatsAppSendResult> SendReminderTemplateAsync(
            string recipientPhone,
            string customerName,
            string businessName,
            string appointmentTime,
            string professionalName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Envia <c>luxurycloud_cancelacion_cita</c>. Los parametros van nombrados en
        /// <see cref="WhatsAppCancellationTemplateParameters"/> para que el orden posicional del
        /// template no dependa de quien llama.
        /// </summary>
        Task<MetaWhatsAppSendResult> SendCancellationTemplateAsync(
            string recipientPhone,
            WhatsAppCancellationTemplateParameters parameters,
            CancellationToken cancellationToken = default);

        Task<MetaWhatsAppSendResult> SendTextMessageAsync(
            string recipientPhone,
            string message,
            CancellationToken cancellationToken = default);

        Task<MetaWhatsAppConfigurationDiagnosticResult> TestConfigurationAsync(
            CancellationToken cancellationToken = default);
    }
}
