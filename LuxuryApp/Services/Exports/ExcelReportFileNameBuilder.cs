using System.Globalization;
using System.Text;

namespace LuxuryApp.Services.Exports
{
    public static class ExcelReportFileNameBuilder
    {
        private const int MaxSegmentLength = 80;

        public static string Build(
            string? tenantDisplayName,
            string reportName,
            DateTime generatedAt)
        {
            var tenantSegment = ToSafeSegment(tenantDisplayName, "LuxuryCloud");
            var reportSegment = ToSafeSegment(reportName, "Reporte");
            var dateSegment = generatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            return $"{tenantSegment}_{reportSegment}_{dateSegment}.xlsx";
        }

        public static string ToSafeSegment(string? value, string fallback)
        {
            var normalized = RemoveDiacritics(value);
            var tokens = SplitSafeTokens(normalized)
                .Select(ToTitleToken)
                .Where(token => token.Length > 0)
                .ToArray();

            var segment = string.Concat(tokens);
            if (string.IsNullOrWhiteSpace(segment))
            {
                segment = string.Concat(SplitSafeTokens(RemoveDiacritics(fallback)).Select(ToTitleToken));
            }

            if (string.IsNullOrWhiteSpace(segment))
            {
                segment = "Reporte";
            }

            return segment.Length <= MaxSegmentLength
                ? segment
                : segment[..MaxSegmentLength].TrimEnd('_', '-', '.');
        }

        private static IEnumerable<string> SplitSafeTokens(string value)
        {
            var current = new StringBuilder(value.Length);

            foreach (var character in value)
            {
                if (IsAsciiLetterOrDigit(character))
                {
                    current.Append(character);
                    continue;
                }

                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }
        }

        private static string ToTitleToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            if (token.Length == 1)
            {
                return token.ToUpperInvariant();
            }

            return char.ToUpperInvariant(token[0]) + token[1..];
        }

        private static bool IsAsciiLetterOrDigit(char character) =>
            (character >= 'A' && character <= 'Z') ||
            (character >= 'a' && character <= 'z') ||
            (character >= '0' && character <= '9');

        private static string RemoveDiacritics(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var character in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (category is UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.Surrogate)
                {
                    continue;
                }

                builder.Append(character);
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
