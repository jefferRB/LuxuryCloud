using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Wrap;

namespace LuxuryApp.Services.Tilopay
{
    public interface ITilopayRepeatAdminService
    {
        /// <summary>True si la integración admin está habilitada por configuración.</summary>
        bool IsEnabled { get; }

        /// <summary>Lista los suscriptores de un plan recurrente. Parseo tolerante (id|id_suscriptor).</summary>
        Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(
            int tilopayPlanId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resuelve el id_suscriptor por (plan, email). Nunca elige a ciegas: 0 => NotFound,
        /// 1 => Found, &gt;1 => Ambiguous. Aplica reintento pequeño por consistencia eventual.
        /// </summary>
        Task<SubscriberResolutionResult> ResolveSubscriberAsync(
            int tilopayPlanId,
            string? email,
            CancellationToken cancellationToken = default);

        /// <summary>URL para renovar/actualizar tarjeta sin crear un suscriptor nuevo.</summary>
        Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(
            int tilopayPlanId,
            string? email,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Clasifica por estado los suscriptores del plan DESTINO que coinciden por email, para que
        /// el checkout decida si puede crear un suscriptor nuevo. A diferencia de
        /// <see cref="ResolveSubscriberAsync"/> (que busca UN suscriptor reutilizable y descarta los
        /// eliminados), aquí importan también los inactivos: son la prueba de que volver a ese plan
        /// es legítimo y no un duplicado.
        /// </summary>
        Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(
            int tilopayPlanId,
            string? email,
            CancellationToken cancellationToken = default);

        Task<TilopayAdminOperationResult> PauseSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default);
        Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default);
        Task<TilopayAdminOperationResult> DeleteSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default);
        Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(string subscriberId, TilopaySubscriberStatus status, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Cliente del API admin de TiloPay Repeat. Los webhooks no traen id_suscriptor; este
    /// servicio lo resuelve por (plan, email) y ejecuta las operaciones de gestión del proveedor.
    /// Nunca loguea key, password, bearer ni email en claro. Deshabilitado por defecto: con
    /// <c>TilopayRepeatAdmin:Enabled=false</c> lanza si se le invoca, y los callers lo saltan.
    /// </summary>
    public sealed class TilopayRepeatAdminService : ITilopayRepeatAdminService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly OpcionesTilopay _tilopayOptions;
        private readonly OpcionesTilopayRepeatAdmin _adminOptions;
        private readonly ILogger<TilopayRepeatAdminService> _logger;
        private readonly AsyncPolicyWrap<HttpResponseMessage> _safeReadPolicy;

        public TilopayRepeatAdminService(
            HttpClient httpClient,
            IMemoryCache cache,
            IOptions<OpcionesTilopay> tilopayOptions,
            IOptions<OpcionesTilopayRepeatAdmin> adminOptions,
            ILogger<TilopayRepeatAdminService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _tilopayOptions = tilopayOptions.Value;
            _adminOptions = adminOptions.Value;
            _logger = logger;

            var retry = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .OrResult(response => IsTransientStatus(response.StatusCode))
                .WaitAndRetryAsync(
                    2,
                    attempt => TimeSpan.FromSeconds(attempt * 2),
                    (result, delay, attempt, _) =>
                        _logger.LogWarning(
                            "TiloPay admin retry {Attempt}. Delay {DelaySeconds}s. Status {StatusCode}",
                            attempt,
                            delay.TotalSeconds,
                            result.Result?.StatusCode));

            var circuit = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .OrResult(response => IsTransientStatus(response.StatusCode))
                .CircuitBreakerAsync(
                    5,
                    TimeSpan.FromSeconds(30),
                    onBreak: (result, delay) =>
                        _logger.LogError(
                            "Circuit breaker TiloPay admin abierto por {DelaySeconds}s. Status {StatusCode}",
                            delay.TotalSeconds,
                            result.Result?.StatusCode),
                    onReset: () => _logger.LogInformation("Circuit breaker TiloPay admin restablecido."));

            _safeReadPolicy = Policy.WrapAsync(retry, circuit);
        }

        public bool IsEnabled => _adminOptions.Enabled;

        public async Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(
            int tilopayPlanId,
            CancellationToken cancellationToken = default)
        {
            EnsureEnabled();
            ValidateApiCredentials();

            // Contrato real de TiloPay (verificado en prod): body JSON con "key" (ApiKey) e "id"
            // (el id del plan recurrente COMO STRING). NO es "id_plan"/"plan_id" ni int.
            var raw = await PostAsync(
                "api/v1/getSuscriptorRepeat",
                new Dictionary<string, object?>
                {
                    ["key"] = _tilopayOptions.ApiKey,
                    ["id"] = tilopayPlanId.ToString(CultureInfo.InvariantCulture)
                },
                cancellationToken);

            return ParseSubscribers(raw, tilopayPlanId);
        }

        public async Task<SubscriberResolutionResult> ResolveSubscriberAsync(
            int tilopayPlanId,
            string? email,
            CancellationToken cancellationToken = default)
        {
            EnsureEnabled();

            var normalizedEmail = NormalizeEmail(email);
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                return SubscriberResolutionResult.Failed("email vacío para resolución de suscriptor.");
            }

            var retryCount = Math.Clamp(_adminOptions.ResolveRetryCount, 0, 5);
            var baseDelay = Math.Clamp(_adminOptions.ResolveRetryBaseDelayMs, 100, 5000);

            SubscriberResolutionResult lastResult = SubscriberResolutionResult.NotFound();
            for (var attempt = 0; attempt <= retryCount; attempt++)
            {
                try
                {
                    var subscribers = await GetSuscriptorRepeatAsync(tilopayPlanId, cancellationToken);
                    var matches = subscribers
                        .Where(subscriber => string.Equals(
                            NormalizeEmail(subscriber.Email),
                            normalizedEmail,
                            StringComparison.OrdinalIgnoreCase))
                        // Un suscriptor eliminado (estado 4) no es un match válido para reutilizar.
                        .Where(subscriber => !IsDeletedStatus(subscriber.Status))
                        .ToList();

                    if (matches.Count == 1)
                    {
                        return SubscriberResolutionResult.Found(matches[0], matches.Count);
                    }

                    if (matches.Count > 1)
                    {
                        // Si hay señales de estado/fecha, intentar un único ACTIVO más reciente;
                        // si eso tampoco desambigua, es Ambiguous (nunca elegir a ciegas).
                        var active = matches.Where(m => IsActiveStatus(m.Status)).ToList();
                        var candidates = active.Count > 0 ? active : matches;
                        var withDates = candidates.Where(m => m.CreatedAtUtc.HasValue).ToList();

                        if (candidates.Count == 1)
                        {
                            return SubscriberResolutionResult.Found(candidates[0], matches.Count);
                        }

                        if (withDates.Count == candidates.Count && candidates.Count > 0)
                        {
                            var newest = candidates.OrderByDescending(m => m.CreatedAtUtc).ToList();
                            if (newest[0].CreatedAtUtc > newest[1].CreatedAtUtc)
                            {
                                return SubscriberResolutionResult.Found(newest[0], matches.Count);
                            }
                        }

                        return SubscriberResolutionResult.Ambiguous(
                            matches.Count,
                            $"{matches.Count} suscriptores coinciden por email y no se pudo desambiguar por estado/fecha.");
                    }

                    lastResult = SubscriberResolutionResult.NotFound(
                        $"Sin suscriptor para el plan {tilopayPlanId} y el email indicado (intento {attempt + 1}).");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Fallo consultando suscriptores TiloPay. PlanId {PlanId}. Intento {Attempt}.",
                        tilopayPlanId,
                        attempt + 1);
                    lastResult = SubscriberResolutionResult.Failed(Trim(ex.Message, 200));
                }

                if (attempt < retryCount)
                {
                    try
                    {
                        await Task.Delay(baseDelay * (attempt + 1), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            return lastResult;
        }

        public async Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(
            int tilopayPlanId,
            string? email,
            CancellationToken cancellationToken = default)
        {
            EnsureEnabled();

            var normalizedEmail = NormalizeEmail(email);
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                return TargetSubscriberAssessment.Error("email vacío para evaluar el plan destino.");
            }

            // Mismo reintento que ResolveSubscriberAsync: si el plan sale VACÍO puede ser
            // consistencia eventual del proveedor, y dar por libre un plan que ya tiene suscriptor
            // es justo el duplicado que este blindaje evita. Un plan con filas no se reintenta.
            var retryCount = Math.Clamp(_adminOptions.ResolveRetryCount, 0, 5);
            var baseDelay = Math.Clamp(_adminOptions.ResolveRetryBaseDelayMs, 100, 5000);

            List<TilopaySubscriber> matches = new();
            TargetSubscriberAssessment? lastError = null;

            for (var attempt = 0; attempt <= retryCount; attempt++)
            {
                try
                {
                    var subscribers = await GetSuscriptorRepeatAsync(tilopayPlanId, cancellationToken);
                    matches = subscribers
                        .Where(subscriber => string.Equals(
                            NormalizeEmail(subscriber.Email),
                            normalizedEmail,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    lastError = null;

                    if (matches.Count > 0)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Fallo evaluando suscriptores del plan destino. PlanId {PlanId}. Intento {Attempt}.",
                        tilopayPlanId,
                        attempt + 1);
                    lastError = TargetSubscriberAssessment.Error(Trim(ex.Message, 200));
                }

                if (attempt < retryCount)
                {
                    try
                    {
                        await Task.Delay(baseDelay * (attempt + 1), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            if (lastError is not null)
            {
                return lastError;
            }

            return TargetSubscriberAssessment.FromMatches(matches, tilopayPlanId);
        }

        public async Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(
            int tilopayPlanId,
            string? email,
            CancellationToken cancellationToken = default)
        {
            EnsureEnabled();
            ValidateApiCredentials();

            var normalizedEmail = NormalizeEmail(email);
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                return TilopayAdminOperationResult.Fail("email vacío para recurrentUrl.");
            }

            // Contrato OFICIAL confirmado por soporte TiloPay (2026): POST /api/v1/recurrentUrl con
            // JSON { key, id, email }. El campo del plan recurrente es "id" (STRING). NO se usan
            // id_plan / plan_id (campos no oficiales) ni id_suscriptor (no se requiere).
            var planIdText = tilopayPlanId.ToString(CultureInfo.InvariantCulture);

            // Para actualización de tarjeta se selecciona SIEMPRE url_renew. "url_register" es un modo
            // de diagnóstico opt-in (config) y NUNCA reemplazo silencioso de url_renew (podría mandar a
            // login/administración): si se pide y no viene url_register, NO se cae a url_renew a ciegas.
            var preferredField = string.Equals(_adminOptions.RecurrentUrlPreferredField?.Trim(), "url_register", StringComparison.OrdinalIgnoreCase)
                ? "url_register"
                : "url_renew";

            return await AttemptRecurrentUrlAsync(
                tilopayPlanId,
                normalizedEmail,
                new Dictionary<string, object?>
                {
                    ["key"] = _tilopayOptions.ApiKey,
                    ["id"] = planIdText,
                    ["email"] = normalizedEmail
                },
                contract: "id",
                preferredField,
                cancellationToken);
        }

        private async Task<TilopayAdminOperationResult> AttemptRecurrentUrlAsync(
            int tilopayPlanId,
            string normalizedEmail,
            IReadOnlyDictionary<string, object?> body,
            string contract,
            string preferredField,
            CancellationToken cancellationToken)
        {
            try
            {
                var (status, raw) = await SendRawAsync("api/v1/recurrentUrl", body, cancellationToken);
                var parsed = ParseRecurrentUrl(raw, preferredField);

                var diagnostics = new RecurrentUrlDiagnostics
                {
                    Contract = contract,
                    HttpStatus = (int)status,
                    ProviderType = Truncate(parsed.Type, 40),
                    ProviderMessage = Truncate(parsed.Message, 200),
                    HasUrlRenew = parsed.HasUrlRenew,
                    HasUrlRegister = parsed.HasUrlRegister,
                    SelectedField = parsed.SelectedField,
                    UrlHostPathMasked = MaskUrlHostPath(parsed.SelectedUrl)
                };

                // Diagnóstico SANITIZADO: contrato, status, type/message, presencia de cada campo y
                // host/path enmascarado. NUNCA la URL completa (el token va en el path de tp.cr).
                _logger.LogInformation(
                    "recurrentUrl diagnóstico. PlanId {PlanId}. Contract {Contract}. Status {Status}. Type {Type}. Message {Message}. HasRenew {HasRenew}. HasRegister {HasRegister}. Selected {Selected}. UrlHostPath {UrlHostPath}.",
                    tilopayPlanId, contract, (int)status, diagnostics.ProviderType ?? "-", diagnostics.ProviderMessage ?? "-",
                    diagnostics.HasUrlRenew, diagnostics.HasUrlRegister, diagnostics.SelectedField ?? "-", diagnostics.UrlHostPathMasked ?? "-");

                if (IsSuccessStatus(status) && !string.IsNullOrWhiteSpace(parsed.SelectedUrl))
                {
                    _logger.LogInformation(
                        "recurrentUrl generado. PlanId {PlanId}. EmailMasked {EmailMasked}. Contract {Contract}. Field {Field}.",
                        tilopayPlanId, SensitiveDataMasker.MaskEmail(normalizedEmail), contract, diagnostics.SelectedField ?? "-");

                    return TilopayAdminOperationResult.Ok("URL de renovación generada.", parsed.SelectedUrl)
                        with { Contract = contract, RecurrentDiagnostics = diagnostics };
                }

                if (!IsSuccessStatus(status))
                {
                    _logger.LogError(
                        "TiloPay admin recurrentUrl devolvió error. Status {StatusCode}. Contract {Contract}. Body {SanitizedBody}",
                        status, contract, SanitizeResponseBody(raw));
                }
                else
                {
                    // 200 pero sin url_renew utilizable (a veces type=402 con status 200, o solo url_register).
                    LogUnexpectedShape("recurrentUrl", raw);
                }

                return TilopayAdminOperationResult.Fail(IsSuccessStatus(status)
                    ? "TiloPay no devolvió una url_renew utilizable."
                    : "No fue posible generar la URL de renovación.")
                    with { RecurrentDiagnostics = diagnostics };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generando recurrentUrl. PlanId {PlanId}. Contract {Contract}.", tilopayPlanId, contract);
                return TilopayAdminOperationResult.Fail("No fue posible generar la URL de renovación.");
            }
        }

        public Task<TilopayAdminOperationResult> PauseSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
            SubscriberOperationAsync("api/v1/pauseSuscriptorRepeat", subscriberId, "pausar", cancellationToken);

        public Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
            SubscriberOperationAsync("api/v1/reactiveSuscriptorRepeat", subscriberId, "reactivar", cancellationToken);

        public Task<TilopayAdminOperationResult> DeleteSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
            SubscriberOperationAsync("api/v1/deleteSuscriptorRepeat", subscriberId, "eliminar", cancellationToken);

        public async Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(
            string subscriberId,
            TilopaySubscriberStatus status,
            CancellationToken cancellationToken = default)
        {
            EnsureEnabled();
            ValidateApiCredentials();

            if (string.IsNullOrWhiteSpace(subscriberId))
            {
                return TilopayAdminOperationResult.Fail("id_suscriptor vacío.");
            }

            try
            {
                await PostAsync(
                    "api/v1/editSuscriptorRepeat",
                    new Dictionary<string, object?>
                    {
                        ["key"] = _tilopayOptions.ApiKey,
                        ["id_suscriptor"] = subscriberId,
                        ["estado"] = (int)status
                    },
                    cancellationToken);

                _logger.LogInformation(
                    "editSuscriptorRepeat aplicado. SubscriberIdSuffix {Suffix}. Estado {Estado}.",
                    SensitiveDataMasker.MaskReference(subscriberId),
                    (int)status);

                return TilopayAdminOperationResult.Ok($"Estado actualizado a {(int)status}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error en editSuscriptorRepeat. SubscriberIdSuffix {Suffix}. Estado {Estado}.",
                    SensitiveDataMasker.MaskReference(subscriberId),
                    (int)status);
                return TilopayAdminOperationResult.Fail("No fue posible actualizar el estado del suscriptor.");
            }
        }

        // ── Internos ─────────────────────────────────────────────────────────────

        private async Task<TilopayAdminOperationResult> SubscriberOperationAsync(
            string path,
            string subscriberId,
            string verb,
            CancellationToken cancellationToken)
        {
            EnsureEnabled();
            ValidateApiCredentials();

            if (string.IsNullOrWhiteSpace(subscriberId))
            {
                return TilopayAdminOperationResult.Fail("id_suscriptor vacío.");
            }

            try
            {
                await PostAsync(
                    path,
                    new Dictionary<string, object?>
                    {
                        ["key"] = _tilopayOptions.ApiKey,
                        ["id_suscriptor"] = subscriberId
                    },
                    cancellationToken);

                _logger.LogInformation(
                    "Operación {Verb} suscriptor TiloPay OK. SubscriberIdSuffix {Suffix}.",
                    verb,
                    SensitiveDataMasker.MaskReference(subscriberId));

                return TilopayAdminOperationResult.Ok($"Suscriptor {verb} correctamente.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error al {Verb} suscriptor TiloPay. SubscriberIdSuffix {Suffix}.",
                    verb,
                    SensitiveDataMasker.MaskReference(subscriberId));
                return TilopayAdminOperationResult.Fail($"No fue posible {verb} el suscriptor en TiloPay.");
            }
        }

        /// <summary>
        /// POST autenticado que exige 2xx: si no, loguea el body sanitizado y lanza. Comportamiento
        /// histórico usado por todos los endpoints salvo recurrentUrl (que necesita inspeccionar el
        /// error para decidir el fallback de contrato, y por eso usa <see cref="SendRawAsync"/>).
        /// </summary>
        private async Task<string> PostAsync(
            string path,
            IReadOnlyDictionary<string, object?> body,
            CancellationToken cancellationToken)
        {
            var (status, raw) = await SendRawAsync(path, body, cancellationToken);

            if (!IsSuccessStatus(status))
            {
                // Body sanitizado para diagnosticar 4xx/5xx (p.ej. "campo id requerido") sin exponer
                // secretos: se redactan key/token/password/auth/email/tarjeta/cvv, etc.
                _logger.LogError(
                    "TiloPay admin {Path} devolvió error. Status {StatusCode}. BodyLength {BodyLength}. Body {SanitizedBody}",
                    path,
                    status,
                    raw.Length,
                    SanitizeResponseBody(raw));
                throw new InvalidOperationException($"TiloPay admin {path} devolvió {(int)status}.");
            }

            return raw;
        }

        /// <summary>
        /// POST autenticado que devuelve status + body crudo SIN lanzar. Lo usa recurrentUrl para
        /// distinguir el error "id plan parameter is required" (y reintentar con otro contrato) tanto
        /// si TiloPay responde 4xx como si responde 200 con un cuerpo de error.
        /// </summary>
        private async Task<(HttpStatusCode Status, string Raw)> SendRawAsync(
            string path,
            IReadOnlyDictionary<string, object?> body,
            CancellationToken cancellationToken)
        {
            var accessToken = await GetApiTokenAsync(cancellationToken);
            using var linkedCts = CreateTimeout(cancellationToken);

            var response = await _safeReadPolicy.ExecuteAsync(async () =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = JsonContent.Create(body)
                };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                return await _httpClient.SendAsync(message, linkedCts.Token);
            });

            var raw = await response.Content.ReadAsStringAsync(linkedCts.Token);
            return (response.StatusCode, raw);
        }

        private static bool IsSuccessStatus(HttpStatusCode status) => (int)status is >= 200 and < 300;

        /// <summary>
        /// Redacta claves sensibles del body de respuesta para logging seguro (JSON). Si no es JSON,
        /// devuelve un marcador; nunca vuelca secretos ni datos personales en claro.
        /// </summary>
        private static string SanitizeResponseBody(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                return JsonSerializer.Serialize(RedactJsonElement(document.RootElement));
            }
            catch (JsonException)
            {
                return "[non-json body omitted]";
            }
        }

        private static object? RedactJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => SensitiveDataMasker.IsSensitiveKey(property.Name)
                        ? SensitiveDataMasker.Redacted
                        : RedactJsonElement(property.Value)),
                JsonValueKind.Array => element.EnumerateArray().Select(RedactJsonElement).ToArray(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.ToString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        private IReadOnlyList<TilopaySubscriber> ParseSubscribers(string raw, int expectedPlanId)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<TilopaySubscriber>();
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                _logger.LogWarning("getSuscriptorRepeat devolvió un payload no-JSON. BodyLength {BodyLength}.", raw.Length);
                return Array.Empty<TilopaySubscriber>();
            }

            using (document)
            {
                var array = FindSubscribersArray(document.RootElement);
                if (array is null)
                {
                    LogUnexpectedShape("getSuscriptorRepeat", raw);
                    return Array.Empty<TilopaySubscriber>();
                }

                var result = new List<TilopaySubscriber>();
                foreach (var element in array.Value.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var subscriberId = ReadFirstString(element, "id_suscriptor", "idSuscriptor", "id", "subscriber_id", "subscriberId");
                    if (string.IsNullOrWhiteSpace(subscriberId))
                    {
                        continue;
                    }

                    var expiresRaw = ReadFirstString(element, "expire", "expires", "expiry", "vence", "vencimiento", "next_billing", "nextBilling");

                    result.Add(new TilopaySubscriber
                    {
                        SubscriberId = subscriberId,
                        Email = ReadFirstString(element, "email", "correo", "mail"),
                        Status = ReadFirstString(element, "status", "estado", "state"),
                        // "create" (con la hora del alta) es el campo real del contrato TiloPay.
                        CreatedAtUtc = ReadFirstDateUtc(element, "create", "created_at", "createdAt", "fecha", "fecha_creacion", "date"),
                        // "expire" es fecha SIN hora ("2026-09-15"): fin del día Costa Rica → UTC.
                        ExpiresAtUtc = ProviderExpiryDate.ParseCostaRicaEndOfDayUtc(expiresRaw),
                        ExpiresRaw = expiresRaw,
                        TilopayPlanId = ReadFirstInt(element, "id_plan", "idPlan", "planId", "plan_id") ?? expectedPlanId
                    });
                }

                return result;
            }
        }

        private static JsonElement? FindSubscribersArray(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                return root;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                // Buscar un array bajo claves típicas primero, luego cualquier array de objetos.
                // "suscriptor" (singular) es la clave real confirmada en el contrato de TiloPay.
                foreach (var key in new[] { "suscriptor", "suscriptores", "subscribers", "data", "response", "result", "items" })
                {
                    if (root.TryGetProperty(key, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
                    {
                        return candidate;
                    }
                }

                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        return property.Value;
                    }
                }
            }

            return null;
        }

