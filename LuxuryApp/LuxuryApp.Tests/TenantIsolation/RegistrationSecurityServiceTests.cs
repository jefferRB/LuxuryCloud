using LuxuryApp.Services.Security;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class RegistrationSecurityServiceTests
    {
        [Fact]
        public void ValidateRegistration_ShouldRejectSuspiciousDottedEmail()
        {
            var service = CreateService();

            var result = service.ValidateRegistration(
                "a.b.c.d.e.f.ghi.jk.42.7@example.com",
                "Luxury Studio");

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.FieldName == "Email");
        }

        [Fact]
        public void ValidateRegistration_ShouldRejectHtmlBusinessName()
        {
            var service = CreateService();

            var result = service.ValidateRegistration(
                "owner@example.com",
                "<script>alert(1)</script>");

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.FieldName == "Name");
        }

        [Fact]
        public void CheckRateLimit_ShouldLimitByEmail()
        {
            var service = CreateService(options =>
            {
                options.Registration.IpPermitLimit = 100;
                options.Registration.EmailPermitLimit = 2;
                options.Registration.WindowMinutes = 10;
            });

            Assert.True(service.CheckRateLimit(
                RegistrationSecurityActions.Registration,
                "127.0.0.1",
                "owner@example.com").Allowed);
            Assert.True(service.CheckRateLimit(
                RegistrationSecurityActions.Registration,
                "127.0.0.1",
                "owner@example.com").Allowed);
            Assert.False(service.CheckRateLimit(
                RegistrationSecurityActions.Registration,
                "127.0.0.1",
                "owner@example.com").Allowed);
        }

        private static RegistrationSecurityService CreateService(
            Action<RegistrationSecurityOptions>? configure = null)
        {
            var options = new RegistrationSecurityOptions();
            configure?.Invoke(options);

            return new RegistrationSecurityService(
                new MemoryCache(new MemoryCacheOptions()),
                new StaticOptionsMonitor<RegistrationSecurityOptions>(options),
                NullLogger<RegistrationSecurityService>.Instance);
        }
    }
}
