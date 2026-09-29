using System.Text.Encodings.Web;
using LuxuryApp.Services.Account;
using LuxuryApp.Services.Security;
using Microsoft.Extensions.Options;
using Resend;

namespace LuxuryApp.Services.Billing
{
    /// <summary>Etapa de la notificación de recuperación de pago.</summary>
    public enum PaymentRecoveryEmailKind
    {
        /// <summary>Pago recurrente fallido: inicio del período de gracia.</summary>
        PaymentFailed = 0,

        /// <summary>Recordatorio: la gracia está por vencer.</summary>
        GraceReminder = 1,

        /// <summary>Suspensión por impago (solo con AutoSuspendAfterGrace=true).</summary>
        Suspended = 2
    }

    /// <summary>Datos seguros (sin tarjeta ni recurrentUrl persistida) para armar el correo.</summary>
    public sealed record PaymentRecoveryEmailContext
    {
        public required string ToEmail { get; init; }
        public string? DisplayName { get; init; }

        /// <summary>Fecha límite de gracia ya formateada en hora de Costa Rica (no UTC crudo).</summary>
        public string? GraceEndsDisplay { get; init; }

        /// <summary>URL absoluta a una ruta interna segura (p.ej. /Billing/Suscripcion). NUNCA la recurrentUrl.</summary>
        public string? UpdateUrl { get; init; }

        public required Guid TenantId { get; init; }
        public required Guid IncidentId { get; init; }
    }

    public sealed record PaymentRecoveryEmailResult(bool Sent, string? Error)
    {
        public static PaymentRecoveryEmailResult Ok() => new(true, null);
        public static PaymentRecoveryEmailResult Fail(string error) => new(false, error);
    }

    public interface IPaymentRecoveryEmailSender
    {
        /// <summary>
        /// Envía el correo de la etapa indicada usando la infraestructura de correo del sistema
        /// (Resend + sección "Email"). Idempotente por incidente+etapa (clave de idempotencia), para
        /// que un retry de red no genere dos correos. NUNCA incluye datos de tarjeta ni recurrentUrl.
        /// </summary>
        Task<PaymentRecoveryEmailResult> SendAsync(
            PaymentRecoveryEmailKind kind,
            PaymentRecoveryEmailContext context,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Envío real de los correos de recuperación de pago vía Resend, con la misma configuración del
    /// resto del sistema (sección "Email"). Sigue el patrón de <see cref="Reports.MonthlyReportEmailSender"/>:
    /// devuelve éxito/fallo (no lanza) para que el servicio de notificación pueda auditar Sent/Failed.
    /// </summary>
    public sealed class PaymentRecoveryEmailSender : IPaymentRecoveryEmailSender
    {
        private readonly IResend _resend;
        private readonly AccountEmailOptions _emailOptions;
        private readonly ILogger<PaymentRecoveryEmailSender> _logger;

        public PaymentRecoveryEmailSender(
            IResend resend,
            IOptions<AccountEmailOptions> emailOptions,
            ILogger<PaymentRecoveryEmailSender> logger)
        {
            _resend = resend;
            _emailOptions = emailOptions.Value;
            _logger = logger;
        }

        public async Task<PaymentRecoveryEmailResult> SendAsync(
            PaymentRecoveryEmailKind kind,
            PaymentRecoveryEmailContext context,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_emailOptions.SmtpPassword))
            {
                _logger.LogWarning(
                    "Email:SmtpPassword no configurado. Correo de recuperación ({Kind}) no enviado para el tenant {TenantId}.",
                    kind, context.TenantId);
                return PaymentRecoveryEmailResult.Fail("Servicio de correo no configurado.");
            }

            if (string.IsNullOrWhiteSpace(context.ToEmail))
            {
                return PaymentRecoveryEmailResult.Fail("El correo destino está vacío.");
            }

            var (subject, html) = Build(kind, context);

            var message = new EmailMessage
            {
                From = $"{_emailOptions.FromName} <{_emailOptions.FromEmail}>",
                Subject = subject,
                HtmlBody = html
            };
            message.To.Add(context.ToEmail);

            if (!string.IsNullOrWhiteSpace(_emailOptions.ReplyToEmail))
            {
                message.ReplyTo = _emailOptions.ReplyToEmail;
            }

            message.Tags = new List<EmailTag>
            {
                new() { Name = "tipo", Value = "recuperacion-pago" },
                new() { Name = "etapa", Value = kind.ToString() },
                new() { Name = "tenantId", Value = context.TenantId.ToString() }
            };

            // Clave de idempotencia estable por incidente+etapa: un retry no manda dos correos.
            var idempotencyKey = $"payrec:{context.IncidentId:N}:{kind}";

            try
            {
                await _resend.EmailSendAsync(idempotencyKey, message, cancellationToken);
                _logger.LogInformation(
                    "Correo de recuperación ({Kind}) enviado a {Email} para el tenant {TenantId}.",
                    kind, SensitiveDataMasker.MaskEmail(context.ToEmail), context.TenantId);
                return PaymentRecoveryEmailResult.Ok();
            }
            catch (ResendException ex)
            {
                _logger.LogError(
                    ex,
                    "Resend rechazó el correo de recuperación ({Kind}) del tenant {TenantId} ({ErrorType}).",
                    kind, context.TenantId, ex.ErrorType);
                return PaymentRecoveryEmailResult.Fail($"Resend: {ex.ErrorType}");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error inesperado al enviar el correo de recuperación ({Kind}) del tenant {TenantId}.",
                    kind, context.TenantId);
                return PaymentRecoveryEmailResult.Fail("Error al enviar el correo.");
            }
        }