        private void LogUnexpectedShape(string operation, string raw)
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                var keys = document.RootElement.ValueKind == JsonValueKind.Object
                    ? string.Join(",", document.RootElement.EnumerateObject().Select(p => p.Name))
                    : document.RootElement.ValueKind.ToString();

                _logger.LogWarning(
                    "TiloPay admin {Operation}: shape inesperado. RootKind {RootKind}. TopLevelKeys [{Keys}].",
                    operation,
                    document.RootElement.ValueKind,
                    keys);
            }
            catch (JsonException)
            {
                _logger.LogWarning(
                    "TiloPay admin {Operation}: respuesta no-JSON. BodyLength {BodyLength}.",
                    operation,
                    raw.Length);
            }
        }

        private readonly record struct RecurrentUrlParse(
            string? SelectedUrl, string? SelectedField, bool HasUrlRenew, bool HasUrlRegister, string? Type, string? Message);

        /// <summary>
        /// Parsea la respuesta de recurrentUrl y elige la URL según <paramref name="preferredField"/>.
        /// Contrato OFICIAL de TiloPay: url_renew = actualizar/renovar el método de pago de un
        /// suscriptor existente; url_register = registro de suscripción nueva. La actualización de
        /// tarjeta usa SIEMPRE url_renew, de forma ESTRICTA: nunca cae a url_register ni a alias como
        /// "url"/"link" (podrían mandar a login/administración). El modo "url_register" es opt-in de
        /// diagnóstico y también selecciona SOLO su propio campo, sin fallback cruzado silencioso.
        /// </summary>
        private static RecurrentUrlParse ParseRecurrentUrl(string raw, string preferredField)
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;

                var renew = NonEmpty(ReadFirstString(root, "url_renew"));
                var register = NonEmpty(ReadFirstString(root, "url_register"));
                var type = ReadFirstString(root, "type");
                var message = ReadFirstString(root, "message");

                // Estricto: cada modo selecciona SOLO su propio campo. Si no viene, SelectedUrl = null
                // ⇒ el caller lo trata como fallo (mensaje controlado / ManualReview), nunca sustituto.
                var (selectedUrl, selectedField) =
                    string.Equals(preferredField, "url_register", StringComparison.OrdinalIgnoreCase)
                        ? (register, register is not null ? "url_register" : null)
                        : (renew, renew is not null ? "url_renew" : null);

                return new RecurrentUrlParse(selectedUrl, selectedField, renew is not null, register is not null, type, message);
            }
            catch (JsonException)
            {
                return new RecurrentUrlParse(null, null, false, false, null, null);
            }
        }

        private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);

        /// <summary>
        /// host + path enmascarado de una URL para diagnóstico. NO incluye el token: en los links de
        /// TiloPay (tp.cr/l/&lt;token&gt;) el secreto va en el ÚLTIMO segmento del path, que se recorta.
        /// </summary>
        private static string? MaskUrlHostPath(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return uri.Host;
            }

            var shown = string.Join("/", segments.Take(segments.Length - 1));
            return string.IsNullOrEmpty(shown)
                ? $"{uri.Host}/*** ({segments.Length} segs)"
                : $"{uri.Host}/{shown}/*** ({segments.Length} segs)";
        }

        private async Task<string> GetApiTokenAsync(CancellationToken cancellationToken)
        {
            ValidateApiCredentials();

            // Comparte el token con TilopayService usando la MISMA clave de cache.
            var cacheKey = $"tilopay_api_token::{_tilopayOptions.ApiUser}";
            if (_cache.TryGetValue<string>(cacheKey, out var cachedToken) && !string.IsNullOrWhiteSpace(cachedToken))
            {
                return cachedToken!;
            }

            using var linkedCts = CreateTimeout(cancellationToken);
            // Contrato real de login TiloPay: apiuser + password + key (ApiKey) en el body JSON.
            var response = await _safeReadPolicy.ExecuteAsync(() =>
                _httpClient.PostAsJsonAsync(
                    "api/v1/login",
                    new
                    {
                        apiuser = _tilopayOptions.ApiUser,
                        password = _tilopayOptions.ApiPassword,
                        key = _tilopayOptions.ApiKey
                    },
                    linkedCts.Token));

            var raw = await response.Content.ReadAsStringAsync(linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("TiloPay admin login devolvió error. Status {StatusCode}.", response.StatusCode);
                throw new InvalidOperationException("No fue posible autenticarse contra TiloPay (admin).");
            }

            using var document = JsonDocument.Parse(raw);
            var accessToken = ReadFirstString(document.RootElement, "access_token", "accessToken", "token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("TiloPay no devolvió access_token (admin).");
            }

            _cache.Set(cacheKey, accessToken, DateTimeOffset.UtcNow.AddMinutes(50));
            return accessToken;
        }

        private void EnsureEnabled()
        {
            if (!_adminOptions.Enabled)
            {
                throw new InvalidOperationException("TiloPay Repeat Admin está deshabilitado: TilopayRepeatAdmin:Enabled=false.");
            }
        }

        private void ValidateApiCredentials()
        {
            if (string.IsNullOrWhiteSpace(_tilopayOptions.ApiUser) ||
                string.IsNullOrWhiteSpace(_tilopayOptions.ApiPassword) ||
                string.IsNullOrWhiteSpace(_tilopayOptions.ApiKey))
            {
                throw new InvalidOperationException(
                    "TiloPay no está configurado. Debe definir Tilopay:ApiUser, Tilopay:ApiPassword y Tilopay:ApiKey.");
            }
        }

        private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _adminOptions.TimeoutSeconds)));
            return cts;
        }

        private static bool IsTransientStatus(HttpStatusCode statusCode) =>
            statusCode == HttpStatusCode.RequestTimeout ||
            statusCode == HttpStatusCode.BadGateway ||
            statusCode == HttpStatusCode.ServiceUnavailable ||
            statusCode == HttpStatusCode.GatewayTimeout ||
            (int)statusCode >= 500;

        // Fuente única: ProviderSubscriberStatusRules. Estas listas vivían aquí y no reconocían
        // "Delete" (singular), el valor real de TiloPay, así que un suscriptor eliminado se
        // reutilizaba como si estuviera vivo.
        private static bool IsDeletedStatus(string? status) =>
            ProviderSubscriberStatusRules.IsProviderSubscriberInactive(status);

        private static bool IsActiveStatus(string? status) =>
            ProviderSubscriberStatusRules.IsProviderSubscriberActive(status);

        private static string? NormalizeEmail(string? email) =>
            string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

        private static string? ReadFirstString(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty(name, out var value))
                {
                    var normalized = value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.Number => value.ToString(),
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(normalized) &&
                        !string.Equals(normalized.Trim(), "null", StringComparison.OrdinalIgnoreCase))
                    {
                        return normalized.Trim();
                    }
                }
            }

            return null;
        }

        private static int? ReadFirstInt(JsonElement element, params string[] names)
        {
            var raw = ReadFirstString(element, names);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static DateTime? ReadFirstDateUtc(JsonElement element, params string[] names)
        {
            var raw = ReadFirstString(element, names);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;
        }

        private static string Trim(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];
    }
}
