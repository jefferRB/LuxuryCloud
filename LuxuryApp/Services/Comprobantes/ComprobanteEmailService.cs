using LuxuryApp.Models.Comprobantes;
using LuxuryApp.Services.Account;
using LuxuryApp.Services.Security;
using Microsoft.Extensions.Options;
using Resend;

namespace LuxuryApp.Services.Comprobantes
{
    /// <summary>
    /// Envío del comprobante vía API de Resend. Usa la misma configuración de correo del sistema
    /// (sección "Email") para From y Reply-To, adjunta el PDF y agrega tags/metadatos. El envío
    /// es idempotente: usa el token del comprobante como clave de idempotencia para que un retry
    /// no genere dos correos.
    /// </summary>
    public sealed class ComprobanteEmailService : IComprobanteEmailService
    {
        private readonly IResend _resend;
        private readonly IComprobanteHtmlRenderer _renderer;
        private readonly AccountEmailOptions _emailOptions;
        private readonly ILogger<ComprobanteEmailService> _logger;

        public ComprobanteEmailService(
            IResend resend,
            IComprobanteHtmlRenderer renderer,
            IOptions<AccountEmailOptions> emailOptions,
            ILogger<ComprobanteEmailService> logger)
        {
            _resend = resend;
            _renderer = renderer;
            _emailOptions = emailOptions.Value;
            _logger = logger;
        }

        public async Task<ComprobanteEnvioResult> EnviarComprobanteCobroAsync(
            ComprobanteCobro c,
            byte[] pdf,
            string? urlPublica,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(c);

            if (string.IsNullOrWhiteSpace(_emailOptions.SmtpPassword))
            {
                // Sin API key (o SMTP password compartido) Resend no funciona. No es un crash:
                // el cobro ya quedó guardado; el comprobante queda Failed y reintentadable.
                _logger.LogWarning(
                    "Email:SmtpPassword no configurado. Comprobante {Numero} no enviado.",
                    c.NumeroInterno);
                return new ComprobanteEnvioResult(false, null, "Servicio de correo no configurado.");
            }

            if (string.IsNullOrWhiteSpace(c.EmailDestino))
            {
                return new ComprobanteEnvioResult(false, null, "El comprobante no tiene correo destino.");
            }

            var message = new EmailMessage
            {
                From = $"{_emailOptions.FromName} <{_emailOptions.FromEmail}>",
                Subject = $"Tu comprobante de pago - {c.NombreNegocioSnapshot} - {c.NumeroInterno}",
                HtmlBody = _renderer.RenderEmailHtml(c, urlPublica),
                TextBody = _renderer.RenderEmailText(c, urlPublica)
            };
            message.To.Add(c.EmailDestino);

            // Reply-To: correo del negocio si existe; si no, el de soporte configurado.
            var replyTo = !string.IsNullOrWhiteSpace(c.EmailNegocioSnapshot)
                ? c.EmailNegocioSnapshot
                : _emailOptions.ReplyToEmail;
            if (!string.IsNullOrWhiteSpace(replyTo))
            {
                message.ReplyTo = replyTo;
            }

            message.Attachments = new List<EmailAttachment>
            {
                new()
                {
                    Filename = $"{c.NumeroInterno}.pdf",
                    Content = pdf,
                    ContentType = "application/pdf"
                }
            };

            message.Tags = new List<EmailTag>
            {
                new() { Name = "tipo", Value = "comprobante-cobro" },
                new() { Name = "tenantId", Value = c.TenantId.ToString() },
                new() { Name = "comprobanteId", Value = c.Id.ToString() },
                new() { Name = "cobroId", Value = c.CobroId.ToString() }
            };

            try
            {
                // Clave de idempotencia estable = token del comprobante: un reintento de red no duplica.
                var response = await _resend.EmailSendAsync(c.TokenPublico, message, cancellationToken);
                var resendId = response.Content.ToString();

                _logger.LogInformation(
                    "Comprobante {Numero} enviado a {Email}. ResendId {ResendId}.",
                    c.NumeroInterno,
                    SensitiveDataMasker.MaskEmail(c.EmailDestino),
                    resendId);

                return new ComprobanteEnvioResult(true, resendId, null);
            }
            catch (ResendException ex)
            {
                _logger.LogError(
                    ex,
                    "Resend rechazó el comprobante {Numero} ({ErrorType}).",
                    c.NumeroInterno,
                    ex.ErrorType);
                return new ComprobanteEnvioResult(false, null, $"Resend: {ex.ErrorType}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inesperado al enviar comprobante {Numero}.", c.NumeroInterno);
                return new ComprobanteEnvioResult(false, null, "Error al enviar el correo.");
            }
        }
    }
}
