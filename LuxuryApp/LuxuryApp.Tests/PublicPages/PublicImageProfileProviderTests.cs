using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.PublicImages;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.PublicPages
{
    /// <summary>
    /// El perfil es la unica definicion de "que se puede hacer con cada imagen". Si estas
    /// reglas cambian sin querer, cambia el marco de la landing.
    /// </summary>
    public class PublicImageProfileProviderTests
    {
        private static PublicImageProfileProvider CreateProvider(PublicImageOptions? options = null) =>
            new(Options.Create(options ?? new PublicImageOptions()));

        [Fact]
        public void Service_UsesVerticalThreeFourFrameWithCropAndPaddedOnly()
        {
            var profile = CreateProvider().Get(TenantPublicAssetType.ServiceMain);

            // 3:4 = foto de celular en vertical, sin pedirle al negocio que adapte nada.
            Assert.Equal(0.75d, profile.RecommendedAspectRatio, 3);
            Assert.Equal(PublicImageFitMode.Cover, profile.DefaultFitMode);
            Assert.Equal("cover:3:4,padded:3:4", profile.CropPresetTokens);
            Assert.Equal("Recortar / rellenar|Completa con fondo", profile.CropPresetLabels);
            Assert.False(profile.AllowsFitMode(PublicImageFitMode.Original));
            Assert.False(profile.PreserveTransparency);
        }

        [Fact]
        public void Service_OutputBoxIsTenEightyByFourteenFortyAndMatchesTheFrame()
        {
            var profile = CreateProvider().Get(TenantPublicAssetType.ServiceMain);

            Assert.Equal(1080, profile.OutputMaxWidth);
            Assert.Equal(1440, profile.OutputMaxHeight);
            Assert.Equal(
                profile.RecommendedAspectRatio,
                (double)profile.OutputMaxWidth / profile.OutputMaxHeight,
                3);
        }

        [Fact]
        public void Cover_OffersOnlyHeroFriendlyRatios()
        {
            var profile = CreateProvider().Get(TenantPublicAssetType.Cover);

            Assert.Equal("cover:16:9,cover:2:1,padded:16:9", profile.CropPresetTokens);
            Assert.Equal("padded:16:9", profile.VerticalPresetToken);
            Assert.Equal(16d / 9d, profile.RecommendedAspectRatio, 3);
        }

        [Fact]
        public void Gallery_AllowsOriginalAndIsTheDefault()
        {
            var profile = CreateProvider().Get(TenantPublicAssetType.BusinessGallery);

            Assert.Equal("original,cover:4:5,cover:1:1", profile.CropPresetTokens);
            Assert.Equal("original", profile.DefaultPresetToken);
            Assert.Equal(PublicImageFitMode.Original, profile.DefaultFitMode);
        }

        [Fact]
        public void Location_PrefersFourThreeThenOriginal()
        {
            var profile = CreateProvider().Get(TenantPublicAssetType.Location);

            Assert.Equal("cover:4:3,original", profile.CropPresetTokens);
            Assert.Equal("cover:4:3", profile.DefaultPresetToken);
        }

        [Fact]
        public void Logo_ContainsWithoutHardCropAndKeepsTransparency()
        {
            var profile = CreateProvider().Get(TenantPublicAssetType.Logo);

            Assert.Equal(PublicImageFitMode.Contain, profile.DefaultFitMode);
            Assert.Equal("contain:1:1", profile.DefaultPresetToken);
            Assert.True(profile.PreserveTransparency);
        }

        [Fact]
        public void Profiles_TakeDimensionsAndLimitsFromOptions()
        {
            var options = new PublicImageOptions
            {
                CoverMaxWidth = 1600,
                CoverMaxHeight = 900,
                MaxCoverBytes = 7 * 1024 * 1024
            };

            var profile = CreateProvider(options).Get(TenantPublicAssetType.Cover);

            Assert.Equal(1600, profile.OutputMaxWidth);
            Assert.Equal(900, profile.OutputMaxHeight);
            Assert.Equal(7 * 1024 * 1024, profile.MaxUploadBytes);
        }

        [Fact]
        public void CropPresetLabels_MatchTokenOrderAndHideTechnicalDetails()
        {
            var profile = CreateProvider().Get(TenantPublicAssetType.ServiceMain);

            Assert.Equal(
                profile.CropPresetTokens.Split(',').Length,
                profile.CropPresetLabels.Split('|').Length);

            // La duena del negocio no deberia leer proporciones ni tamanos en la interfaz.
            Assert.DoesNotContain("3:4", profile.CropPresetLabels);
            Assert.DoesNotContain("1080", profile.CropPresetLabels);
        }

        [Fact]
        public void Get_UnknownUsage_FallsBackInsteadOfThrowing()
        {
            var profile = CreateProvider().Get((TenantPublicAssetType)999);

            Assert.NotNull(profile);
            Assert.True(profile.OutputMaxWidth > 0);
        }
    }
}
