using LuxuryApp.Models.Identity;
using LuxuryApp.Services.Identity;

namespace LuxuryApp.Tests.Identity
{
    public class SuperAdminPasswordValidatorTests
    {
        private static readonly SuperAdminPasswordValidator Validator = new();

        [Theory]
        [InlineData("Once11chars")]
        [InlineData("Abc12345")]
        [InlineData(null)]
        public async Task ValidateAsync_ShouldRejectShortPasswordForSuperAdmin(string? password)
        {
            var superAdmin = new AppUsuario { IsPlatformSuperAdmin = true };

            var result = await Validator.ValidateAsync(null!, superAdmin, password);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, error => error.Code == "SuperAdminPasswordTooShort");
        }

        [Fact]
        public async Task ValidateAsync_ShouldAcceptTwelveCharactersForSuperAdmin()
        {
            var superAdmin = new AppUsuario { IsPlatformSuperAdmin = true };

            var result = await Validator.ValidateAsync(null!, superAdmin, "Abcdef123456");

            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task ValidateAsync_ShouldNotRestrictRegularUsers()
        {
            var regularUser = new AppUsuario { IsPlatformSuperAdmin = false };

            var result = await Validator.ValidateAsync(null!, regularUser, "Abc12345");

            Assert.True(result.Succeeded);
        }
    }
}
