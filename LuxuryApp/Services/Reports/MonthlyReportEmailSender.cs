using LuxuryApp.Services.Account;
using LuxuryApp.Services.Security;
using Microsoft.Extensions.Options;
using Resend;

namespace LuxuryApp.Services.Reports
{
    /// <summary>
    /// Envío del resumen mensual vía API de Resend, con la misma configuración de correo
    /// del sistema (sección "Email"). Usa clave de idempotencia para que un retry de red
    /// no genere dos correos. Sigue el patrón de <c>ComprobanteEmailService</c>.
    /// </summary>
    public sealed class MonthlyReportEmailSender : IMonthlyReportEmailSender
    {
        private readonly IResend _resend;
        private readonly AccountEmailOptions _emailOptions;
        private readonly ILogger<MonthlyReportEmailSender> _logger;

        public MonthlyReportEmailSender(
            IResend resend,
            IOptions<AccountEmailOptions> emailOptions,
            ILogger<MonthlyReportEmailSender> logger)
        {
            _resend = resend;
            _emailOptions = emailOptions.Value;
            _logger = logger;
        }

        public async Task<MonthlyReportEmailSendAttempt> SendAsync(
            string recipientEmail,
            string subject,
            string htmlBody,
            string textBody,
            string idempotencyKey,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_emailOptions.SmtpPassword))
            {
                _logger.LogWarning(
                    "Email:SmtpPassword no configurado. Resumen mensual no enviado para el tenant {TenantId}.",
                    tenantId);
                return new MonthlyReportEmailSendAttempt(false, null, "Servicio de correo no configurado.");
            }

            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                return new MonthlyReportEmailSendAttempt(false, null, "El correo destino está vacío.");
            }

            var message = new EmailMessage
            {
                From = $"{_emailOptions.FromName} <{_emailOptions.FromEmail}>",
                Subject = subject,
                HtmlBody = htmlBody,
                TextBody = textBody
            };
            message.To.Add(recipientEmail);

            if (!string.IsNullOrWhiteSpace(_emailOptions.ReplyToEmail))
            {
                message.ReplyTo = _emailOptions.ReplyToEmail;
            }

            message.Tags = new List<EmailTag>
            {
                new() { Name = "tipo", Value = "resumen-mensual" },
                new() { Name = "tenantId", Value = tenantId.ToString() }
            };

            try
            {
                var response = await _resend.EmailSendAsync(idempotencyKey, message, cancellationToken);
                var resendId = response.Content.ToString();

                _logger.LogInformation(
                    "Resumen mensual enviado a {Email} para el tenant {TenantId}. ResendId {ResendId}.",
                    SensitiveDataMasker.MaskEmail(recipientEmail),
                    tenantId,
                    resendId);

                return new MonthlyReportEmailSendAttempt(true, resendId, null);
            }
            catch (ResendException ex)
            {
                _logger.LogError(
                    ex,
                    "Resend rechazó el resumen mensual del tenant {TenantId} ({ErrorType}).",
                    tenantId,
                    ex.ErrorType);
                return new MonthlyReportEmailSendAttempt(false, null, $"Resend: {ex.ErrorType}");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error inesperado al enviar el resumen mensual del tenant {TenantId}.",
                    tenantId);
                return new MonthlyReportEmailSendAttempt(false, null, "Error al enviar el correo.");
            }
        }
    }
}
