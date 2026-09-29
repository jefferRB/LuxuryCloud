using LuxuryApp.Services.Account;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class AccountEmailServiceSecurityTests
    {
        [Fact]
        public void BuildResetEmailHtml_ShouldEncodeUserControlledFields()
        {
            var html = AccountEmailService.BuildResetEmailHtml(
                "<img src=x onerror=alert(1)>",
                "https://app.local/Accounts/ResetPassword?token=<token>&next=\"x\"");

            Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
            Assert.Contains("token=&lt;token&gt;&amp;next=&quot;x&quot;", html, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildEmailConfirmationHtml_ShouldEncodeUserControlledFields()
        {
            var html = AccountEmailService.BuildEmailConfirmationHtml(
                "<img src=x onerror=alert(1)>",
                "https://app.local/Accounts/ConfirmarEmail?token=<token>&next=\"x\"");

            Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
            Assert.Contains("token=&lt;token&gt;&amp;next=&quot;x&quot;", html, StringComparison.Ordinal);
        }
    }
}