        private static (string Subject, string Html) Build(PaymentRecoveryEmailKind kind, PaymentRecoveryEmailContext context)
        {
            var name = HtmlEncoder.Default.Encode(
                string.IsNullOrWhiteSpace(context.DisplayName) ? "Hola" : context.DisplayName!);
            var grace = HtmlEncoder.Default.Encode(context.GraceEndsDisplay ?? "el fin del período de gracia");

            return kind switch
            {
                PaymentRecoveryEmailKind.PaymentFailed => (
                    "No pudimos procesar tu pago de LuxuryCloud",
                    BuildHtml(
                        name,
                        title: "No pudimos procesar tu pago",
                        body: $"No pudimos procesar el pago de tu suscripción. Tu acceso seguirá activo hasta " +
                              $"<strong>{grace}</strong>. Actualizá tu método de pago para evitar la suspensión.",
                        cta: "Actualizar método de pago",
                        context.UpdateUrl)),

                PaymentRecoveryEmailKind.GraceReminder => (
                    "Tu suscripción de LuxuryCloud necesita actualizar el pago",
                    BuildHtml(
                        name,
                        title: "Tu período de gracia está por finalizar",
                        body: $"Tu período de gracia está por finalizar. Actualizá tu método de pago antes de " +
                              $"<strong>{grace}</strong> para evitar la suspensión.",
                        cta: "Actualizar método de pago",
                        context.UpdateUrl)),

                _ => (
                    "Tu cuenta fue suspendida por pago pendiente",
                    BuildHtml(
                        name,
                        title: "Tu cuenta fue suspendida por pago pendiente",
                        body: "Tu cuenta fue suspendida porque no pudimos procesar el pago. Actualizá tu método de " +
                              "pago para reactivarla.",
                        cta: "Actualizar método de pago",
                        context.UpdateUrl))
            };
        }

        private static string BuildHtml(string name, string title, string body, string cta, string? updateUrl)
        {
            var safeTitle = HtmlEncoder.Default.Encode(title);
            var safeCta = HtmlEncoder.Default.Encode(cta);

            // El CTA apunta a una ruta interna segura (login requerido) que genera la recurrentUrl
            // on-demand; nunca a una recurrentUrl persistida. Sin base pública válida, se omite el botón.
            var ctaBlock = string.IsNullOrWhiteSpace(updateUrl)
                ? "<p style=\"margin:0 0 24px;font-size:14px;color:#555555;line-height:1.6;\">Ingresá a tu cuenta de LuxuryCloud, entrá a <strong>Suscripción</strong> y actualizá tu método de pago.</p>"
                : $"""
                    <table cellpadding="0" cellspacing="0" style="margin:0 0 24px;">
                      <tr>
                        <td style="background:#111111;border-radius:6px;padding:0;">
                          <a href="{HtmlEncoder.Default.Encode(updateUrl)}"
                             style="display:inline-block;padding:14px 28px;color:#ffffff;font-size:15px;font-weight:600;text-decoration:none;border-radius:6px;">
                            {safeCta}
                          </a>
                        </td>
                      </tr>
                    </table>
                    """;

            return $"""
                <!DOCTYPE html>
                <html lang="es">
                <head>
                  <meta charset="UTF-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                  <title>{safeTitle}</title>
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
                              <p style="margin:0 0 16px;font-size:16px;color:#333333;"><strong>{name}</strong>,</p>
                              <p style="margin:0 0 16px;font-size:18px;color:#111111;font-weight:700;">{safeTitle}</p>
                              <p style="margin:0 0 24px;font-size:15px;color:#555555;line-height:1.6;">{body}</p>
                              {ctaBlock}
                              <hr style="border:none;border-top:1px solid #eeeeee;margin:24px 0;" />
                              <p style="margin:0;font-size:13px;color:#999999;line-height:1.5;">
                                Si ya actualizaste tu pago, podés ignorar este mensaje. ¿Dudas? Contactá a soporte.
                              </p>
                            </td>
                          </tr>
                          <tr>
                            <td style="background:#f9f9f9;padding:16px 32px;border-top:1px solid #eeeeee;">
                              <p style="margin:0;font-size:12px;color:#aaaaaa;">&copy; 2026 LuxuryCloud. Todos los derechos reservados.</p>
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
