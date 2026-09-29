using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LuxuryApp.Services.Common;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Services.WhatsApp
{
    public sealed class MetaWhatsAppClient : IMetaWhatsAppClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _httpClient;
        private readonly IOptionsMonitor<MetaWhatsAppOptions> _options;
        private readonly ILogger<MetaWhatsAppClient> _logger;

        public MetaWhatsAppClient(
            HttpClient httpClient,
            IOptionsMonitor<MetaWhatsAppOptions> options,
            ILogger<MetaWhatsAppClient> logger)
        {
            _httpClient = httpClient;
            _options = options;
            _logger = logger;
        }

        public bool IsValidPhoneNumber(string? phoneNumber) =>
            NormalizePhoneNumber(phoneNumber) is not null;

        /// <summary>
        /// Delega en <see cref="PhoneNumberNormalizer"/>: el algoritmo es el mismo de siempre, pero
        /// vive en un solo lugar para que la identificación de clientes por teléfono use idéntica
        /// normalización que el envío de mensajes.
        /// </summary>
        public string? NormalizePhoneNumber(string? phoneNumber) =>
            PhoneNumberNormalizer.ToE164(phoneNumber, _options.CurrentValue.DefaultCountryCode);

        public Task<MetaWhatsAppSendResult> SendConfirmationTemplateAsync(
            string recipientPhone,
            string customerName,
            string businessName,
            string appointmentDate,
            string appointmentTime,
            string professionalName,
            CancellationToken cancellationToken = default)
        {
            var options = _options.CurrentValue;
            return SendTemplateAsync(
                recipientPhone,
                options.ConfirmationTemplateName,
                [
                    customerName,
                    businessName,
                    appointmentDate,
                    appointmentTime,
                    professionalName
                ],
                cancellationToken);
        }

        public Task<MetaWhatsAppSendResult> SendReminderTemplateAsync(
            string recipientPhone,
            string customerName,
            string businessName,
            string appointmentTime,
            string professionalName,
            CancellationToken cancellationToken = default)
        {
            var options = _options.CurrentValue;
            return SendTemplateAsync(
                recipientPhone,
                options.ReminderTemplateName,
                [
                    customerName,
                    businessName,
                    appointmentTime,
                    professionalName
                ],
                cancellationToken);
        }

        public Task<MetaWhatsAppSendResult> SendCancellationTemplateAsync(
            string recipientPhone,
            WhatsAppCancellationTemplateParameters parameters,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parameters);

            var options = _options.CurrentValue;
            return SendTemplateAsync(
                recipientPhone,
                options.CancellationTemplateName,
                parameters.ToOrderedBodyParameters(),
                cancellationToken);
        }

        public async Task<MetaWhatsAppSendResult> SendTextMessageAsync(
            string recipientPhone,
            string message,
            CancellationToken cancellationToken = default)
        {
            var normalizedPhone = NormalizePhoneNumber(recipientPhone);
            if (normalizedPhone is null)
            {
                return MetaWhatsAppSendResult.Failed("INVALID_PHONE", "Telefono invalido.");
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = ToMetaRecipient(normalizedPhone),
                type = "text",
                text = new
                {
                    preview_url = false,
                    body = message
                }
            };

            return await SendPayloadAsync(payload, cancellationToken);
        }

        public async Task<MetaWhatsAppConfigurationDiagnosticResult> TestConfigurationAsync(
            CancellationToken cancellationToken = default)
        {
            var rawOptions = _options.CurrentValue;
            MetaWhatsAppDiagnosticsLogger.LogEffectiveConfiguration(_logger, rawOptions, "test_configuration_request");

            var options = MetaWhatsAppNormalizedOptions.Create(rawOptions);
            var snapshot = MetaWhatsAppConfigurationSnapshot.Create(rawOptions);

            var validationError = ValidateDiagnosticConfiguration(options);
            if (validationError is not null)
            {
                return new MetaWhatsAppConfigurationDiagnosticResult(
                    Success: false,
                    Configuration: snapshot,
                    PhoneNumberProbe: validationError,
                    WabaPhoneNumbersProbe: null,
                    PhoneNumberBelongsToConfiguredWaba: null);
            }

            var phoneEndpoint = BuildPhoneNumberEndpoint(options);
            var phoneProbe = await ProbePhoneNumberAsync(phoneEndpoint, options.AccessToken, cancellationToken);

            MetaWhatsAppEndpointProbeResult? wabaProbe = null;
            bool? phoneBelongsToConfiguredWaba = null;

            if (!string.IsNullOrWhiteSpace(options.WhatsAppBusinessAccountId))
            {
                var wabaEndpoint = BuildWabaPhoneNumbersEndpoint(options);
                var wabaProbeResponse = await ProbeWabaPhoneNumbersAsync(
                    wabaEndpoint,
                    options.AccessToken,
                    options.PhoneNumberId,
                    cancellationToken);
                wabaProbe = wabaProbeResponse.Probe;
                phoneBelongsToConfiguredWaba = wabaProbeResponse.PhoneNumberBelongsToWaba;
            }

            var success = phoneProbe.Success &&
                          (wabaProbe is null || wabaProbe.Success) &&
                          phoneBelongsToConfiguredWaba != false;

            return new MetaWhatsAppConfigurationDiagnosticResult(
                success,
                snapshot,
                phoneProbe,
                wabaProbe,
                phoneBelongsToConfiguredWaba);
        }

        private async Task<MetaWhatsAppSendResult> SendTemplateAsync(
            string recipientPhone,
            string templateName,
            IReadOnlyList<string> bodyParameters,
            CancellationToken cancellationToken)
        {
            var normalizedPhone = NormalizePhoneNumber(recipientPhone);
            if (normalizedPhone is null)
            {
                return MetaWhatsAppSendResult.Failed("INVALID_PHONE", "Telefono invalido.");
            }

            if (string.IsNullOrWhiteSpace(templateName))
            {
                return MetaWhatsAppSendResult.Failed("MISSING_TEMPLATE", "Template de WhatsApp no configurado.");
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = ToMetaRecipient(normalizedPhone),
                type = "template",
                template = new
                {
                    name = templateName.Trim(),
                    language = new
                    {
                        code = "es"
                    },
                    components = new[]
                    {
                        new
                        {
                            type = "body",
                            parameters = bodyParameters
                                .Select(value => new
                                {
                                    type = "text",
                                    text = value
                                })
                                .ToArray()
                        }
                    }
                }
            };

            return await SendPayloadAsync(payload, cancellationToken);
        }

        private async Task<MetaWhatsAppSendResult> SendPayloadAsync(
            object payload,
            CancellationToken cancellationToken)
        {
            var rawOptions = _options.CurrentValue;
            MetaWhatsAppDiagnosticsLogger.LogEffectiveConfiguration(_logger, rawOptions, "before_send");

            var options = MetaWhatsAppNormalizedOptions.Create(rawOptions);
            if (!options.Enabled)
            {
                return MetaWhatsAppSendResult.Failed("DISABLED", "Meta WhatsApp esta deshabilitado.");
            }

            var endpointValidationError = ValidateSendConfiguration(options);
            if (endpointValidationError is not null)
            {
                return endpointValidationError;
            }

            var endpoint = BuildMessagesEndpoint(options);
            var json = JsonSerializer.Serialize(payload, JsonOptions);

            _logger.LogInformation(
                "Meta WhatsApp request prepared. Endpoint {Endpoint}. AuthorizationScheme {AuthorizationScheme}. AccessTokenPresent {AccessTokenPresent}.",
                endpoint,
                "Bearer",
                !string.IsNullOrWhiteSpace(options.AccessToken));

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var safeBody = Trim(body, 4000);

                if (!response.IsSuccessStatusCode)
                {
                    var error = ExtractMetaError(response.StatusCode, body, response.Headers.WwwAuthenticate.ToString());
                    _logger.LogWarning(
                        "Meta WhatsApp request failed. Endpoint {Endpoint}. StatusCode {StatusCode}. ErrorType {ErrorType}. ErrorCode {ErrorCode}. ErrorSubcode {ErrorSubcode}. FbTraceId {FbTraceId}. ErrorMessage {ErrorMessage}. ResponseBodyLength {ResponseBodyLength}",
                        endpoint,
                        (int)response.StatusCode,
                        error.Type,
                        error.Code,
                        error.Subcode,
                        error.FbTraceId,
                        error.Message,
                        body.Length);

                    return MetaWhatsAppSendResult.Failed(
                        error.Code,
                        error.Message,
                        response.StatusCode,
                        safeBody,
                        error.Type,
                        error.Subcode,
                        error.FbTraceId,
                        error.ShouldRetry,
                        endpoint.ToString());
                }

                var messageId = ExtractMessageId(body);
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    return MetaWhatsAppSendResult.Failed(
                        "MISSING_MESSAGE_ID",
                        "Meta no retorno message id.",
                        response.StatusCode,
                        safeBody,
                        shouldRetry: false,
                        endpoint: endpoint.ToString());
                }

                _logger.LogInformation("Meta WhatsApp accepted message {MetaMessageId}. Endpoint {Endpoint}.", messageId, endpoint);
                return MetaWhatsAppSendResult.Succeeded(messageId, response.StatusCode, safeBody, endpoint.ToString());
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return MetaWhatsAppSendResult.Failed(
                    "TIMEOUT",
                    "Timeout enviando mensaje a Meta WhatsApp.",
                    shouldRetry: true,
                    endpoint: endpoint.ToString());
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "HTTP request error sending message to Meta WhatsApp. Endpoint {Endpoint}.", endpoint);
                return MetaWhatsAppSendResult.Failed(
                    "HTTP_ERROR",
                    Trim(ex.Message, 500),
                    ex.StatusCode,
                    shouldRetry: true,
                    endpoint: endpoint.ToString());
            }

        }

        private async Task<MetaWhatsAppEndpointProbeResult> ProbePhoneNumberAsync(
            Uri endpoint,
            string accessToken,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var safeBody = Trim(body, 4000);

                if (!response.IsSuccessStatusCode)
                {
                    var error = ExtractMetaError(response.StatusCode, body, response.Headers.WwwAuthenticate.ToString());
                    return new MetaWhatsAppEndpointProbeResult(
                        Success: false,
                        Endpoint: endpoint.ToString(),
                        HttpStatus: (int)response.StatusCode,
                        DisplayPhoneNumber: null,
                        VerifiedName: null,
                        ErrorType: error.Type,
                        ErrorCode: error.Code,
                        ErrorSubcode: error.Subcode,
                        ErrorMessage: error.Message,
                        FbTraceId: error.FbTraceId,
                        ResponsePreview: safeBody);
                }

                var metadata = ExtractPhoneNumberMetadata(body);
                return new MetaWhatsAppEndpointProbeResult(
                    Success: true,
                    Endpoint: endpoint.ToString(),
                    HttpStatus: (int)response.StatusCode,
                    DisplayPhoneNumber: metadata.DisplayPhoneNumber,
                    VerifiedName: metadata.VerifiedName,
                    ErrorType: null,
                    ErrorCode: null,
                    ErrorSubcode: null,
                    ErrorMessage: null,
                    FbTraceId: null,
                    ResponsePreview: safeBody);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new MetaWhatsAppEndpointProbeResult(
                    Success: false,
                    Endpoint: endpoint.ToString(),
                    HttpStatus: null,
                    DisplayPhoneNumber: null,
                    VerifiedName: null,
                    ErrorType: null,
                    ErrorCode: "TIMEOUT",
                    ErrorSubcode: null,
                    ErrorMessage: "Timeout validando configuracion de Meta WhatsApp.",
                    FbTraceId: null,
                    ResponsePreview: null);
            }
            catch (HttpRequestException ex)
            {
                return new MetaWhatsAppEndpointProbeResult(
                    Success: false,
                    Endpoint: endpoint.ToString(),
                    HttpStatus: ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null,
                    DisplayPhoneNumber: null,
                    VerifiedName: null,
                    ErrorType: null,
                    ErrorCode: "HTTP_ERROR",
                    ErrorSubcode: null,
                    ErrorMessage: Trim(ex.Message, 500),
                    FbTraceId: null,
                    ResponsePreview: null);
            }
        }

        private async Task<(MetaWhatsAppEndpointProbeResult Probe, bool? PhoneNumberBelongsToWaba)> ProbeWabaPhoneNumbersAsync(
            Uri endpoint,
            string accessToken,
            string expectedPhoneNumberId,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var safeBody = Trim(body, 4000);

                if (!response.IsSuccessStatusCode)
                {
                    var error = ExtractMetaError(response.StatusCode, body, response.Headers.WwwAuthenticate.ToString());
                    return (
                        new MetaWhatsAppEndpointProbeResult(
                            Success: false,
                            Endpoint: endpoint.ToString(),
                            HttpStatus: (int)response.StatusCode,
                            DisplayPhoneNumber: null,
                            VerifiedName: null,
                            ErrorType: error.Type,
                            ErrorCode: error.Code,
                            ErrorSubcode: error.Subcode,
                            ErrorMessage: error.Message,
                            FbTraceId: error.FbTraceId,
                            ResponsePreview: safeBody),
                        null);
                }

                var matchesPhoneNumber = ResponseContainsPhoneNumber(body, expectedPhoneNumberId);
                return (
                    new MetaWhatsAppEndpointProbeResult(
                        Success: true,
                        Endpoint: endpoint.ToString(),
                        HttpStatus: (int)response.StatusCode,
                        DisplayPhoneNumber: null,
                        VerifiedName: null,
                        ErrorType: null,
                        ErrorCode: null,
                        ErrorSubcode: null,
                        ErrorMessage: null,
                        FbTraceId: null,
                        ResponsePreview: safeBody),
                    matchesPhoneNumber);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (
                    new MetaWhatsAppEndpointProbeResult(
                        Success: false,
                        Endpoint: endpoint.ToString(),
                        HttpStatus: null,
                        DisplayPhoneNumber: null,
                        VerifiedName: null,
                        ErrorType: null,
                        ErrorCode: "TIMEOUT",
                        ErrorSubcode: null,
                        ErrorMessage: "Timeout validando WABA de Meta WhatsApp.",
                        FbTraceId: null,
                        ResponsePreview: null),
                    null);
            }
            catch (HttpRequestException ex)
            {
                return (
                    new MetaWhatsAppEndpointProbeResult(
                        Success: false,
                        Endpoint: endpoint.ToString(),
                        HttpStatus: ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null,
                        DisplayPhoneNumber: null,
                        VerifiedName: null,
                        ErrorType: null,
                        ErrorCode: "HTTP_ERROR",
                        ErrorSubcode: null,
                        ErrorMessage: Trim(ex.Message, 500),
                        FbTraceId: null,
                        ResponsePreview: null),
                    null);
            }
        }

        private static MetaWhatsAppEndpointProbeResult? ValidateDiagnosticConfiguration(MetaWhatsAppNormalizedOptions options)
        {
            if (!options.Enabled)
            {
                return new MetaWhatsAppEndpointProbeResult(
                    Success: false,
                    Endpoint: string.Empty,
                    HttpStatus: null,
                    DisplayPhoneNumber: null,
                    VerifiedName: null,
                    ErrorType: null,
                    ErrorCode: "DISABLED",
                    ErrorSubcode: null,
                    ErrorMessage: "Meta WhatsApp esta deshabilitado globalmente.",
                    FbTraceId: null,
                    ResponsePreview: null);
            }

            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            {
                return new MetaWhatsAppEndpointProbeResult(
                    Success: false,
                    Endpoint: options.BaseUrl,
                    HttpStatus: null,
                    DisplayPhoneNumber: null,
                    VerifiedName: null,
                    ErrorType: null,
                    ErrorCode: "INVALID_BASE_URL",
                    ErrorSubcode: null,
                    ErrorMessage: "Meta WhatsApp tiene BaseUrl invalida.",
                    FbTraceId: null,
                    ResponsePreview: null);
            }

            if (string.IsNullOrWhiteSpace(options.GraphApiVersion))
            {
                return new MetaWhatsAppEndpointProbeResult(
                    Success: false,
                    Endpoint: options.BaseUrl,
                    HttpStatus: null,
                    DisplayPhoneNumber: null,
                    VerifiedName: null,
                    ErrorType: null,
                    ErrorCode: "MISSING_GRAPH_VERSION",
                    ErrorSubcode: null,
                    ErrorMessage: "Meta WhatsApp no tiene GraphApiVersion configurado.",
                    FbTraceId: null,
                    ResponsePreview: null);
            }

            if (string.IsNullOrWhiteSpace(options.PhoneNumberId))
            {
                return new MetaWhatsAppEndpointProbeResult(
                    Success: false,
                    Endpoint: options.BaseUrl,
                    HttpStatus: null,
                    DisplayPhoneNumber: null,
                    VerifiedName: null,
                    ErrorType: null,
                    ErrorCode: "MISSING_PHONE_NUMBER_ID",
                    ErrorSubcode: null,
                    ErrorMessage: "Meta WhatsApp no tiene PhoneNumberId configurado.",
                    FbTraceId: null,
                    ResponsePreview: null);
            }

            if (string.IsNullOrWhiteSpace(options.AccessToken))
            {
                return new MetaWhatsAppEndpointProbeResult(
                    Success: false,
                    Endpoint: options.BaseUrl,
                    HttpStatus: null,
                    DisplayPhoneNumber: null,
                    VerifiedName: null,
                    ErrorType: null,
                    ErrorCode: "MISSING_ACCESS_TOKEN",
                    ErrorSubcode: null,
                    ErrorMessage: "Meta WhatsApp no tiene AccessToken configurado.",
                    FbTraceId: null,
                    ResponsePreview: null);
            }

            return null;
        }

        private static MetaWhatsAppSendResult? ValidateSendConfiguration(MetaWhatsAppNormalizedOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.AccessToken) ||
                string.IsNullOrWhiteSpace(options.PhoneNumberId))
            {
                return MetaWhatsAppSendResult.Failed(
                    "MISSING_CONFIGURATION",
                    "Meta WhatsApp no tiene AccessToken o PhoneNumberId configurado.",
                    shouldRetry: false);
            }

            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            {
                return MetaWhatsAppSendResult.Failed(
                    "INVALID_BASE_URL",
                    "Meta WhatsApp tiene BaseUrl invalida.",
                    shouldRetry: false);
            }

            if (string.IsNullOrWhiteSpace(options.GraphApiVersion))
            {
                return MetaWhatsAppSendResult.Failed(
                    "MISSING_GRAPH_VERSION",
                    "Meta WhatsApp no tiene GraphApiVersion configurado.",
                    shouldRetry: false);
            }

            return null;
        }

        private static Uri BuildMessagesEndpoint(MetaWhatsAppNormalizedOptions options) =>
            new($"{options.BaseUrl}/{options.GraphApiVersion}/{options.PhoneNumberId}/messages", UriKind.Absolute);

        private static Uri BuildPhoneNumberEndpoint(MetaWhatsAppNormalizedOptions options) =>
            new($"{options.BaseUrl}/{options.GraphApiVersion}/{options.PhoneNumberId}?fields=id,display_phone_number,verified_name", UriKind.Absolute);

        private static Uri BuildWabaPhoneNumbersEndpoint(MetaWhatsAppNormalizedOptions options) =>
            new($"{options.BaseUrl}/{options.GraphApiVersion}/{options.WhatsAppBusinessAccountId}/phone_numbers?fields=id,display_phone_number,verified_name", UriKind.Absolute);

        private static string ToMetaRecipient(string e164Phone) =>
            e164Phone.TrimStart('+');

        private static string? ExtractMessageId(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("messages", out var messages) ||
                    messages.ValueKind != JsonValueKind.Array ||
                    messages.GetArrayLength() == 0)
                {
                    return null;
                }

                return messages[0].TryGetProperty("id", out var id)
                    ? id.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static (string? DisplayPhoneNumber, string? VerifiedName) ExtractPhoneNumberMetadata(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var displayPhoneNumber = root.TryGetProperty("display_phone_number", out var displayPhoneNumberElement)
                    ? displayPhoneNumberElement.GetString()
                    : null;
                var verifiedName = root.TryGetProperty("verified_name", out var verifiedNameElement)
                    ? verifiedNameElement.GetString()
                    : null;

                return (displayPhoneNumber, verifiedName);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }

        private static bool? ResponseContainsPhoneNumber(string body, string expectedPhoneNumberId)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idElement) &&
                        string.Equals(idElement.GetString(), expectedPhoneNumberId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static MetaWhatsAppApiError ExtractMetaError(
            HttpStatusCode statusCode,
            string body,
            string? wwwAuthenticateHeader)
        {
            string code = "META_ERROR";
            string message = string.Empty;
            int? subcode = null;
            string? type = null;
            string? fbTraceId = null;

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.TryGetProperty("code", out var codeElement))
                    {
                        code = codeElement.ToString();
                    }

                    if (error.TryGetProperty("error_subcode", out var subcodeElement) &&
                        subcodeElement.TryGetInt32(out var parsedSubcode))
                    {
                        subcode = parsedSubcode;
                    }

                    if (error.TryGetProperty("type", out var typeElement))
                    {
                        type = typeElement.GetString();
                    }

                    if (error.TryGetProperty("message", out var messageElement))
                    {
                        message = messageElement.GetString() ?? string.Empty;
                    }

                    if (error.TryGetProperty("fbtrace_id", out var fbTraceIdElement))
                    {
                        fbTraceId = fbTraceIdElement.GetString();
                    }
                }
            }
            catch (JsonException)
            {
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                message = !string.IsNullOrWhiteSpace(wwwAuthenticateHeader)
                    ? wwwAuthenticateHeader
                    : Trim(body, 500);
            }

            code = string.IsNullOrWhiteSpace(code) ? "META_ERROR" : code;
            message = Trim(message, 500);

            var isAuthenticationError =
                statusCode == HttpStatusCode.Unauthorized ||
                statusCode == HttpStatusCode.Forbidden ||
                string.Equals(type, "OAuthException", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "190", StringComparison.Ordinal) ||
                message.Contains("access token", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("invalid_token", StringComparison.OrdinalIgnoreCase);

            var shouldRetry =
                !isAuthenticationError &&
                (statusCode == HttpStatusCode.RequestTimeout ||
                 statusCode == HttpStatusCode.TooManyRequests ||
                 (int)statusCode >= 500);

            return new MetaWhatsAppApiError(
                Code: code,
                Message: FormatMetaErrorMessage(statusCode, type, code, subcode, message, fbTraceId),
                Subcode: subcode,
                Type: type,
                FbTraceId: fbTraceId,
                ResponsePreview: Trim(body, 4000),
                IsAuthenticationError: isAuthenticationError,
                ShouldRetry: shouldRetry);
        }

        private static string FormatMetaErrorMessage(
            HttpStatusCode statusCode,
            string? type,
            string? code,
            int? subcode,
            string? message,
            string? fbTraceId)
        {
            var parts = new List<string>
            {
                $"Meta API error HTTP {(int)statusCode}"
            };

            if (!string.IsNullOrWhiteSpace(type))
            {
                parts.Add($"type={type}");
            }

            if (!string.IsNullOrWhiteSpace(code))
            {
                parts.Add($"code={code}");
            }

            if (subcode.HasValue)
            {
                parts.Add($"subcode={subcode.Value}");
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                parts.Add($"message={message}");
            }

            if (!string.IsNullOrWhiteSpace(fbTraceId))
            {
                parts.Add($"fbtrace_id={fbTraceId}");
            }

            return Trim(string.Join(", ", parts), 1000);
        }

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
