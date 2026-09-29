using System.Net;
using System.Net.Http;
using System.Text;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class MetaWhatsAppClientTests
    {
        [Fact]
        public async Task SendTextMessageAsync_WhenMetaReturnsOAuthError_ShouldExposeDetailedSafeError()
        {
            var handler = new StubHttpMessageHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        "{\"error\":{\"message\":\"Error validating access token: Session has expired.\",\"type\":\"OAuthException\",\"code\":190,\"error_subcode\":463,\"fbtrace_id\":\"trace-123\"}}",
                        Encoding.UTF8,
                        "application/json")
                };
                response.Headers.TryAddWithoutValidation("WWW-Authenticate", "OAuth invalid_token");
                return response;
            });
            using var httpClient = new HttpClient(handler);
            var client = new MetaWhatsAppClient(
                httpClient,
                new StaticOptionsMonitor<MetaWhatsAppOptions>(CreateEnabledOptions()),
                NullLogger<MetaWhatsAppClient>.Instance);

            var result = await client.SendTextMessageAsync("88889999", "Hola");

            Assert.False(result.Success);
            Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
            Assert.Equal("190", result.ErrorCode);
            Assert.Equal("OAuthException", result.ErrorType);
            Assert.Equal(463, result.ErrorSubcode);
            Assert.Equal("trace-123", result.FbTraceId);
            Assert.False(result.ShouldRetry);
            Assert.Contains("HTTP 401", result.ErrorMessage);
            Assert.Contains("type=OAuthException", result.ErrorMessage);
            Assert.Contains("code=190", result.ErrorMessage);
            Assert.Contains("subcode=463", result.ErrorMessage);
            Assert.Contains("fbtrace_id=trace-123", result.ErrorMessage);
        }

        [Fact]
        public async Task TestConfigurationAsync_WhenPhoneNumberBelongsToConfiguredWaba_ShouldReturnSuccess()
        {
            var handler = new StubHttpMessageHandler(request =>
            {
                var absolutePath = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (absolutePath.EndsWith("/1000000000000001", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"id\":\"1000000000000001\",\"display_phone_number\":\"+506 8888-9999\",\"verified_name\":\"LuxuryCloud\"}",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"id\":\"1000000000000001\",\"display_phone_number\":\"+506 8888-9999\",\"verified_name\":\"LuxuryCloud\"}]}",
                        Encoding.UTF8,
                        "application/json")
                };
            });
            using var httpClient = new HttpClient(handler);
            var client = new MetaWhatsAppClient(
                httpClient,
                new StaticOptionsMonitor<MetaWhatsAppOptions>(CreateEnabledOptions()),
                NullLogger<MetaWhatsAppClient>.Instance);

            var result = await client.TestConfigurationAsync();

            Assert.True(result.Success);
            Assert.True(result.PhoneNumberProbe.Success);
            Assert.NotNull(result.WabaPhoneNumbersProbe);
            Assert.True(result.WabaPhoneNumbersProbe!.Success);
            Assert.True(result.PhoneNumberBelongsToConfiguredWaba);
            Assert.Equal("LuxuryCloud", result.PhoneNumberProbe.VerifiedName);
        }

        private static MetaWhatsAppOptions CreateEnabledOptions() =>
            new()
            {
                Enabled = true,
                GraphApiVersion = "v25.0",
                BaseUrl = "https://graph.facebook.com",
                PhoneNumberId = "1000000000000001",
                WhatsAppBusinessAccountId = "2000000000000002",
                AccessToken = "EAATEST000000000000000000000",
                AppSecret = "00000000000000000000000000000000",
                DefaultCountryCode = "506",
                ConfirmationTemplateName = "luxurycloud_confirmacion_cita_v1",
                ReminderTemplateName = "luxurycloud_recordatorio_cita_3h_v1",
                RequestTimeoutSeconds = 15,
                SendConfirmationOnCreate = true,
                SendReminderBeforeAppointment = true
            };

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(_handler(request));
        }
    }
}
