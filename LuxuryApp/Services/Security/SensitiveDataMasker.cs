namespace LuxuryApp.Services.Security
{
    public static class SensitiveDataMasker
    {
        public const string Redacted = "***redacted***";

        public static string MaskEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return string.Empty;
            }

            var trimmed = email.Trim();
            var atIndex = trimmed.LastIndexOf('@');
            if (atIndex <= 0 || atIndex == trimmed.Length - 1)
            {
                return MaskToken(trimmed);
            }

            var localPart = trimmed[..atIndex];
            var domain = trimmed[(atIndex + 1)..];
            var maskedLocalPart = localPart.Length == 1
                ? "*"
                : $"{localPart[0]}***";

            return $"{maskedLocalPart}@{domain}";
        }

        public static string MaskPhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
            {
                return string.Empty;
            }

            var digits = new string(phone.Where(char.IsDigit).ToArray());
            return digits.Length > 4
                ? $"***{digits[^4..]}"
                : "***";
        }

        public static string MaskToken(string? token, int visibleSuffixLength = 4)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            var trimmed = token.Trim();
            if (visibleSuffixLength <= 0 || trimmed.Length <= visibleSuffixLength)
            {
                return "***";
            }

            return $"***{trimmed[^visibleSuffixLength..]}";
        }

        public static string MaskReference(string? reference, int visibleSuffixLength = 4) =>
            MaskToken(reference, visibleSuffixLength);

        public static string RedactQueryString(string? queryString)
        {
            if (string.IsNullOrWhiteSpace(queryString))
            {
                return string.Empty;
            }

            var trimmed = queryString.Trim();
            var hasQuestionMark = trimmed.StartsWith("?", StringComparison.Ordinal);
            var query = hasQuestionMark ? trimmed[1..] : trimmed;

            if (string.IsNullOrWhiteSpace(query))
            {
                return hasQuestionMark ? "?" : string.Empty;
            }

            var redactedParts = query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(RedactQueryPart);

            return (hasQuestionMark ? "?" : string.Empty) + string.Join("&", redactedParts);
        }

        public static string RedactUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            var trimmed = url.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
                return queryIndex < 0
                    ? trimmed
                    : trimmed[..queryIndex] + RemoveSensitiveQueryString(trimmed[queryIndex..]);
            }

            var builder = new UriBuilder(uri)
            {
                Query = RemoveSensitiveQueryString(uri.Query).TrimStart('?')
            };

            return builder.Uri.ToString();
        }

        public static bool IsSensitiveKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("auth", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("key", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("email", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("correo", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("phone", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("telefono", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("wa_id", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("waid", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("cvv", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("cvc", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("pan", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("card", StringComparison.OrdinalIgnoreCase);
        }

        private static string RedactQueryPart(string part)
        {
            var equalsIndex = part.IndexOf('=', StringComparison.Ordinal);
            var key = equalsIndex >= 0 ? part[..equalsIndex] : part;
            var decodedKey = DecodeQueryKey(key);

            if (!IsSensitiveKey(decodedKey))
            {
                return part;
            }

            return equalsIndex >= 0 ? $"{key}={Redacted}" : $"{key}={Redacted}";
        }

        private static string RemoveSensitiveQueryString(string? queryString)
        {
            if (string.IsNullOrWhiteSpace(queryString))
            {
                return string.Empty;
            }

            var trimmed = queryString.Trim();
            var hasQuestionMark = trimmed.StartsWith("?", StringComparison.Ordinal);
            var query = hasQuestionMark ? trimmed[1..] : trimmed;
            if (string.IsNullOrWhiteSpace(query))
            {
                return string.Empty;
            }

            var retainedParts = query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part =>
                {
                    var equalsIndex = part.IndexOf('=', StringComparison.Ordinal);
                    var key = equalsIndex >= 0 ? part[..equalsIndex] : part;
                    return !IsSensitiveKey(DecodeQueryKey(key));
                })
                .ToArray();

            return retainedParts.Length == 0
                ? string.Empty
                : (hasQuestionMark ? "?" : string.Empty) + string.Join("&", retainedParts);
        }

        private static string DecodeQueryKey(string key)
        {
            try
            {
                return Uri.UnescapeDataString(key.Replace("+", " ", StringComparison.Ordinal));
            }
            catch (UriFormatException)
            {
                return key;
            }
        }
    }
}
