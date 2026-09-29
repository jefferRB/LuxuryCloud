using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Account;
using LuxuryApp.Services.Security;
using Microsoft.Extensions.Options;
using Resend;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Envío del estado de participación vía API de Resend, con la misma configuración de correo
    /// del sistema (sección "Email"). Adjunta el PDF y usa clave de idempotencia para que un retry
    /// de red no genere dos correos. Sigue el patrón de <c>MonthlyReportEmailSender</c>.
    /// </summary>
    public sealed class InvestorStatementEmailSender : IInvestorStatementEmailSender
    {
        private readonly IResend _resend;
        private readonly AccountEmailOptions _emailOptions;
        private readonly ILogger<InvestorStatementEmailSender> _logger;

        public InvestorStatementEmailSender(
            IResend resend,
            IOptions<AccountEmailOptions> emailOptions,
            ILogger<InvestorStatementEmailSender> logger)
        {
            _resend = resend;
            _emailOptions = emailOptions.Value;
            _logger = logger;
        }

        public async Task<InvestorStatementEmailSendAttempt> SendAsync(
            InvestorStatementDocument document,
            string recipientEmail,
            string subject,
            string htmlBody,
            string textBody,
            byte[]? pdf,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(document);

            if (string.IsNullOrWhiteSpace(_emailOptions.SmtpPassword))
            {
                _logger.LogWarning(
                    "Email:SmtpPassword no configurado. Estado de participación {StatementId} no enviado.",
                    document.StatementId);
                return new InvestorStatementEmailSendAttempt(false, null, "Servicio de correo no configurado.");
            }

            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                return new InvestorStatementEmailSendAttempt(false, null, "El correo destino está vacío.");
            }

            var message = new EmailMessage
            {
                From = $"{_emailOptions.FromName} <{_emailOptions.FromEmail}>",
                Subject = subject,
                HtmlBody = htmlBody,
                TextBody = textBody
            };
            message.To.Add(recipientEmail);

            // Reply-To: el correo del negocio si lo tiene; si no, el de soporte configurado.
            var replyTo = !string.IsNullOrWhiteSpace(document.EmailNegocio)
                ? document.EmailNegocio
                : _emailOptions.ReplyToEmail;

            if (!string.IsNullOrWhiteSpace(replyTo))
            {
                message.ReplyTo = replyTo;
            }

            if (pdf is { Length: > 0 })
            {
                message.Attachments = new List<EmailAttachment>
                {
                    new()
                    {
                        Filename = document.NombreArchivo,
                        Content = pdf,
                        ContentType = "application/pdf"
                    }
                };
            }

            message.Tags = new List<EmailTag>
            {
                new() { Name = "tipo", Value = "estado-participacion" },
                new() { Name = "tenantId", Value = document.TenantId.ToString() },
                new() { Name = "statementId", Value = document.StatementId.ToString() }
            };

            try
            {
                var response = await _resend.EmailSendAsync(idempotencyKey, message, cancellationToken);
                var resendId = response.Content.ToString();

                _logger.LogInformation(
                    "Estado de participación {StatementId} enviado a {Email}. ResendId {ResendId}.",
                    document.StatementId,
                    SensitiveDataMasker.MaskEmail(recipientEmail),
                    resendId);

                return new InvestorStatementEmailSendAttempt(true, resendId, null);
            }
            catch (ResendException ex)
            {
                // Se registra el tipo de error del proveedor, nunca el cuerpo del correo ni datos financieros.
                _logger.LogError(
                    ex,
                    "Resend rechazó el estado de participación {StatementId} ({ErrorType}).",
                    document.StatementId,
                    ex.ErrorType);
                return new InvestorStatementEmailSendAttempt(false, null, $"Resend: {ex.ErrorType}");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error inesperado al enviar el estado de participación {StatementId}.",
                    document.StatementId);
                return new InvestorStatementEmailSendAttempt(false, null, "Error al enviar el correo.");
            }
        }
    }
}
