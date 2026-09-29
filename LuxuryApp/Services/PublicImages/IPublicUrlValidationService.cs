using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.PublicPages;

namespace LuxuryApp.Services.PublicImages
{
    public interface IPublicUrlValidationService
    {
        string? NormalizePlainText(string? value, int maxLength, string field);

        string? NormalizeMultilinePlainText(string? value, int maxLength, string field);

        string? NormalizePhone(string? value, int maxLength, string field);

        string? NormalizeWhatsAppPhone(string? value, string field);

        string? NormalizeEmail(string? value, string field);

        string? NormalizeGoogleMapsUrl(string? value, string field);

        string? NormalizeWazeUrl(string? value, string field);

        string? NormalizeInstagramUrl(string? value, string field);

        string? NormalizeFacebookUrl(string? value, string field);

        string? NormalizeTikTokUrl(string? value, string field);

        string? BuildWhatsAppUrl(string? normalizedPhone);
    }

    public sealed class PublicUrlValidationService : IPublicUrlValidationService
    {
        private static readonly HashSet<string> GoogleMapsHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "google.com",
            "www.google.com",
            "maps.google.com",
            "maps.app.goo.gl"
        };

        private static readonly HashSet<string> WazeHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "waze.com",
            "www.waze.com",
            "ul.waze.com"
        };

        private static readonly HashSet<string> InstagramHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "instagram.com",
            "www.instagram.com"
        };

        private static readonly HashSet<string> FacebookHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "facebook.com",
            "www.facebook.com",
            "fb.com",
            "www.fb.com"
        };

        private static readonly HashSet<string> TikTokHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "tiktok.com",
            "www.tiktok.com"
        };

        public string? NormalizePlainText(string? value, int maxLength, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            ValidateLength(trimmed, maxLength, field);
            EnsurePlainText(trimmed, field);
            return trimmed;
        }

        public string? NormalizeMultilinePlainText(string? value, int maxLength, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();

            ValidateLength(normalized, maxLength, field);
            EnsureMultilinePlainText(normalized, field);
            return normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        public string? NormalizePhone(string? value, int maxLength, string field)
        {
            var normalized = NormalizePlainText(value, maxLength, field);
            if (normalized is null)
            {
                return null;
            }

            if (normalized.Any(char.IsControl))
            {
                throw new TenantPublicPageValidationException(
                    "El telefono contiene caracteres no permitidos.",
                    field);
            }

            return normalized;
        }

        public string? NormalizeWhatsAppPhone(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            EnsurePlainText(value, field);
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length is < 7 or > 15)
            {
                throw new TenantPublicPageValidationException(
                    "Indica un numero de WhatsApp valido, con codigo de pais si aplica.",
                    field);
            }

            return digits;
        }

        public string? NormalizeEmail(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            ValidateLength(trimmed, 256, field);
            EnsurePlainText(trimmed, field);
            if (!new EmailAddressAttribute().IsValid(trimmed))
            {
                throw new TenantPublicPageValidationException("Indica un correo valido.", field);
            }

            return trimmed;
        }

        public string? NormalizeGoogleMapsUrl(string? value, string field) =>
            NormalizeAllowlistedHttpsUrl(value, field, GoogleMapsHosts, "Google Maps");

        public string? NormalizeWazeUrl(string? value, string field) =>
            NormalizeAllowlistedHttpsUrl(value, field, WazeHosts, "Waze");

        public string? NormalizeInstagramUrl(string? value, string field) =>
            NormalizeAllowlistedHttpsUrl(value, field, InstagramHosts, "Instagram");

        public string? NormalizeFacebookUrl(string? value, string field) =>
            NormalizeAllowlistedHttpsUrl(value, field, FacebookHosts, "Facebook");

        public string? NormalizeTikTokUrl(string? value, string field) =>
            NormalizeAllowlistedHttpsUrl(value, field, TikTokHosts, "TikTok");

        public string? BuildWhatsAppUrl(string? normalizedPhone)
        {
            if (string.IsNullOrWhiteSpace(normalizedPhone))
            {
                return null;
            }

            var digits = new string(normalizedPhone.Where(char.IsDigit).ToArray());
            return digits.Length == 0 ? null : $"https://wa.me/{digits}";
        }

        private static string? NormalizeAllowlistedHttpsUrl(
            string? value,
            string field,
            HashSet<string> allowedHosts,
            string label)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            ValidateLength(trimmed, 500, field);
            EnsureSafeUrlText(trimmed, field);

            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                ContainsDangerousScheme(trimmed) ||
                !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                !allowedHosts.Contains(uri.Host))
            {
                throw new TenantPublicPageValidationException(
                    $"Indica una URL segura de {label} que empiece con https://.",
                    field);
            }

            return uri.ToString();
        }

        private static void ValidateLength(string value, int maxLength, string field)
        {
            if (value.Length > maxLength)
            {
                throw new TenantPublicPageValidationException(
                    $"El campo no puede exceder {maxLength} caracteres.",
                    field);
            }
        }

        private static void EnsurePlainText(string value, string field)
        {
            if (value.Contains('<', StringComparison.Ordinal) ||
                value.Contains('>', StringComparison.Ordinal) ||
                value.Any(char.IsControl))
            {
                throw new TenantPublicPageValidationException(
                    "No se permiten HTML ni caracteres peligrosos en los datos publicos.",
                    field);
            }
        }

        private static void EnsureMultilinePlainText(string value, string field)
        {
            if (value.Contains('<', StringComparison.Ordinal) ||
                value.Contains('>', StringComparison.Ordinal) ||
                value.Any(character => char.IsControl(character) && character != '\n' && character != '\t'))
            {
                throw new TenantPublicPageValidationException(
                    "No se permiten HTML ni caracteres peligrosos en los datos publicos.",
                    field);
            }
        }

        private static void EnsureSafeUrlText(string value, string field)
        {
            EnsurePlainText(value, field);

            if (value.Contains('"', StringComparison.Ordinal) ||
                value.Contains('\'', StringComparison.Ordinal) ||
                value.Contains('`', StringComparison.Ordinal))
            {
                throw new TenantPublicPageValidationException(
                    "No se permiten HTML, scripts ni caracteres peligrosos en los datos publicos.",
                    field);
            }
        }

        private static bool ContainsDangerousScheme(string value)
        {
            var lowered = value.Trim().ToLowerInvariant();
            return lowered.StartsWith("javascript:", StringComparison.Ordinal) ||
                   lowered.StartsWith("data:", StringComparison.Ordinal) ||
                   lowered.StartsWith("file:", StringComparison.Ordinal) ||
                   lowered.StartsWith("blob:", StringComparison.Ordinal) ||
                   lowered.StartsWith("vbscript:", StringComparison.Ordinal);
        }
    }
}
