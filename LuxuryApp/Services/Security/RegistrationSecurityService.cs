using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Services.Security
{
    public static class RegistrationSecurityActions
    {
        public const string Registration = "registration";
        public const string Login = "login";
        public const string ForgotPassword = "forgot-password";
    }

    public sealed class RegistrationSecurityService
    {
        private readonly IMemoryCache _cache;
        private readonly IOptionsMonitor<RegistrationSecurityOptions> _options;
        private readonly ILogger<RegistrationSecurityService> _logger;
        private readonly object _rateLimitGate = new();

        public RegistrationSecurityService(
            IMemoryCache cache,
            IOptionsMonitor<RegistrationSecurityOptions> options,
            ILogger<RegistrationSecurityService> logger)
        {
            _cache = cache;
            _options = options;
            _logger = logger;
        }

        public bool IsHoneypotTriggered(IFormCollection form)
        {
            var options = _options.CurrentValue;
            if (!options.EnableHoneypot)
            {
                return false;
            }

            var fieldName = string.IsNullOrWhiteSpace(options.HoneypotFieldName)
                ? "CompanyWebsite"
                : options.HoneypotFieldName.Trim();

            return form.TryGetValue(fieldName, out var values) &&
                values.Any(value => !string.IsNullOrWhiteSpace(value));
        }

        public RegistrationValidationResult ValidateRegistration(string? email, string? businessName)
        {
            var errors = new List<RegistrationValidationError>();
            var normalizedEmail = NormalizeEmail(email);
            var normalizedBusinessName = NormalizeBusinessName(businessName);

            if (!IsEmailShapeValid(normalizedEmail))
            {
                errors.Add(new RegistrationValidationError(
                    nameof(LuxuryApp.Models.Identity.RegistroViewModel.Email),
                    "Ingresa un correo electronico valido."));
            }
            else if (_options.CurrentValue.BlockSuspiciousEmailPatterns && LooksSuspiciousEmail(normalizedEmail))
            {
                errors.Add(new RegistrationValidationError(
                    nameof(LuxuryApp.Models.Identity.RegistroViewModel.Email),
                    "No pudimos validar ese correo. Usa un correo real del negocio o contacta soporte."));
            }

            if (!IsBusinessNameValid(normalizedBusinessName))
            {
                errors.Add(new RegistrationValidationError(
                    nameof(LuxuryApp.Models.Identity.RegistroViewModel.Name),
                    "Ingresa un nombre de negocio valido, sin HTML, enlaces ni caracteres de control."));
            }

            return new RegistrationValidationResult(errors);
        }

        public RateLimitCheckResult CheckRateLimit(string action, string? ipAddress, string? email)
        {
            var options = _options.CurrentValue;
            var rule = action switch
            {
                RegistrationSecurityActions.Registration => options.Registration,
                RegistrationSecurityActions.Login => options.Login,
                RegistrationSecurityActions.ForgotPassword => options.ForgotPassword,
                _ => null
            };

            if (rule is null || rule.WindowMinutes <= 0)
            {
                return RateLimitCheckResult.AllowedResult;
            }

            var window = TimeSpan.FromMinutes(rule.WindowMinutes);
            DateTimeOffset? retryAt = null;

            if (rule.IpPermitLimit > 0 && !string.IsNullOrWhiteSpace(ipAddress))
            {
                var ipResult = IncrementAndCheck(
                    $"ip:{action}:{HashKey(ipAddress.Trim())}",
                    rule.IpPermitLimit,
                    window);

                if (!ipResult.Allowed)
                {
                    retryAt = ipResult.ResetAtUtc;
                }
            }

            var normalizedEmail = NormalizeEmail(email);
            if (rule.EmailPermitLimit > 0 && !string.IsNullOrWhiteSpace(normalizedEmail))
            {
                var emailResult = IncrementAndCheck(
                    $"email:{action}:{HashKey(normalizedEmail)}",
                    rule.EmailPermitLimit,
                    window);

                if (!emailResult.Allowed)
                {
                    retryAt = retryAt.HasValue
                        ? Max(retryAt.Value, emailResult.ResetAtUtc)
                        : emailResult.ResetAtUtc;
                }
            }

            if (!retryAt.HasValue)
            {
                return RateLimitCheckResult.AllowedResult;
            }

            _logger.LogWarning(
                "Rate limit de seguridad activado. Action {Action}. IpPresent {IpPresent}. EmailPresent {EmailPresent}.",
                action,
                !string.IsNullOrWhiteSpace(ipAddress),
                !string.IsNullOrWhiteSpace(normalizedEmail));

            return new RateLimitCheckResult(false, retryAt.Value);
        }

        public static string NormalizeEmail(string? email) =>
            string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();

        public static string NormalizeBusinessName(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

        private RateLimitCounterResult IncrementAndCheck(string key, int limit, TimeSpan window)
        {
            var now = DateTimeOffset.UtcNow;

            lock (_rateLimitGate)
            {
                var cacheKey = $"registration-security:{key}";
                var counter = _cache.Get<RateLimitCounter>(cacheKey);
                if (counter is null || counter.ResetAtUtc <= now)
                {
                    counter = new RateLimitCounter(0, now.Add(window));
                }

                counter = counter with { Count = counter.Count + 1 };
                _cache.Set(cacheKey, counter, counter.ResetAtUtc);

                return new RateLimitCounterResult(counter.Count <= limit, counter.ResetAtUtc);
            }
        }

        private static bool IsEmailShapeValid(string email)
        {
            if (string.IsNullOrWhiteSpace(email) ||
                email.Length > 254 ||
                email.Count(character => character == '@') != 1 ||
                email.Any(char.IsControl) ||
                email.Any(char.IsWhiteSpace))
            {
                return false;
            }

            try
            {
                var address = new MailAddress(email);
                if (!string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (FormatException)
            {
                return false;
            }

            var parts = email.Split('@', 2);
            var local = parts[0];
            var domain = parts[1];

            if (local.Length is 0 or > 64 ||
                local.StartsWith(".", StringComparison.Ordinal) ||
                local.EndsWith(".", StringComparison.Ordinal) ||
                local.Contains("..", StringComparison.Ordinal) ||
                domain.Length is 0 or > 253 ||
                !domain.Contains('.'))
            {
                return false;
            }

            var labels = domain.Split('.');
            return labels.All(label =>
                label.Length is > 0 and <= 63 &&
                !label.StartsWith("-", StringComparison.Ordinal) &&
                !label.EndsWith("-", StringComparison.Ordinal) &&
                label.All(character => char.IsLetterOrDigit(character) || character == '-'));
        }

        private static bool LooksSuspiciousEmail(string email)
        {
            var parts = email.Split('@', 2);
            var local = parts[0];
            var domain = parts[1];
            var localSegments = local.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var singleCharacterSegments = localSegments.Count(segment => segment.Length == 1);
            var dotCount = local.Count(character => character == '.');
            var digitRatio = local.Length == 0
                ? 0
                : (double)local.Count(char.IsDigit) / local.Length;

            var blockedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "10minutemail.com",
                "guerrillamail.com",
                "mailinator.com",
                "tempmail.com",
                "temp-mail.org",
                "yopmail.com"
            };

            return blockedDomains.Contains(domain) ||
                dotCount >= 7 ||
                singleCharacterSegments >= 5 ||
                (local.Length >= 12 && digitRatio > 0.55);
        }

        private static bool IsBusinessNameValid(string businessName)
        {
            if (businessName.Length is < 3 or > 100 ||
                businessName.Any(char.IsControl) ||
                businessName.Contains('<') ||
                businessName.Contains('>') ||
                businessName.Contains('{') ||
                businessName.Contains('}') ||
                businessName.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                businessName.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
                businessName.Count(char.IsLetter) < 2)
            {
                return false;
            }

            return !businessName.All(character => char.IsDigit(character) || char.IsWhiteSpace(character));
        }

        private static string HashKey(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes);
        }

        private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
            left >= right ? left : right;

        private sealed record RateLimitCounter(int Count, DateTimeOffset ResetAtUtc);
        private sealed record RateLimitCounterResult(bool Allowed, DateTimeOffset ResetAtUtc);
    }

    public sealed record RegistrationValidationError(string FieldName, string Message);

    public sealed class RegistrationValidationResult
    {
        public RegistrationValidationResult(IReadOnlyCollection<RegistrationValidationError> errors)
        {
            Errors = errors;
        }

        public IReadOnlyCollection<RegistrationValidationError> Errors { get; }
        public bool IsValid => Errors.Count == 0;
    }

    public sealed record RateLimitCheckResult(bool Allowed, DateTimeOffset? RetryAtUtc)
    {
        public static readonly RateLimitCheckResult AllowedResult = new(true, null);
    }
}
