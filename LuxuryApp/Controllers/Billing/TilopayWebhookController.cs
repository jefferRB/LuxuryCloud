using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Payments;
using LuxuryApp.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("Webhook")]
    [Route("api/webhooks/tilopay")]
    public class TilopayWebhookController : ControllerBase
    {
        private readonly ILogger<TilopayWebhookController> _logger;
        private readonly SaaSPaymentService _paymentService;
        private readonly OpcionesTilopay _options;
        private readonly IWebHostEnvironment _environment;

        public TilopayWebhookController(
            ILogger<TilopayWebhookController> logger,
            SaaSPaymentService paymentService,
            IOptions<OpcionesTilopay> options,
            IWebHostEnvironment environment)
        {
            _logger = logger;
            _paymentService = paymentService;
            _options = options.Value;
            _environment = environment;
        }

        [HttpPost]
        public async Task<IActionResult> Handle(CancellationToken cancellationToken)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var correlationId = HttpContext.TraceIdentifier;
            var body = await new StreamReader(Request.Body).ReadToEndAsync(cancellationToken);
            var incomingEvent = Request.Query.TryGetValue("event", out var eventValues)
                ? eventValues.ToString()
                : null;

            LogDevelopmentWebhookRequest(body, correlationId, incomingEvent);

            if (string.IsNullOrWhiteSpace(_options.WebhookAccessToken))
            {
                _logger.LogCritical("Tilopay webhook rechazado porque no existe WebhookAccessToken configurado.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (!Request.Query.TryGetValue(_options.WebhookAccessTokenQueryParameter, out var incomingToken) ||
                !SecureEquals(incomingToken.ToString(), _options.WebhookAccessToken))
            {
                _logger.LogWarning(
                    "Tilopay webhook rechazado por token de acceso invalido. TraceIdentifier {TraceIdentifier}. Path {Path}.",
                    correlationId,
                    Request.Path.Value);
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return BadRequest("Payload vacio.");
            }

            try
            {
                var result = await _paymentService.ProcessTilopayWebhookAsync(
                    body,
                    correlationId,
                    incomingEvent,
                    cancellationToken);

                LogDevelopmentWebhookResult(body, correlationId, incomingEvent, result);

                // Línea única de observabilidad por webhook: suficiente para diagnosticar
                // sin abrir la base de datos (correlación, resultado, duración).
                _logger.LogInformation(
                    "Tilopay webhook resumen. CorrelationId {CorrelationId}. EventIdSuffix {EventIdSuffix}. ReferenceSuffix {ReferenceSuffix}. Duplicate {Duplicate}. Processed {Processed}. EstadoPago {EstadoPago}. DurationMs {DurationMs}.",
                    correlationId,
                    SensitiveDataMasker.MaskReference(result.EventId),
                    SensitiveDataMasker.MaskReference(result.Reference),
                    result.IsDuplicate,
                    result.IsProcessed,
                    result.EstadoPago,
                    stopwatch.ElapsedMilliseconds);

                return Ok(new
                {
                    accepted = true,
                    duplicate = result.IsDuplicate,
                    processed = result.IsProcessed
                });
            }
            catch (PaymentWebhookValidationException ex)
            {
                await _paymentService.RegisterRejectedWebhookAsync(
                    PaymentProviderType.Tilopay,
                    body,
                    ex.Message,
                    correlationId,
                    cancellationToken);

                _logger.LogWarning(ex, "Tilopay webhook rechazado por payload invalido.");
                return BadRequest("Payload invalido.");
            }
            catch (PaymentProviderConfigurationException ex)
            {
                _logger.LogCritical(ex, "Tilopay webhook rechazado por configuracion incompleta.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error procesando webhook Tilopay. CorrelationId {CorrelationId}. DurationMs {DurationMs}.",
                    correlationId,
                    stopwatch.ElapsedMilliseconds);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        private void LogDevelopmentWebhookRequest(string body, string correlationId, string? incomingEvent)
        {
            if (!_environment.IsDevelopment())
            {
                return;
            }

            var diagnostics = ExtractDiagnostics(body);

            _logger.LogInformation(
                "Tilopay webhook recibido en Development. TraceIdentifier {TraceIdentifier}. TimestampUtc {TimestampUtc}. Method {Method}. Path {Path}. Event {Event}. RedactedQuery {RedactedQuery}. Headers {Headers}. PayloadBytes {PayloadBytes}. TransactionIdSuffix {TransactionIdSuffix}. OrderIdSuffix {OrderIdSuffix}. AuthSuffix {AuthSuffix}. PlanId {PlanId}. PlanCode {PlanCode}. SubscriberIdSuffix {SubscriberIdSuffix}. MaskedEmail {MaskedEmail}. HasAmount {HasAmount}. Currency {Currency}. Status {Status}. CorrelationTokenSuffix {CorrelationTokenSuffix}.",
                correlationId,
                DateTime.UtcNow,
                Request.Method,
                Request.Path.Value,
                incomingEvent,
                SensitiveDataMasker.RedactQueryString(Request.QueryString.Value),
                JsonSerializer.Serialize(GetSafeHeaders()),
                Encoding.UTF8.GetByteCount(body),
                SensitiveDataMasker.MaskReference(diagnostics.TransactionId),
                SensitiveDataMasker.MaskReference(diagnostics.OrderId),
                SensitiveDataMasker.MaskToken(diagnostics.AuthorizationCode),
                diagnostics.PlanId,
                diagnostics.PlanCode,
                SensitiveDataMasker.MaskReference(diagnostics.SubscriberId),
                SensitiveDataMasker.MaskEmail(diagnostics.Email),
                !string.IsNullOrWhiteSpace(diagnostics.Amount),
                diagnostics.Currency,
                diagnostics.Status,
                SensitiveDataMasker.MaskReference(diagnostics.CorrelationToken));
        }

        private void LogDevelopmentWebhookResult(
            string body,
            string correlationId,
            string? incomingEvent,
            PaymentWebhookProcessingResult result)
        {
            if (!_environment.IsDevelopment())
            {
                return;
            }

            var diagnostics = ExtractDiagnostics(body);

            _logger.LogInformation(
                "Tilopay webhook procesado en Development. TraceIdentifier {TraceIdentifier}. Event {Event}. EventIdSuffix {EventIdSuffix}. ReferenceSuffix {ReferenceSuffix}. Duplicate {Duplicate}. Processed {Processed}. EstadoPago {EstadoPago}. MessagePresent {MessagePresent}. TransactionIdSuffix {TransactionIdSuffix}. OrderIdSuffix {OrderIdSuffix}. AuthSuffix {AuthSuffix}. PlanId {PlanId}. PlanCode {PlanCode}. SubscriberIdSuffix {SubscriberIdSuffix}. MaskedEmail {MaskedEmail}. HasAmount {HasAmount}. Currency {Currency}. Status {Status}. CorrelationTokenSuffix {CorrelationTokenSuffix}.",
                correlationId,
                incomingEvent,
                SensitiveDataMasker.MaskReference(result.EventId),
                SensitiveDataMasker.MaskReference(result.Reference),
                result.IsDuplicate,
                result.IsProcessed,
                result.EstadoPago,
                !string.IsNullOrWhiteSpace(result.Message),
                SensitiveDataMasker.MaskReference(diagnostics.TransactionId),
                SensitiveDataMasker.MaskReference(diagnostics.OrderId),
                SensitiveDataMasker.MaskToken(diagnostics.AuthorizationCode),
                diagnostics.PlanId,
                diagnostics.PlanCode,
                SensitiveDataMasker.MaskReference(diagnostics.SubscriberId),
                SensitiveDataMasker.MaskEmail(diagnostics.Email),
                !string.IsNullOrWhiteSpace(diagnostics.Amount),
                diagnostics.Currency,
                diagnostics.Status,
                SensitiveDataMasker.MaskReference(diagnostics.CorrelationToken));
        }

        private Dictionary<string, string> GetSafeQuery() =>
            Request.Query.ToDictionary(
                pair => pair.Key,
                pair => SensitiveDataMasker.IsSensitiveKey(pair.Key) ? SensitiveDataMasker.Redacted : pair.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> GetSafeHeaders()
        {
            var headerNames = new[]
            {
                "Content-Type",
                "User-Agent",
                "Host",
                "X-Forwarded-For",
                "X-Forwarded-Proto",
                "X-Real-IP",
                "CF-Connecting-IP"
            };

            return headerNames
                .Where(header => Request.Headers.ContainsKey(header))
                .ToDictionary(
                    header => header,
                    header => Request.Headers[header].ToString(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static WebhookDiagnostics ExtractDiagnostics(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return WebhookDiagnostics.Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var statusCode = TryReadFirstString(root, "code", "statusCode", "status_code", "status");
                var statusDescription = TryReadFirstString(root, "codeDescription", "statusDescription", "status_description", "description", "response", "message");
                var status = string.Join(
                    " ",
                    new[] { statusCode, statusDescription }.Where(value => !string.IsNullOrWhiteSpace(value)));

                return new WebhookDiagnostics(
                    TransactionId: TryReadFirstString(root, "transactionId", "transaction_id", "paymentId", "payment_id", "orderId", "order_id", "id_tilopay"),
                    OrderId: TryReadFirstString(root, "orderNumber", "order_number", "providerOrderNumber"),
                    AuthorizationCode: TryReadFirstString(root, "auth", "authorizationCode"),
                    PlanId: TryReadFirstString(root, "id_plan", "idPlan", "recurringPlanId", "repeatPlanId", "planId", "subscriptionPlanId", "plan_id"),
                    PlanCode: TryReadFirstString(root, "lc_plan", "planCode", "plan_code", "subscriptionPlanCode", "subscription_plan_code", "codigoPlan", "codigo_plan"),
                    SubscriberId: TryReadFirstString(root, "subscriberId", "subscriber_id", "subscriptionId", "subscription_id", "suscriptorId", "suscriptor_id", "customerId", "customer_id"),
                    Email: TryReadFirstString(root, "customerEmail", "clientEmail", "email", "correo", "mail"),
                    Amount: TryReadFirstString(root, "amount", "monto", "total"),
                    Currency: TryReadFirstString(root, "currency", "moneda"),
                    Status: string.IsNullOrWhiteSpace(status) ? null : status,
                    CorrelationToken: TryReadFirstString(root, "lc_ref", "correlationToken", "reference", "internalReference"));
            }
            catch (JsonException)
            {
                return WebhookDiagnostics.Empty;
            }
        }

        private static string RedactPayloadForLog(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return string.Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                return JsonSerializer.Serialize(RedactJsonElement(document.RootElement));
            }
            catch (JsonException)
            {
                return "[non-json payload omitted]";
            }
        }

        private static object? RedactJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => IsSensitiveProperty(property.Name)
                        ? "***redacted***"
                        : RedactJsonElement(property.Value)),
                JsonValueKind.Array => element.EnumerateArray().Select(RedactJsonElement).ToArray(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.ToString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        private static string? TryReadFirstString(JsonElement root, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                if (TryFindPropertyValue(root, propertyName, out var value))
                {
                    var normalized = value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.Number => value.ToString(),
                        JsonValueKind.True => bool.TrueString,
                        JsonValueKind.False => bool.FalseString,
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        return normalized;
                    }
                }
            }

            return null;
        }

        private static bool TryFindPropertyValue(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }

                    if (TryFindPropertyValue(property.Value, propertyName, out value))
                    {
                        return true;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindPropertyValue(item, propertyName, out value))
                    {
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static bool IsSensitiveProperty(string propertyName) =>
            SensitiveDataMasker.IsSensitiveKey(propertyName);

        private static bool SecureEquals(string left, string right)
        {
            var leftBytes = Encoding.UTF8.GetBytes(left);
            var rightBytes = Encoding.UTF8.GetBytes(right);

            if (leftBytes.Length != rightBytes.Length)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        private sealed record WebhookDiagnostics(
            string? TransactionId,
            string? OrderId,
            string? AuthorizationCode,
            string? PlanId,
            string? PlanCode,
            string? SubscriberId,
            string? Email,
            string? Amount,
            string? Currency,
            string? Status,
            string? CorrelationToken)
        {
            public static WebhookDiagnostics Empty { get; } = new(
                TransactionId: null,
                OrderId: null,
                AuthorizationCode: null,
                PlanId: null,
                PlanCode: null,
                SubscriberId: null,
                Email: null,
                Amount: null,
                Currency: null,
                Status: null,
                CorrelationToken: null);
        }
    }
}
