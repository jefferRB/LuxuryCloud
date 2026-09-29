using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.PublicPages;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PublicUrlValidationServiceTests
    {
        private readonly PublicUrlValidationService _service = new();

        [Theory]
        [InlineData("javascript:alert(1)")]
        [InlineData("data:text/html,<script>alert(1)</script>")]
        [InlineData("//instagram.com/luxury")]
        [InlineData("http://instagram.com/luxury")]
        [InlineData("/perfil")]
        [InlineData("https://evil.example/luxury")]
        public void NormalizeInstagramUrl_RejectsUnsafeUrls(string url)
        {
            var ex = Assert.Throws<TenantPublicPageValidationException>(() =>
                _service.NormalizeInstagramUrl(url, "InstagramUrl"));

            Assert.Equal("InstagramUrl", ex.Field);
        }

        [Fact]
        public void NormalizeAllowlistedUrls_AcceptsExpectedHosts()
        {
            Assert.Equal(
                "https://www.instagram.com/luxury",
                _service.NormalizeInstagramUrl("https://www.instagram.com/luxury", "InstagramUrl"));

            Assert.Equal(
                "https://maps.app.goo.gl/abc",
                _service.NormalizeGoogleMapsUrl("https://maps.app.goo.gl/abc", "GoogleMapsUrl"));

            Assert.Equal(
                "https://waze.com/ul?ll=9.9,-84.1&navigate=yes",
                _service.NormalizeWazeUrl("https://waze.com/ul?ll=9.9,-84.1&navigate=yes", "WazeUrl"));

            Assert.Equal(
                "https://www.facebook.com/luxury",
                _service.NormalizeFacebookUrl("https://www.facebook.com/luxury", "FacebookUrl"));

            Assert.Equal(
                "https://www.tiktok.com/@luxury",
                _service.NormalizeTikTokUrl("https://www.tiktok.com/@luxury", "TikTokUrl"));
        }

        [Fact]
        public void NormalizeWhatsAppPhone_StoresDigitsAndBuildsServerSideLink()
        {
            var normalized = _service.NormalizeWhatsAppPhone("+506 8888-7777", "WhatsAppPhone");

            Assert.Equal("50688887777", normalized);
            Assert.Equal("https://wa.me/50688887777", _service.BuildWhatsAppUrl(normalized));
        }

        [Fact]
        public void NormalizePlainText_RejectsHtml()
        {
            Assert.Throws<TenantPublicPageValidationException>(() =>
                _service.NormalizePlainText("<b>Promo</b>", 120, "HeroTitle"));
        }

        [Fact]
        public void NormalizeMultilinePlainText_AllowsLineBreaksButRejectsHtml()
        {
            var normalized = _service.NormalizeMultilinePlainText("Lun a Vie\n9 a.m. - 6 p.m.", 500, "BusinessHours");

            Assert.Contains("9 a.m.", normalized);
            Assert.Throws<TenantPublicPageValidationException>(() =>
                _service.NormalizeMultilinePlainText("Lun\n<script>alert(1)</script>", 500, "BusinessHours"));
        }

        [Theory]
        [InlineData("https://evil.example/ul")]
        [InlineData("http://waze.com/ul")]
        [InlineData("javascript:alert(1)")]
        public void NormalizeWazeUrl_RejectsUnsafeUrls(string url)
        {
            var ex = Assert.Throws<TenantPublicPageValidationException>(() =>
                _service.NormalizeWazeUrl(url, "WazeUrl"));

            Assert.Equal("WazeUrl", ex.Field);
        }
    }
}
