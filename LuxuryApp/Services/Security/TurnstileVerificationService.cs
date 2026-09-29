using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Services.Security
{
    public sealed class TurnstileVerificationService
    {
        private readonly HttpClient _httpClient;
        private readonly IOptionsMonitor<RegistrationSecurityOptions> _options;
        private readonly ILogger<TurnstileVerificationService> _logger;

        public TurnstileVerificationService(
            HttpClient httpClient,
            IOptionsMonitor<RegistrationSecurityOptions> options,
            ILogger<TurnstileVerificationService> logger)
        {
            _httpClient = httpClient;
            _options = options;
            _logger = logger;
        }

        public bool IsEnabled => _options.CurrentValue.Turnstile.Enabled;

        public string SiteKey => _options.CurrentValue.Turnstile.SiteKey;

        public string ResponseFieldName => string.IsNullOrWhiteSpace(_options.CurrentValue.Turnstile.ResponseFieldName)
            ? "cf-turnstile-response"
            : _options.CurrentValue.Turnstile.ResponseFieldName.Trim();

        public async Task<bool> VerifyAsync(
            string? token,
            string? remoteIp,
            CancellationToken cancellationToken = default)
        {
            var turnstile = _options.CurrentValue.Turnstile;
            if (!turnstile.Enabled)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(turnstile.SecretKey))
            {
                _logger.LogCritical("Turnstile habilitado sin RegistrationSecurity:Turnstile:SecretKey.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var form = new Dictionary<string, string>
            {
                ["secret"] = turnstile.SecretKey,
                ["response"] = token
            };

            if (!string.IsNullOrWhiteSpace(remoteIp))
            {
                form["remoteip"] = remoteIp;
            }

            using var response = await _httpClient.PostAsync(
                "turnstile/v0/siteverify",
                new FormUrlEncodedContent(form),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Turnstile respondio HTTP {StatusCode}.",
                    (int)response.StatusCode);
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync<TurnstileResponse>(
                stream,
                cancellationToken: cancellationToken);

            return result?.Success == true;
        }

        private sealed class TurnstileResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; set; }
        }
    }
}
