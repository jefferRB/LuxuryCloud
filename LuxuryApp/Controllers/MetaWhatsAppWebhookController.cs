using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("Webhook")]
    [Route("api/webhooks/meta-whatsapp")]
    public sealed class MetaWhatsAppWebhookController : ControllerBase
    {
        private readonly IOptionsMonitor<MetaWhatsAppOptions> _options;
        private readonly ICalendarWhatsAppNotificationService _notificationService;
        private readonly IWhatsAppInboundAutoReplyService _autoReplyService;
        private readonly ILogger<MetaWhatsAppWebhookController> _logger;

        public MetaWhatsAppWebhookController(
            IOptionsMonitor<MetaWhatsAppOptions> options,
            ICalendarWhatsAppNotificationService notificationService,
            IWhatsAppInboundAutoReplyService autoReplyService,
            ILogger<MetaWhatsAppWebhookController> logger)
        {
            _options = options;
            _notificationService = notificationService;
            _autoReplyService = autoReplyService;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Verify()
        {
            var options = _options.CurrentValue;
            if (string.IsNullOrWhiteSpace(options.WebhookVerifyToken))
            {
                _logger.LogCritical("Meta WhatsApp webhook verify token no configurado.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var mode = Request.Query["hub.mode"].ToString();
            var verifyToken = Request.Query["hub.verify_token"].ToString();
            var challenge = Request.Query["hub.challenge"].ToString();

            if (string.Equals(mode, "subscribe", StringComparison.Ordinal) &&
                SecureEquals(verifyToken, options.WebhookVerifyToken))
            {
                return Content(challenge, "text/plain", Encoding.UTF8);
            }

            return StatusCode(StatusCodes.Status403Forbidden);
        }

        [HttpPost]
        public async Task<IActionResult> Receive(CancellationToken cancellationToken)
        {
            var options = _options.CurrentValue;
            if (string.IsNullOrWhiteSpace(options.AppSecret))
            {
                _logger.LogCritical("Meta WhatsApp webhook app secret no configurado.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            using var memoryStream = new MemoryStream();
            await Request.Body.CopyToAsync(memoryStream, cancellationToken);
            var bodyBytes = memoryStream.ToArray();

            if (!ValidateSignature(bodyBytes, options.AppSecret, Request.Headers["X-Hub-Signature-256"].ToString()))
            {
                _logger.LogWarning("Meta WhatsApp webhook rechazado por firma invalida.");
                return Unauthorized();
            }

            if (bodyBytes.Length == 0)
            {
                return Ok(new { accepted = true });
            }

            try
            {
                using var document = JsonDocument.Parse(bodyBytes);
                var root = document.RootElement;

                // Tres etapas independientes sobre el mismo payload: que una falle no debe dejar
                // sin correr a las otras. Meta recibe 200 igual y no reintenta.
                await RunStageAsync(
                    "respuestas de citas",
                    () => _notificationService.ProcessInboundReplyAsync(root, cancellationToken));

                await RunStageAsync(
                    "estados de mensajes",
                    () => _notificationService.ProcessStatusUpdateAsync(root, cancellationToken));

                await RunStageAsync(
                    "respuesta automatica",
                    () => _autoReplyService.ProcessInboundMessagesAsync(root, cancellationToken));

                return Ok(new { accepted = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Meta WhatsApp webhook con JSON invalido.");
                return BadRequest("Payload invalido.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando webhook Meta WhatsApp.");
                return Ok(new { accepted = true, processed = false });
            }
        }

        private async Task RunStageAsync(string stage, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando la etapa {Stage} del webhook Meta WhatsApp.", stage);
            }
        }

        private static bool ValidateSignature(byte[] bodyBytes, string appSecret, string signatureHeader)
        {
            if (string.IsNullOrWhiteSpace(signatureHeader) ||
                !signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
            var hash = hmac.ComputeHash(bodyBytes);
            var expected = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();

            var expectedBytes = Encoding.ASCII.GetBytes(expected);
            var actualBytes = Encoding.ASCII.GetBytes(signatureHeader.ToLowerInvariant());

            return expectedBytes.Length == actualBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        private static bool SecureEquals(string left, string right)
        {
            var leftBytes = Encoding.UTF8.GetBytes(left);
            var rightBytes = Encoding.UTF8.GetBytes(right);

            return leftBytes.Length == rightBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
    }
}
