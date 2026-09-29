using LuxuryApp.Services.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ContractAcceptanceBindingHelperTests
    {
        [Theory]
        [MemberData(nameof(TruthySingleValueCases))]
        public void IsAccepted_ShouldTreatTruthySingleValuesAsAccepted(string rawValue)
        {
            var form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["AcceptCurrentContract"] = new StringValues(rawValue)
            });

            var accepted = ContractAcceptanceBindingHelper.IsAccepted(form, "AcceptCurrentContract");

            Assert.True(accepted);
        }

        [Theory]
        [MemberData(nameof(TruthyCombinedValueCases))]
        public void IsAccepted_ShouldTreatCombinedTruthyValuesAsAccepted(StringValues rawValue)
        {
            var form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["AcceptCurrentContract"] = rawValue
            });

            var accepted = ContractAcceptanceBindingHelper.IsAccepted(form, "AcceptCurrentContract");

            Assert.True(accepted);
        }

        [Theory]
        [MemberData(nameof(FalseyValueCases))]
        public void IsAccepted_ShouldTreatFalseyValuesAsRejected(StringValues rawValue)
        {
            var form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["AcceptCurrentContract"] = rawValue
            });
            var accepted = ContractAcceptanceBindingHelper.IsAccepted(form, "AcceptCurrentContract");

            Assert.False(accepted);
        }

        [Fact]
        public void IsAccepted_ShouldReturnFalseWhenFieldIsAbsent()
        {
            var form = new FormCollection(new Dictionary<string, StringValues>());

            var accepted = ContractAcceptanceBindingHelper.IsAccepted(form, "AcceptCurrentContract");

            Assert.False(accepted);
        }

        [Fact]
        public void NormalizeAcceptedValue_ShouldReturnCurrentValueWhenFieldIsAbsent()
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/x-www-form-urlencoded";

            var accepted = ContractAcceptanceBindingHelper.NormalizeAcceptedValue(
                context.Request,
                "AcceptCurrentContract",
                currentValue: false);

            Assert.False(accepted);
        }

        public static IEnumerable<object[]> TruthySingleValueCases()
        {
            yield return ["true"];
            yield return ["True"];
            yield return ["on"];
            yield return ["1"];
            yield return ["yes"];
            yield return [" YES "];
        }

        public static IEnumerable<object[]> TruthyCombinedValueCases()
        {
            yield return [new StringValues("true,false")];
            yield return [new StringValues("on,false")];
            yield return [new StringValues(["true", "false"])];
            yield return [new StringValues(["on", "false"])];
            yield return [new StringValues([" false ", " yes "])];
        }

        public static IEnumerable<object[]> FalseyValueCases()
        {
            yield return [new StringValues("false")];
            yield return [new StringValues("0")];
            yield return [new StringValues(string.Empty)];
            yield return [new StringValues("false,false")];
            yield return [new StringValues(["false", "0"])];
        }
    }
}
