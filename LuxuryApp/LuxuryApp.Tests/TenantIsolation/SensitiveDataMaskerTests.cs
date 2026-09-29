using LuxuryApp.Services.Security;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class SensitiveDataMaskerTests
    {
        [Fact]
        public void MaskEmail_ShouldHideLocalPartAndKeepDomain()
        {
            var masked = SensitiveDataMasker.MaskEmail("cliente.real@example.com");

            Assert.Equal("c***@example.com", masked);
            Assert.DoesNotContain("cliente.real", masked, StringComparison.Ordinal);
        }

        [Fact]
        public void MaskPhone_ShouldKeepOnlyLastFourDigits()
        {
            var masked = SensitiveDataMasker.MaskPhone("+506 8888-1234");

            Assert.Equal("***1234", masked);
            Assert.DoesNotContain("8888", masked, StringComparison.Ordinal);
        }

        [Fact]
        public void MaskToken_ShouldExposeOnlySuffix()
        {
            var masked = SensitiveDataMasker.MaskToken("tok_live_abcdefghijklmnopqrstuvwxyz");

            Assert.Equal("***wxyz", masked);
            Assert.DoesNotContain("tok_live", masked, StringComparison.Ordinal);
            Assert.DoesNotContain("abcdefghijklmnopqrstuv", masked, StringComparison.Ordinal);
        }

        [Fact]
        public void RedactQueryString_ShouldRedactSensitiveValues()
        {
            var redacted = SensitiveDataMasker.RedactQueryString(
                "?event=payment&token=secret-token&lc_email=cliente.real@example.com&code=1");

            Assert.Equal("?event=payment&token=***redacted***&lc_email=***redacted***&code=1", redacted);
            Assert.DoesNotContain("secret-token", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("cliente.real@example.com", redacted, StringComparison.Ordinal);
        }

        [Fact]
        public void RedactUrl_ShouldRemoveSensitiveQueryParameters()
        {
            var redacted = SensitiveDataMasker.RedactUrl(
                "https://app.local/api/webhooks/tilopay?access_token=secret-token&event=payment");

            Assert.Equal("https://app.local/api/webhooks/tilopay?event=payment", redacted);
            Assert.DoesNotContain("access_token", redacted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-token", redacted, StringComparison.Ordinal);
        }
    }
}
