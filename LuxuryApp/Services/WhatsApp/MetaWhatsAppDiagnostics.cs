using System.Net;

namespace LuxuryApp.Services.WhatsApp
{
    public sealed record MetaWhatsAppConfigurationSnapshot(
        bool Enabled,
        string GraphApiVersion,
        string BaseUrl,
        string PhoneNumberId,
        string WhatsAppBusinessAccountId,
        string ConfirmationTemplateName,
        string ReminderTemplateName,
        string DefaultCountryCode,
        int RequestTimeoutSeconds,
        bool SendConfirmationOnCreate,
        bool SendReminderBeforeAppointment,
        bool AccessTokenPresent,
        bool AppSecretPresent)
    {
        public static MetaWhatsAppConfigurationSnapshot Create(MetaWhatsAppOptions options)
        {
            var normalized = MetaWhatsAppNormalizedOptions.Create(options);
            return new MetaWhatsAppConfigurationSnapshot(
                normalized.Enabled,
                normalized.GraphApiVersion,
                normalized.BaseUrl,
                normalized.PhoneNumberId,
                normalized.WhatsAppBusinessAccountId,
                normalized.ConfirmationTemplateName,
                normalized.ReminderTemplateName,
                normalized.DefaultCountryCode,
                normalized.RequestTimeoutSeconds,
                normalized.SendConfirmationOnCreate,
                normalized.SendReminderBeforeAppointment,
                AccessTokenPresent: !string.IsNullOrWhiteSpace(normalized.AccessToken),
                AppSecretPresent: !string.IsNullOrWhiteSpace(normalized.AppSecret));
        }
    }

    public sealed record MetaWhatsAppEndpointProbeResult(
        bool Success,
        string Endpoint,
        int? HttpStatus,
        string? DisplayPhoneNumber,
        string? VerifiedName,
        string? ErrorType,
        string? ErrorCode,
        int? ErrorSubcode,
        string? ErrorMessage,
        string? FbTraceId,
        string? ResponsePreview);

    public sealed record MetaWhatsAppConfigurationDiagnosticResult(
        bool Success,
        MetaWhatsAppConfigurationSnapshot Configuration,
        MetaWhatsAppEndpointProbeResult PhoneNumberProbe,
        MetaWhatsAppEndpointProbeResult? WabaPhoneNumbersProbe,
        bool? PhoneNumberBelongsToConfiguredWaba);

    internal sealed record MetaWhatsAppNormalizedOptions(
        bool Enabled,
        string GraphApiVersion,
        string BaseUrl,
        string PhoneNumberId,
        string WhatsAppBusinessAccountId,
        string AccessToken,
        string AppSecret,
        string DefaultCountryCode,
        string ConfirmationTemplateName,
        string ReminderTemplateName,
        int RequestTimeoutSeconds,
        bool SendConfirmationOnCreate,
        bool SendReminderBeforeAppointment)
    {
        public static MetaWhatsAppNormalizedOptions Create(MetaWhatsAppOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            return new MetaWhatsAppNormalizedOptions(
                options.Enabled,
                NormalizeGraphApiVersion(options.GraphApiVersion),
                NormalizeBaseUrl(options.BaseUrl),
                NormalizeSecret(options.PhoneNumberId),
                NormalizeSecret(options.WhatsAppBusinessAccountId),
                NormalizeSecret(options.AccessToken),
                NormalizeSecret(options.AppSecret),
                NormalizeDefaultCountryCode(options.DefaultCountryCode),
                NormalizeTemplateName(options.ConfirmationTemplateName),
                NormalizeTemplateName(options.ReminderTemplateName),
                options.RequestTimeoutSeconds,
                options.SendConfirmationOnCreate,
                options.SendReminderBeforeAppointment);
        }

        internal static string NormalizeSecret(string? value)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (trimmed.Length >= 2 &&
                ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
                 (trimmed.StartsWith('\'') && trimmed.EndsWith('\''))))
            {
                trimmed = trimmed[1..^1].Trim();
            }

            return trimmed;
        }

        private static string NormalizeBaseUrl(string? baseUrl)
        {
            var normalized = NormalizeSecret(baseUrl);
            return string.IsNullOrWhiteSpace(normalized)
                ? "https://graph.facebook.com"
                : normalized.TrimEnd('/');
        }

        private static string NormalizeGraphApiVersion(string? graphApiVersion)
        {
            var normalized = NormalizeSecret(graphApiVersion).Trim('/');
            return string.IsNullOrWhiteSpace(normalized)
                ? "v25.0"
                : normalized;
        }

        private static string NormalizeDefaultCountryCode(string? defaultCountryCode)
        {
            var digits = new string((defaultCountryCode ?? "506").Where(char.IsDigit).ToArray());
            return string.IsNullOrWhiteSpace(digits) ? "506" : digits;
        }

        private static string NormalizeTemplateName(string? templateName) =>
            NormalizeSecret(templateName);
    }

    internal sealed record MetaWhatsAppApiError(
        string Code,
        string Message,
        int? Subcode,
        string? Type,
        string? FbTraceId,
        string? ResponsePreview,
        bool IsAuthenticationError,
        bool ShouldRetry);

    internal static class MetaWhatsAppDiagnosticsLogger
    {
        public static void LogEffectiveConfiguration(
            ILogger logger,
            MetaWhatsAppOptions options,
            string reason)
        {
            ArgumentNullException.ThrowIfNull(logger);
            var snapshot = MetaWhatsAppConfigurationSnapshot.Create(options);

            logger.LogInformation(
                "Meta WhatsApp configuration {Reason}. Enabled {Enabled}. GraphApiVersion {GraphApiVersion}. BaseUrl {BaseUrl}. PhoneNumberId {PhoneNumberId}. WhatsAppBusinessAccountId {WhatsAppBusinessAccountId}. ConfirmationTemplateName {ConfirmationTemplateName}. ReminderTemplateName {ReminderTemplateName}. DefaultCountryCode {DefaultCountryCode}. RequestTimeoutSeconds {RequestTimeoutSeconds}. SendConfirmationOnCreate {SendConfirmationOnCreate}. SendReminderBeforeAppointment {SendReminderBeforeAppointment}. AccessTokenPresent {AccessTokenPresent}. AppSecretPresent {AppSecretPresent}.",
                reason,
                snapshot.Enabled,
                snapshot.GraphApiVersion,
                snapshot.BaseUrl,
                snapshot.PhoneNumberId,
                snapshot.WhatsAppBusinessAccountId,
                snapshot.ConfirmationTemplateName,
                snapshot.ReminderTemplateName,
                snapshot.DefaultCountryCode,
                snapshot.RequestTimeoutSeconds,
                snapshot.SendConfirmationOnCreate,
                snapshot.SendReminderBeforeAppointment,
                snapshot.AccessTokenPresent,
                snapshot.AppSecretPresent);
        }
    }
}
