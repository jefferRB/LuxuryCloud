using MailKit.Net.Smtp;
using MailKit.Security;
using System.Text.Encodings.Web;
using LuxuryApp.Services.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace LuxuryApp.Services.Account
{
    public sealed class AccountEmailService : IAccountEmailService
    {
        private readonly IOptions<AccountEmailOptions> _emailOptions;
        private readonly ILogger<AccountEmailService> _logger;

        public AccountEmailService(
            IOptions<AccountEmailOptions> emailOptions,
            ILogger<AccountEmailService> logger)
        {
            _emailOptions = emailOptions;
            _logger = logger;
        }

        public async Task SendPasswordResetEmailAsync(
            string toEmail,
            string displayName,
            string resetLink,
            CancellationToken cancellationToken = default)
        {
            var opts = _emailOptions.Value;
            var maskedEmail = SensitiveDataMasker.MaskEmail(toEmail);

            if (string.IsNullOrWhiteSpace(opts.SmtpPassword))
            {
                _logger.LogWarning(
                    "Email:SmtpPassword no configurado. Email de restablecimiento no enviado para {MaskedEmail}.",
                    maskedEmail);
                return;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(opts.FromName, opts.FromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));

            if (!string.IsNullOrWhiteSpace(opts.ReplyToEmail))
            {
                message.ReplyTo.Add(MailboxAddress.Parse(opts.ReplyToEmail));
            }

            message.Subject = "Restablece tu contraseña de LuxuryCloud";
            message.Body = new TextPart(TextFormat.Html)
            {
                Text = BuildResetEmailHtml(displayName, resetLink)
            };

            using var client = new SmtpClient();
            try
            {
                await client.ConnectAsync(opts.SmtpHost, opts.SmtpPort, SecureSocketOptions.StartTls, cancellationToken);
                await client.AuthenticateAsync(opts.SmtpUsername, opts.SmtpPassword, cancellationToken);
                await client.SendAsync(message, cancellationToken);
                await client.DisconnectAsync(quit: true, cancellationToken);

                _logger.LogInformation(
                    "Email de restablecimiento de contraseña enviado a {MaskedEmail}.",
                    maskedEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error al enviar email de restablecimiento para {MaskedEmail}.",
                    maskedEmail);
                throw;
            }
        }

        public async Task SendEmailConfirmationEmailAsync(
            string toEmail,
            string displayName,
            string confirmationLink,
            CancellationToken cancellationToken = default)
        {
            var opts = _emailOptions.Value;
            var maskedEmail = SensitiveDataMasker.MaskEmail(toEmail);

            if (string.IsNullOrWhiteSpace(opts.SmtpPassword))
            {
                _logger.LogWarning(
                    "Email:SmtpPassword no configurado. Email de confirmacion no enviado para {MaskedEmail}.",
                    maskedEmail);
                return;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(opts.FromName, opts.FromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));

            if (!string.IsNullOrWhiteSpace(opts.ReplyToEmail))
            {
                message.ReplyTo.Add(MailboxAddress.Parse(opts.ReplyToEmail));
            }

            message.Subject = "Confirma tu correo de LuxuryCloud";
            message.Body = new TextPart(TextFormat.Html)
            {
                Text = BuildEmailConfirmationHtml(displayName, confirmationLink)
            };

            using var client = new SmtpClient();
            try
            {
                await client.ConnectAsync(opts.SmtpHost, opts.SmtpPort, SecureSocketOptions.StartTls, cancellationToken);
                await client.AuthenticateAsync(opts.SmtpUsername, opts.SmtpPassword, cancellationToken);
                await client.SendAsync(message, cancellationToken);
                await client.DisconnectAsync(quit: true, cancellationToken);

                _logger.LogInformation(
                    "Email de confirmacion enviado a {MaskedEmail}.",
                    maskedEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error al enviar email de confirmacion para {MaskedEmail}.",
                    maskedEmail);
                throw;
            }
        }

        /// <summary>Frase por defecto: la que ha recibido siempre el portal de funcionarios.</summary>
        internal const string DescripcionAccesoFuncionario =
            "tu portal personal en LuxuryCloud, donde podrás ver tu agenda, tus citas, tu producción y tus pagos";

        public Task SendFuncionarioInvitationEmailAsync(
            string toEmail,
            string displayName,
            string setPasswordLink,
            string businessName,
            CancellationToken cancellationToken = default) =>
            SendAccessInvitationEmailAsync(
                toEmail,
                displayName,
                setPasswordLink,
                businessName,
                DescripcionAccesoFuncionario,
                cancellationToken);

        public async Task SendAccessInvitationEmailAsync(
            string toEmail,
            string displayName,
            string setPasswordLink,
            string businessName,
            string accessDescription,
            CancellationToken cancellationToken = default)
        {
            var opts = _emailOptions.Value;
            var maskedEmail = SensitiveDataMasker.MaskEmail(toEmail);

            if (string.IsNullOrWhiteSpace(opts.SmtpPassword))
            {
                _logger.LogWarning(
                    "Email:SmtpPassword no configurado. Invitación de acceso no enviada para {MaskedEmail}.",
                    maskedEmail);
                return;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(opts.FromName, opts.FromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));

            if (!string.IsNullOrWhiteSpace(opts.ReplyToEmail))
            {
                message.ReplyTo.Add(MailboxAddress.Parse(opts.ReplyToEmail));
            }

            message.Subject = "Tu acceso al portal de LuxuryCloud";
            message.Body = new TextPart(TextFormat.Html)
            {
                Text = BuildInvitationEmailHtml(displayName, setPasswordLink, businessName, accessDescription)
            };

            using var client = new SmtpClient();
            try
            {
                await client.ConnectAsync(opts.SmtpHost, opts.SmtpPort, SecureSocketOptions.StartTls, cancellationToken);
                await client.AuthenticateAsync(opts.SmtpUsername, opts.SmtpPassword, cancellationToken);
                await client.SendAsync(message, cancellationToken);
                await client.DisconnectAsync(quit: true, cancellationToken);

                _logger.LogInformation(
                    "Invitación de acceso enviada a {MaskedEmail}.",
                    maskedEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error al enviar invitación de acceso para {MaskedEmail}.",
                    maskedEmail);
                throw;
            }
        }

        internal static string BuildInvitationEmailHtml(
            string displayName,
            string setPasswordLink,
            string businessName = "",
            string? accessDescription = null)
        {
            var safeDisplayName = HtmlEncoder.Default.Encode(displayName);
            var safeLink = HtmlEncoder.Default.Encode(setPasswordLink);
            var safeBusinessName = HtmlEncoder.Default.Encode(
                string.IsNullOrWhiteSpace(businessName) ? "Tu negocio" : businessName);
            var safeAccessDescription = HtmlEncoder.Default.Encode(
                string.IsNullOrWhiteSpace(accessDescription)
                    ? DescripcionAccesoFuncionario
                    : accessDescription);

            return $"""
                <!DOCTYPE html>
                <html lang="es">
                <head>
                  <meta charset="UTF-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                  <title>Acceso al portal</title>
                </head>
                <body style="margin:0;padding:0;background:#f5f5f5;font-family:Arial,Helvetica,sans-serif;">
                  <table width="100%" cellpadding="0" cellspacing="0" style="background:#f5f5f5;padding:32px 0;">
                    <tr>
                      <td align="center">
                        <table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.08);max-width:600px;width:100%;">
                          <tr>
                            <td style="background:#111111;padding:24px 32px;">
                              <span style="color:#ffffff;font-size:20px;font-weight:700;letter-spacing:.5px;">LuxuryCloud</span>
                            </td>
                          </tr>
                          <tr>
                            <td style="padding:36px 32px 24px;">
                              <p style="margin:0 0 16px;font-size:16px;color:#333333;">Hola, <strong>{safeDisplayName}</strong>,</p>
                              <p style="margin:0 0 24px;font-size:15px;color:#555555;line-height:1.6;">
                                <strong>{safeBusinessName}</strong> te habilitó acceso a {safeAccessDescription}.
                                Para empezar, define tu contraseña con el siguiente botón.
                              </p>
                              <table cellpadding="0" cellspacing="0" style="margin:0 0 24px;">
                                <tr>
                                  <td style="background:#111111;border-radius:6px;padding:0;">
                                    <a href="{safeLink}"
                                       style="display:inline-block;padding:14px 28px;color:#ffffff;font-size:15px;font-weight:600;text-decoration:none;border-radius:6px;">
                                      Definir mi contraseña
                                    </a>
                                  </td>
                                </tr>
                              </table>
                              <p style="margin:0 0 12px;font-size:13px;color:#888888;line-height:1.5;">
                                Si no puedes hacer clic en el botón, copia y pega este enlace en tu navegador:
                              </p>
                              <p style="margin:0 0 24px;font-size:12px;color:#aaaaaa;word-break:break-all;">{safeLink}</p>
                              <hr style="border:none;border-top:1px solid #eeeeee;margin:24px 0;" />
                              <p style="margin:0;font-size:13px;color:#999999;line-height:1.5;">
                                Este enlace es válido por 24 horas. Si no esperabas este correo, puedes ignorarlo.
                              </p>
                            </td>
                          </tr>
                          <tr>
                            <td style="background:#f9f9f9;padding:16px 32px;border-top:1px solid #eeeeee;">
                              <p style="margin:0;font-size:12px;color:#aaaaaa;">
                                &copy; 2026 LuxuryCloud. Todos los derechos reservados.
                              </p>
                            </td>
                          </tr>
                        </table>
                      </td>
                    </tr>
                  </table>
                </body>
                </html>
                """;
        }

        internal static string BuildEmailConfirmationHtml(string displayName, string confirmationLink)
        {
            var safeDisplayName = HtmlEncoder.Default.Encode(displayName);
            var safeConfirmationLink = HtmlEncoder.Default.Encode(confirmationLink);

            return $"""
                <!DOCTYPE html>
                <html lang="es">
                <head>
                  <meta charset="UTF-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                  <title>Confirmar correo</title>
                </head>
                <body style="margin:0;padding:0;background:#f5f5f5;font-family:Arial,Helvetica,sans-serif;">
                  <table width="100%" cellpadding="0" cellspacing="0" style="background:#f5f5f5;padding:32px 0;">
                    <tr>
                      <td align="center">
                        <table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.08);max-width:600px;width:100%;">
                          <tr>
                            <td style="background:#111111;padding:24px 32px;">
                              <span style="color:#ffffff;font-size:20px;font-weight:700;letter-spacing:.5px;">LuxuryCloud</span>
                            </td>
                          </tr>
                          <tr>
                            <td style="padding:36px 32px 24px;">
                              <p style="margin:0 0 16px;font-size:16px;color:#333333;">Hola, <strong>{safeDisplayName}</strong>,</p>
                              <p style="margin:0 0 24px;font-size:15px;color:#555555;line-height:1.6;">
                                Confirma tu correo para activar el registro de tu negocio y continuar con la seleccion del plan.
                              </p>
                              <table cellpadding="0" cellspacing="0" style="margin:0 0 24px;">
                                <tr>
                                  <td style="background:#111111;border-radius:6px;padding:0;">
                                    <a href="{safeConfirmationLink}"
                                       style="display:inline-block;padding:14px 28px;color:#ffffff;font-size:15px;font-weight:600;text-decoration:none;border-radius:6px;">
                                      Confirmar correo
                                    </a>
                                  </td>
                                </tr>
                              </table>
                              <p style="margin:0 0 12px;font-size:13px;color:#888888;line-height:1.5;">
                                Si no puedes hacer clic en el boton, copia y pega este enlace en tu navegador:
                              </p>
                              <p style="margin:0 0 24px;font-size:12px;color:#aaaaaa;word-break:break-all;">{safeConfirmationLink}</p>
                              <hr style="border:none;border-top:1px solid #eeeeee;margin:24px 0;" />
                              <p style="margin:0;font-size:13px;color:#999999;line-height:1.5;">
                                Si no creaste esta cuenta, puedes ignorar este mensaje.
                              </p>
                            </td>
                          </tr>
                          <tr>
                            <td style="background:#f9f9f9;padding:16px 32px;border-top:1px solid #eeeeee;">
                              <p style="margin:0;font-size:12px;color:#aaaaaa;">
                                &copy; 2026 LuxuryCloud. Todos los derechos reservados.
                              </p>
                            </td>
                          </tr>
                        </table>
                      </td>
                    </tr>
                  </table>
                </body>
                </html>
                """;
        }

        internal static string BuildResetEmailHtml(string displayName, string resetLink)
        {
            var safeDisplayName = HtmlEncoder.Default.Encode(displayName);
            var safeResetLink = HtmlEncoder.Default.Encode(resetLink);

            return $"""
                <!DOCTYPE html>
                <html lang="es">
                <head>
                  <meta charset="UTF-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                  <title>Restablecer contraseña</title>
                </head>
                <body style="margin:0;padding:0;background:#f5f5f5;font-family:Arial,Helvetica,sans-serif;">
                  <table width="100%" cellpadding="0" cellspacing="0" style="background:#f5f5f5;padding:32px 0;">
                    <tr>
                      <td align="center">
                        <table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.08);max-width:600px;width:100%;">
                          <tr>
                            <td style="background:#111111;padding:24px 32px;">
                              <span style="color:#ffffff;font-size:20px;font-weight:700;letter-spacing:.5px;">LuxuryCloud</span>
                            </td>
                          </tr>
                          <tr>
                            <td style="padding:36px 32px 24px;">
                              <p style="margin:0 0 16px;font-size:16px;color:#333333;">Hola, <strong>{safeDisplayName}</strong>,</p>
                              <p style="margin:0 0 24px;font-size:15px;color:#555555;line-height:1.6;">
                                Recibimos una solicitud para restablecer la contraseña de tu cuenta en LuxuryCloud.
                                Haz clic en el botón a continuación para establecer una nueva contraseña.
                              </p>
                              <table cellpadding="0" cellspacing="0" style="margin:0 0 24px;">
                                <tr>
                                  <td style="background:#111111;border-radius:6px;padding:0;">
                                    <a href="{safeResetLink}"
                                       style="display:inline-block;padding:14px 28px;color:#ffffff;font-size:15px;font-weight:600;text-decoration:none;border-radius:6px;">
                                      Restablecer contraseña
                                    </a>
                                  </td>
                                </tr>
                              </table>
                              <p style="margin:0 0 12px;font-size:13px;color:#888888;line-height:1.5;">
                                Si no puedes hacer clic en el botón, copia y pega este enlace en tu navegador:
                              </p>
                              <p style="margin:0 0 24px;font-size:12px;color:#aaaaaa;word-break:break-all;">{safeResetLink}</p>
                              <hr style="border:none;border-top:1px solid #eeeeee;margin:24px 0;" />
                              <p style="margin:0;font-size:13px;color:#999999;line-height:1.5;">
                                Este enlace es válido por 24 horas. Si no solicitaste este cambio, puedes ignorar este mensaje;
                                tu contraseña actual permanece sin cambios.
                              </p>
                            </td>
                          </tr>
                          <tr>
                            <td style="background:#f9f9f9;padding:16px 32px;border-top:1px solid #eeeeee;">
                              <p style="margin:0;font-size:12px;color:#aaaaaa;">
                                &copy; 2026 LuxuryCloud. Todos los derechos reservados.
                              </p>
                            </td>
                          </tr>
                        </table>
                      </td>
                    </tr>
                  </table>
                </body>
                </html>
                """;
        }
    }
}
