using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace LuxuryApp.Tests.Identity
{
    public class AuthCookiePolicyTests
    {
        [Fact]
        public void ConfigureApplicationCookie_ShouldSet30DaySlidingSecureCookie()
        {
            var options = new CookieAuthenticationOptions();

            AuthCookiePolicy.ConfigureApplicationCookie(options, isDevelopment: false);

            Assert.Equal(TimeSpan.FromDays(30), options.ExpireTimeSpan);
            Assert.True(options.SlidingExpiration);
            Assert.True(options.Cookie.HttpOnly);
            Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
            Assert.Equal("/", options.Cookie.Path);
            Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
            Assert.Equal("/Accounts/Acceso", options.LoginPath);
            Assert.Equal("/Accounts/Bloqueado", options.AccessDeniedPath);
        }

        [Fact]
        public void ConfigureApplicationCookie_InDevelopment_ShouldRelaxSecurePolicyOnly()
        {
            var options = new CookieAuthenticationOptions();

            AuthCookiePolicy.ConfigureApplicationCookie(options, isDevelopment: true);

            Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
            // El resto de las banderas de seguridad se conservan también en desarrollo.
            Assert.True(options.Cookie.HttpOnly);
            Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
            Assert.Equal(TimeSpan.FromDays(30), options.ExpireTimeSpan);
        }

        [Fact]
        public void ConfigureApplicationCookie_ShouldNotOverrideDefaultCookieName()
        {
            // Conservar el nombre por defecto evita expulsar a los usuarios ya autenticados.
            var options = new CookieAuthenticationOptions();
            var defaultName = options.Cookie.Name;

            AuthCookiePolicy.ConfigureApplicationCookie(options, isDevelopment: false);

            Assert.Equal(defaultName, options.Cookie.Name);
        }

        [Fact]
        public void ResolveDataProtectionKeysPath_Development_WithoutConfig_ShouldReturnNull()
        {
            var resolved = AuthCookiePolicy.ResolveDataProtectionKeysPath(
                configuredPath: null,
                isDevelopment: true);

            Assert.Null(resolved);
        }

        [Fact]
        public void ResolveDataProtectionKeysPath_Production_WithoutConfig_ShouldUseStablePath()
        {
            var resolved = AuthCookiePolicy.ResolveDataProtectionKeysPath(
                configuredPath: "   ",
                isDevelopment: false);

            Assert.Equal("/var/lib/luxury/dataprotection-keys", resolved);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ResolveDataProtectionKeysPath_WithConfiguredPath_ShouldUseItInAnyEnvironment(bool isDevelopment)
        {
            var resolved = AuthCookiePolicy.ResolveDataProtectionKeysPath(
                configuredPath: @"C:\keys\luxury",
                isDevelopment);

            Assert.Equal(@"C:\keys\luxury", resolved);
        }

        [Fact]
        public void DataProtectionApplicationName_ShouldRemainStable()
        {
            // Cambiar este nombre invalidaría todas las cookies existentes.
            Assert.Equal("Luxury", AuthCookiePolicy.DataProtectionApplicationName);
        }
    }
}
