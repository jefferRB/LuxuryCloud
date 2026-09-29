using Microsoft.Extensions.Primitives;

namespace LuxuryApp.Services.Contracts
{
    public static class ContractAcceptanceBindingHelper
    {
        private static readonly string[] TruthyValues = ["true", "on", "1", "yes"];

        public static bool IsAccepted(IFormCollection form, string fieldName)
        {
            ArgumentNullException.ThrowIfNull(form);

            if (string.IsNullOrWhiteSpace(fieldName))
            {
                throw new ArgumentException("El nombre del campo no puede ser vacio.", nameof(fieldName));
            }

            return ExpandSubmittedValues(form, fieldName).Any(IsTruthyValue);
        }

        public static bool NormalizeAcceptedValue(
            HttpRequest request,
            string fieldName,
            bool currentValue)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!request.HasFormContentType)
            {
                return currentValue;
            }

            if (!request.Form.ContainsKey(fieldName))
            {
                return currentValue;
            }

            return IsAccepted(request.Form, fieldName);
        }

        public static IReadOnlyList<string> GetSubmittedValues(HttpRequest request, string fieldName)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!request.HasFormContentType)
            {
                return Array.Empty<string>();
            }

            return ExpandSubmittedValues(request.Form, fieldName);
        }

        private static string[] ExpandSubmittedValues(IFormCollection form, string fieldName)
        {
            if (!form.TryGetValue(fieldName, out StringValues values) || values.Count == 0)
            {
                return [];
            }

            return values
                .SelectMany(value => SplitCombinedValues(value))
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToArray();
        }

        private static IEnumerable<string?> SplitCombinedValues(string? value)
        {
            if (value is null)
            {
                yield return null;
                yield break;
            }

            var parts = value.Split(',', StringSplitOptions.None);
            foreach (var part in parts)
            {
                yield return part.Trim();
            }
        }

        private static bool IsTruthyValue(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            TruthyValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
