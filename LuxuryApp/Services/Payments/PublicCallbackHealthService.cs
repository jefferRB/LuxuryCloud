using LuxuryApp.Services.Security;

namespace LuxuryApp.Services.Payments
{
    public class PublicCallbackHealthService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PublicCallbackHealthService> _logger;

        public PublicCallbackHealthService(
            HttpClient httpClient,
            ILogger<PublicCallbackHealthService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task EnsureReachableAsync(
            string healthUrl,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(healthUrl))
            {
                throw new InvalidOperationException("La URL publica de validacion de callbacks es obligatoria.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, healthUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                _logger.LogError(
                    "La URL publica de callbacks no respondio correctamente. Url {HealthUrl}. Status {StatusCode}. BodyLength {BodyLength}",
                    SensitiveDataMasker.RedactUrl(healthUrl),
                    response.StatusCode,
                    body.Length);

                throw new InvalidOperationException(
                    $"La URL publica de callbacks no esta accesible ({(int)response.StatusCode}). Corrige el tunel o proxy antes de iniciar un cobro real.");
            }

            _logger.LogInformation(
                "La URL publica de callbacks respondio correctamente. Url {HealthUrl}",
                SensitiveDataMasker.RedactUrl(healthUrl));
        }
    }
}
