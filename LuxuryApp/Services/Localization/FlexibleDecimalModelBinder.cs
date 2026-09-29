using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Services.Localization
{
    public sealed class FlexibleDecimalModelBinder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            var valueProviderResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);

            if (valueProviderResult == ValueProviderResult.None)
            {
                return Task.CompletedTask;
            }

            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, valueProviderResult);

            var rawValue = valueProviderResult.FirstValue;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                if (IsNullable(bindingContext.ModelType))
                {
                    bindingContext.Result = ModelBindingResult.Success(null);
                }

                return Task.CompletedTask;
            }

            if (TryParseDecimal(rawValue, out var value))
            {
                bindingContext.Result = ModelBindingResult.Success(value);
                return Task.CompletedTask;
            }

            bindingContext.ModelState.TryAddModelError(
                bindingContext.ModelName,
                "El valor numerico no tiene un formato valido.");

            return Task.CompletedTask;
        }

        private static bool TryParseDecimal(string rawValue, out decimal value)
        {
            var styles = NumberStyles.Number | NumberStyles.AllowCurrencySymbol;

            if (decimal.TryParse(rawValue, styles, CultureInfo.CurrentCulture, out value) ||
                decimal.TryParse(rawValue, styles, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            return decimal.TryParse(NormalizeDecimalText(rawValue), styles, CultureInfo.InvariantCulture, out value);
        }

        private static string NormalizeDecimalText(string value)
        {
            var normalized = value
                .Trim()
                .Replace("₡", string.Empty, StringComparison.Ordinal)
                .Replace("\u00A0", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal);

            var commaIndex = normalized.LastIndexOf(',');
            var dotIndex = normalized.LastIndexOf('.');

            if (commaIndex >= 0 && dotIndex >= 0)
            {
                var decimalSeparator = commaIndex > dotIndex ? ',' : '.';
                var groupSeparator = decimalSeparator == ',' ? "." : ",";

                normalized = normalized.Replace(groupSeparator, string.Empty, StringComparison.Ordinal);
                return decimalSeparator == ','
                    ? normalized.Replace(',', '.')
                    : normalized;
            }

            if (commaIndex >= 0)
            {
                return ShouldTreatAsGroupSeparator(normalized, commaIndex)
                    ? normalized.Replace(",", string.Empty, StringComparison.Ordinal)
                    : normalized.Replace(',', '.');
            }

            if (dotIndex >= 0 && ShouldTreatAsGroupSeparator(normalized, dotIndex))
            {
                return normalized.Replace(".", string.Empty, StringComparison.Ordinal);
            }

            return normalized;
        }

        private static bool ShouldTreatAsGroupSeparator(string value, int separatorIndex)
        {
            var digitsAfterSeparator = value.Length - separatorIndex - 1;
            return digitsAfterSeparator == 3 && value.Count(character => character is ',' or '.') == 1;
        }

        private static bool IsNullable(Type type) =>
            !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
    }
}
