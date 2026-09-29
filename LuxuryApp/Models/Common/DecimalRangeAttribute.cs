using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace LuxuryApp.Models.Common
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
    public sealed class DecimalRangeAttribute : ValidationAttribute
    {
        private readonly decimal _minimum;
        private readonly decimal _maximum;

        public DecimalRangeAttribute(double minimum, double maximum)
        {
            _minimum = Convert.ToDecimal(minimum);
            _maximum = Convert.ToDecimal(maximum);
        }

        public override bool IsValid(object? value)
        {
            if (value is null)
            {
                return true;
            }

            return value is decimal decimalValue
                && decimalValue >= _minimum
                && decimalValue <= _maximum;
        }

        public override string FormatErrorMessage(string name)
        {
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                return ErrorMessage!;
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                "El valor de {0} debe estar entre {1} y {2}.",
                name,
                _minimum,
                _maximum);
        }
    }
}
