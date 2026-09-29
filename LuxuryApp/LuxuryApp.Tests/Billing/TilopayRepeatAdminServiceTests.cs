using System.Net;
using System.Text;
using System.Text.Json;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Tilopay;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Prueba el cliente admin REAL contra respuestas JSON simuladas: parseo tolerante de
    /// id/id_suscriptor y la lógica de resolución 0/1/N por email.
    /// </summary>
    public class TilopayRepeatAdminServiceTests
    {
        [Fact]
        public async Task GetSuscriptorRepeat_ParsesIdSuscriptorField()
        {
            var service = CreateService(
                """[ { "id_suscriptor": "900830", "email": "buyer1@example.com", "status": "1" } ]""");

            var subscribers = await service.GetSuscriptorRepeatAsync(7101);

            var subscriber = Assert.Single(subscribers);
            Assert.Equal("900830", subscriber.SubscriberId);
            Assert.Equal("buyer1@example.com", subscriber.Email);
        }

        [Fact]
        public async Task GetSuscriptorRepeat_ParsesPlainIdField()
        {
            var service = CreateService(
                """{ "suscriptores": [ { "id": 991122, "correo": "owner@test.local" } ] }""");

            var subscribers = await service.GetSuscriptorRepeatAsync(7101);

            var subscriber = Assert.Single(subscribers);
            Assert.Equal("991122", subscriber.SubscriberId);
            Assert.Equal("owner@test.local", subscriber.Email);
        }

        [Fact]
        public async Task ResolveSubscriber_SingleMatchByEmail_ReturnsFound()
        {
            var service = CreateService(
                """
                [
                  { "id_suscriptor": "111", "email": "other@test.local" },
                  { "id_suscriptor": "900830", "email": "Buyer1@example.com" }
                ]
                """);

            var result = await service.ResolveSubscriberAsync(7101, "buyer1@example.com");

            Assert.Equal(SubscriberResolutionStatus.Found, result.Status);
            Assert.Equal("900830", result.Subscriber!.SubscriberId);
        }

        [Fact]
        public async Task ResolveSubscriber_NoMatch_ReturnsNotFound()
        {
            var service = CreateService("""[ { "id_suscriptor": "111", "email": "other@test.local" } ]""");

            var result = await service.ResolveSubscriberAsync(7101, "buyer1@example.com");

            Assert.Equal(SubscriberResolutionStatus.NotFound, result.Status);
        }

        [Fact]
        public async Task ResolveSubscriber_MultipleMatches_ReturnsAmbiguous()
        {
            var service = CreateService(
                """
                [
                  { "id_suscriptor": "111", "email": "dup@test.local" },
                  { "id_suscriptor": "222", "email": "dup@test.local" }
                ]
                """);

            var result = await service.ResolveSubscriberAsync(7101, "dup@test.local");

            Assert.Equal(SubscriberResolutionStatus.Ambiguous, result.Status);
            Assert.Equal(2, result.MatchCount);
        }

        [Fact]
        public async Task ResolveSubscriber_MultipleButSingleActive_ResolvesToActive()
        {
            var service = CreateService(
                """
                [
                  { "id_suscriptor": "111", "email": "dup@test.local", "status": "4" },
                  { "id_suscriptor": "222", "email": "dup@test.local", "status": "1" }
                ]
                """);

            var result = await service.ResolveSubscriberAsync(7101, "dup@test.local");

            // El estado 4 (eliminado) se descarta; queda un único activo.
            Assert.Equal(SubscriberResolutionStatus.Found, result.Status);
            Assert.Equal("222", result.Subscriber!.SubscriberId);
        }

        [Fact]
        public async Task GetRecurrentUrl_ReturnsUrl()
        {
            // Contrato oficial: la URL de actualización viene en url_renew (no en un alias "url").
            var service = CreateService("""{ "type": "success", "url_renew": "https://tp.cr/l/recurrent-link" }""");

            var result = await service.GetRecurrentUrlAsync(7101, "owner@test.local");

            Assert.True(result.Succeeded);
            Assert.Equal("https://tp.cr/l/recurrent-link", result.Url);
        }

        [Fact]
        public async Task Disabled_Throws()
        {
            var service = CreateService("[]", enabled: false);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSuscriptorRepeatAsync(7101));
        }

        // ── Contrato del proveedor (forma de las respuestas de TiloPay) ──

        // Respuesta con el formato de getSuscriptorRepeat (id numérico, array bajo "suscriptor", status Active).
        private const string RealSuscriptorResponse =
            """
            {
              "type": "success",
              "message": "ok",
              "suscriptor": [
                {
                  "id": 900830,
                  "name": "Alex",
                  "lastname": "Example",
                  "email": "buyer1@example.com",
                  "modality": "LC_M_01",
                  "amount": "8000.00",
                  "expire": "2026-08-07",
                  "coupon": "",
                  "status": "Active",
                  "create": "2026-07-07 19:26:00"
                }
              ]
            }
            """;

        [Fact]
        public async Task GetSuscriptorRepeat_SendsExactContract_KeyAndIdAsString()
        {
            var handler = new StubHandler(RealSuscriptorResponse);
            var service = CreateService(handler);

            await service.GetSuscriptorRepeatAsync(7101);

            var request = handler.LastByPath("getSuscriptorRepeat");
            Assert.NotNull(request);
            Assert.Equal("application/json", request!.ContentType);
            Assert.Equal("Bearer", request.AuthScheme);
            Assert.Equal("fake-token", request.AuthParameter);

            using var doc = JsonDocument.Parse(request.Body!);
            var root = doc.RootElement;
            Assert.Equal("k", root.GetProperty("key").GetString());              // key presente = ApiKey
            Assert.Equal(JsonValueKind.String, root.GetProperty("id").ValueKind); // id COMO STRING
            Assert.Equal("7101", root.GetProperty("id").GetString());
            Assert.False(root.TryGetProperty("id_plan", out _));                  // NO id_plan
            Assert.False(root.TryGetProperty("plan_id", out _));                  // NO plan_id
        }

        // ── recurrentUrl: contrato OFICIAL del body (soporte TiloPay 2026) ──
        // POST /api/v1/recurrentUrl, JSON { key, id, email }. El campo del plan recurrente es "id"
        // (STRING). NO se envía id_plan ni plan_id (no oficiales) ni id_suscriptor (no requerido).
        [Fact]
        public async Task GetRecurrentUrl_SendsId_NotIdPlanNorPlanId_AsJson()
        {
            var handler = new StubHandler(RealSuscriptorResponse);
            handler.SetResponse(
                "recurrentUrl",
                HttpStatusCode.OK,
                """{ "type": "success", "url_register": "https://tp.cr/l/reg", "url_renew": "https://tp.cr/l/renew" }""");
            var service = CreateService(handler);

            var result = await service.GetRecurrentUrlAsync(7102, "buyer3@example.com");

            Assert.True(result.Succeeded);
            Assert.Equal("https://tp.cr/l/renew", result.Url); // url_renew, no url_register

            var request = handler.LastByPath("recurrentUrl");
            Assert.NotNull(request);
            Assert.Equal("application/json", request!.ContentType); // Content-Type application/json

            using var doc = JsonDocument.Parse(request.Body!);
            var root = doc.RootElement;
            Assert.Equal("k", root.GetProperty("key").GetString());
            Assert.Equal(JsonValueKind.String, root.GetProperty("id").ValueKind); // "id" STRING, no int
            Assert.Equal("7102", root.GetProperty("id").GetString());
            Assert.False(root.TryGetProperty("id_plan", out _));       // NO id_plan
            Assert.False(root.TryGetProperty("plan_id", out _));       // NO plan_id
            Assert.False(root.TryGetProperty("id_suscriptor", out _)); // NO id_suscriptor
            Assert.Equal("buyer3@example.com", root.GetProperty("email").GetString());
        }

        [Fact]
        public async Task GetRecurrentUrl_OnlyRegisterUrlAvailable_FailsInsteadOfCreatingDuplicate()
        {
            var handler = new StubHandler(RealSuscriptorResponse);
            // Sin url_renew: registrar sería crear un suscriptor NUEVO (doble cobro), no renovar.
            handler.SetResponse(
                "recurrentUrl",
                HttpStatusCode.OK,
                """{ "type": "success", "url_register": "https://tp.cr/l/reg", "url_renew": "" }""");
            var service = CreateService(handler);

            var result = await service.GetRecurrentUrlAsync(7102, "buyer3@example.com");

            Assert.False(result.Succeeded);
            Assert.Null(result.Url);
        }

        [Fact]
        public async Task GetRecurrentUrl_WhenProviderRejects_FailsWithoutThrowing()
        {
            var handler = new StubHandler(RealSuscriptorResponse);
            handler.SetResponse(
                "recurrentUrl",
                HttpStatusCode.OK,
                """{ "type": "402", "message": "The id plan parameter is required", "url_register": "", "url_renew": "" }""");
            var service = CreateService(handler);

            var result = await service.GetRecurrentUrlAsync(7102, "buyer3@example.com");

            // Sin URL utilizable no se inventa una: el checkout decide qué hacer con el fallo.
            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task GetRecurrentUrl_Succeeds_ReportsIdContract()
        {
            var handler = new StubHandler(RealSuscriptorResponse);
            handler.SetResponse(
                "recurrentUrl",
                HttpStatusCode.OK,
                """{ "type": "success", "url_renew": "https://tp.cr/l/renew" }""");
            var service = CreateService(handler);

            var result = await service.GetRecurrentUrlAsync(7102, "buyer3@example.com");

            Assert.True(result.Succeeded);
            Assert.Equal("id", result.Contract);
            Assert.Single(handler.AllByPath("recurrentUrl"));
        }

        [Fact]
        public async Task GetRecurrentUrl_ProviderError_SingleAttempt_NoFallback()
        {
            var handler = new StubHandler(RealSuscriptorResponse);
            // Contrato oficial "id": NUNCA hay reintento con campos no oficiales (id_plan/plan_id).
            handler.SetResponse(
                "recurrentUrl",
                HttpStatusCode.PaymentRequired,
                """{ "type": "402", "message": "insufficient funds" }""");
            var service = CreateService(handler);

            var result = await service.GetRecurrentUrlAsync(7102, "buyer3@example.com");

            Assert.False(result.Succeeded);
            Assert.Single(handler.AllByPath("recurrentUrl")); // un solo intento, sin fallback
        }

        // ── recurrentUrl: selección de campo (url_renew / url_register) + diagnóstico ──

        [Fact]
        public async Task GetRecurrentUrl_DefaultPrefersUrlRenew_EvenIfRegisterPresent()
        {
            var handler = new StubHandler(RealSuscriptorResponse);
            handler.SetResponse(
                "recurrentUrl",
                HttpStatusCode.OK,
                """{ "type": "success", "url_register": "https://tp.cr/l/reg", "url_renew": "https://tp.cr/l/renew" }""");
            var service = CreateService(handler); // default url_renew

            var result = await service.GetRecurrentUrlAsync(7102, "buyer3@example.com");

            Assert.True(result.Succeeded);
            Assert.Equal("https://tp.cr/l/renew", result.Url);
            Assert.Equal("url_renew", result.RecurrentDiagnostics!.SelectedField);
            Assert.True(result.RecurrentDiagnostics.HasUrlRenew);
            Assert.True(result.RecurrentDiagnostics.HasUrlRegister);
            Assert.Equal(200, result.RecurrentDiagnostics.HttpStatus);
        }

        [Fact]
        public async Task GetRecurrentUrl_PreferredUrlRegister_SelectsRegister()
        {
            var handler = new StubHandler(RealSuscriptorResponse);
            handler.SetResponse(
                "recurrentUrl",
                HttpStatusCode.OK,
                """{ "type": "success", "url_register": "https://tp.cr/l/reg", "url_renew": "https://tp.cr/l/renew" }""");
            var service = CreateService(handler, preferredField: "url_register"); // modo controlado

            var result = await service.GetRecurrentUrlAsync(7102, "buyer3@example.com");

            Assert.True(result.Succeeded);
            Assert.Equal("https://tp.cr/l/reg", result.Url);
            Assert.Equal("url_register", result.RecurrentDiagnostics!.SelectedField);
        }

        [Fact]
        public async Task GetRecurrentUrl_NeverLogsFullUrl()
        {
            const string secretToken = "SUPERSECRETTOKEN123456789";
            var handler = new StubHandler(RealSuscriptorResponse);
            handler.SetResponse(
                "recurrentUrl",
                HttpStatusCode.OK,
                $$"""{ "type": "success", "url_renew": "https://tp.cr/l/{{secretToken}}" }""");
            var logger = new CapturingLogger();
            var service = CreateService(handler, logger);

            var result = await service.GetRecurrentUrlAsync(7102, "buyer3@example.com");

            Assert.True(result.Succeeded);
            var logged = string.Join("\n", logger.Messages);
            Assert.DoesNotContain(secretToken, logged, StringComparison.Ordinal); // token NUNCA en logs
            Assert.Contains("tp.cr", logged, StringComparison.Ordinal);           // host sí, sanitizado
        }

        // ── Estado del suscriptor: la API de TiloPay usa "Delete" (singular) ──
        [Fact]
        public async Task Resolve_SubscriberWithRealDeleteStatus_IsNotReusedAsExisting()
        {
            var service = CreateService(DeletedSuscriptorResponse);

            var resolution = await service.ResolveSubscriberAsync(7102, "buyer3@example.com");

            // Un suscriptor eliminado NO es un match reutilizable: el plan está libre.
            Assert.Equal(SubscriberResolutionStatus.NotFound, resolution.Status);
        }

        [Fact]
        public async Task AssessTargetSubscribers_RealDeleteStatus_IsFreeAndReportsInactive()
        {
            var service = CreateService(DeletedSuscriptorResponse);

            var assessment = await service.AssessTargetSubscribersAsync(7102, "buyer3@example.com");

            Assert.Equal(TargetSubscriberVerdict.Free, assessment.Verdict);
            Assert.Empty(assessment.Active);
            Assert.Single(assessment.Inactive);
            Assert.Equal("900117", assessment.Inactive[0].SubscriberId);
        }

        [Fact]
        public async Task AssessTargetSubscribers_OtherEmail_IsIgnored()
        {
            var service = CreateService(DeletedSuscriptorResponse);

            var assessment = await service.AssessTargetSubscribersAsync(7102, "otro@example.com");

            Assert.Equal(TargetSubscriberVerdict.Free, assessment.Verdict);
            Assert.Empty(assessment.Inactive); // No es de este cliente: ni siquiera se reporta.
        }

        /// <summary>Respuesta con el formato de getSuscriptorRepeat para un suscriptor dado de baja tras un upgrade.</summary>
        private const string DeletedSuscriptorResponse =
            """
            {
              "type": "success",
              "message": "ok",
              "suscriptor": [
                {
                  "id": 900117,
                  "name": "Alex",
                  "lastname": "Example",
                  "email": "buyer3@example.com",
                  "modality": "LC_M_02",
                  "amount": "15000.00",
                  "expire": "2026-08-15",
                  "coupon": "",
                  "status": "Delete",
                  "create": "2026-07-15 20:55:00"
                }
              ]
            }
            """;

        [Fact]
        public async Task GetSuscriptorRepeat_ParsesRealSuscriptorArray()
        {
            var service = CreateService(new StubHandler(RealSuscriptorResponse));

            var subscribers = await service.GetSuscriptorRepeatAsync(7101);

            var subscriber = Assert.Single(subscribers);
            Assert.Equal("900830", subscriber.SubscriberId);           // item.id
            Assert.Equal("buyer1@example.com", subscriber.Email);
            Assert.Equal("Active", subscriber.Status);
        }

        [Fact]
        public async Task ResolveSubscriber_RealResponse_FindsEmailCaseInsensitive()
        {
            var service = CreateService(new StubHandler(RealSuscriptorResponse));

            // El correo consultado en mayúsculas distintas debe resolver igual (case-insensitive).
            var result = await service.ResolveSubscriberAsync(7101, "Buyer1@EXAMPLE.com");

            Assert.Equal(SubscriberResolutionStatus.Found, result.Status);
            Assert.Equal("900830", result.Subscriber!.SubscriberId);
        }

        [Fact]
        public async Task GetSuscriptorRepeat_On400_LogsSanitizedBody_AndResolveReturnsError()
        {
            // TiloPay responde 400 con un body que incluye datos sensibles: no deben loguearse en claro.
            const string errorBody =
                """{ "type": "error", "message": "campo id requerido", "email": "buyer1@example.com", "key": "SECRET-KEY" }""";
            var handler = new StubHandler(defaultBody: "[]");
            handler.SetResponse("getSuscriptorRepeat", HttpStatusCode.BadRequest, errorBody);

            var logger = new CapturingLogger();
            var service = CreateService(handler, logger);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSuscriptorRepeatAsync(7101));

            var logged = string.Join("\n", logger.Messages);
            Assert.Contains("campo id requerido", logged, StringComparison.Ordinal); // mensaje útil sí
            Assert.DoesNotContain("SECRET-KEY", logged, StringComparison.Ordinal);   // key redactada
            Assert.DoesNotContain("buyer1@example.com", logged, StringComparison.Ordinal); // email redactado

            // Resolución sobre un 400 no lanza hacia el caller: devuelve Error (falla-cerrado aguas arriba).
            var resolution = await service.ResolveSubscriberAsync(7101, "buyer1@example.com");
            Assert.Equal(SubscriberResolutionStatus.Error, resolution.Status);
        }

        private static TilopayRepeatAdminService CreateService(string subscribersJson, bool enabled = true) =>
            CreateService(new StubHandler(subscribersJson), enabled: enabled);

        private static TilopayRepeatAdminService CreateService(
            StubHandler handler,
            ILogger<TilopayRepeatAdminService>? logger = null,
            bool enabled = true,
            string preferredField = "url_renew")
        {
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://app.tilopay.com/") };
            var cache = new MemoryCache(new MemoryCacheOptions());

            return new TilopayRepeatAdminService(
                httpClient,
                cache,
                Options.Create(new OpcionesTilopay
                {
                    ApiUser = "u",
                    ApiPassword = "p",
                    ApiKey = "k",
                    BaseUrl = "https://app.tilopay.com/"
                }),
                Options.Create(new OpcionesTilopayRepeatAdmin
                {
                    Enabled = enabled,
                    ResolveRetryCount = 0,
                    RecurrentUrlPreferredField = preferredField
                }),
                logger ?? NullLogger<TilopayRepeatAdminService>.Instance);
        }

        internal sealed record CapturedRequest(string? Body, string? ContentType, string? AuthScheme, string? AuthParameter);

        /// <summary>
        /// Responde login con un token; para otras rutas devuelve el body por defecto o uno
        /// configurado por path (status + body). Captura cada request para aserciones de contrato.
        /// </summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly string _defaultBody;
            private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _byPath = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _sequences = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<(string Path, CapturedRequest Request)> _captured = new();

            public StubHandler(string defaultBody) => _defaultBody = defaultBody;

            public void SetResponse(string pathContains, HttpStatusCode status, string body) =>
                _byPath[pathContains] = (status, body);

            /// <summary>Respuestas en orden para reintentos del mismo path (p.ej. primary + fallback de recurrentUrl).</summary>
            public void SetResponseSequence(string pathContains, params (HttpStatusCode Status, string Body)[] responses) =>
                _sequences[pathContains] = new Queue<(HttpStatusCode, string)>(responses);

            public CapturedRequest? LastByPath(string pathContains) =>
                _captured.LastOrDefault(entry => entry.Path.Contains(pathContains, StringComparison.OrdinalIgnoreCase)).Request;

            public IReadOnlyList<CapturedRequest> AllByPath(string pathContains) =>
                _captured.Where(entry => entry.Path.Contains(pathContains, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Request).ToList();

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var contentType = request.Content?.Headers.ContentType?.MediaType;

                _captured.Add((path, new CapturedRequest(
                    body,
                    contentType,
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter)));

                if (path.EndsWith("login", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(HttpStatusCode.OK, """{ "access_token": "fake-token", "expires_in": 3600 }""");
                }

                foreach (var (pathContains, queue) in _sequences)
                {
                    if (path.Contains(pathContains, StringComparison.OrdinalIgnoreCase) && queue.Count > 0)
                    {
                        var next = queue.Dequeue();
                        return Json(next.Status, next.Body);
                    }
                }

                foreach (var (pathContains, configured) in _byPath)
                {
                    if (path.Contains(pathContains, StringComparison.OrdinalIgnoreCase))
                    {
                        return Json(configured.Status, configured.Body);
                    }
                }

                return Json(HttpStatusCode.OK, _defaultBody);
            }

            private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
                new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private sealed class CapturingLogger : ILogger<TilopayRepeatAdminService>
        {
            public List<string> Messages { get; } = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                Messages.Add(formatter(state, exception));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
