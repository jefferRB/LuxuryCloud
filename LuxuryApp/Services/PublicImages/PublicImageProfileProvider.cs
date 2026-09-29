using LuxuryApp.Models.PublicPages;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Services.PublicImages
{
    /// <summary>
    /// Registro de perfiles por uso. Las dimensiones y limites vienen de
    /// <see cref="PublicImageOptions"/> para que sigan siendo configurables por ambiente.
    /// </summary>
    public sealed class PublicImageProfileProvider : IPublicImageProfileProvider
    {
        private readonly IReadOnlyDictionary<TenantPublicAssetType, PublicImageProfile> _profiles;
        private readonly PublicImageProfile _fallback;

        public PublicImageProfileProvider(IOptions<PublicImageOptions> options)
        {
            var settings = options.Value;

            var logo = new PublicImageProfile
            {
                Usage = TenantPublicAssetType.Logo,
                RecommendedAspectRatio = 1d,
                OutputMaxWidth = settings.LogoMaxWidth,
                OutputMaxHeight = settings.LogoMaxHeight,
                DefaultFitMode = PublicImageFitMode.Contain,
                PreserveTransparency = true,
                Quality = settings.LogoQuality,
                MaxUploadBytes = settings.MaxLogoBytes,
                CropPresets = new[]
                {
                    // El logo no se recorta agresivamente: se contiene y conserva transparencia.
                    PublicImageCropPreset.Contain("1:1", "Ajustar sin recortar"),
                    PublicImageCropPreset.Cover("1:1", "Recortar cuadrado")
                }
            };

            var cover = new PublicImageProfile
            {
                Usage = TenantPublicAssetType.Cover,
                RecommendedAspectRatio = 16d / 9d,
                OutputMaxWidth = settings.CoverMaxWidth,
                OutputMaxHeight = settings.CoverMaxHeight,
                DefaultFitMode = PublicImageFitMode.Cover,
                Quality = settings.DefaultQuality,
                MaxUploadBytes = settings.MaxCoverBytes,
                VerticalPresetToken = "padded:16:9",
                CropPresets = new[]
                {
                    PublicImageCropPreset.Cover("16:9", "Portada 16:9"),
                    PublicImageCropPreset.Cover("2:1", "Portada amplia 2:1"),
                    PublicImageCropPreset.Padded("16:9", "Completa con fondo")
                }
            };

            var service = new PublicImageProfile
            {
                Usage = TenantPublicAssetType.ServiceMain,
                // Marco vertical 3:4: es lo que sale de la camara del celular en vertical, asi la
                // duena del negocio sube su foto tal cual. Es fijo para toda la fila, de modo que
                // ninguna foto puede estirar el grid.
                RecommendedAspectRatio = 3d / 4d,
                OutputMaxWidth = settings.ServiceImageMaxWidth,
                OutputMaxHeight = settings.ServiceImageMaxHeight,
                DefaultFitMode = PublicImageFitMode.Cover,
                Quality = settings.DefaultQuality,
                MaxUploadBytes = settings.MaxServiceImageBytes,
                CropPresets = new[]
                {
                    PublicImageCropPreset.Cover("3:4", "Recortar / rellenar"),
                    PublicImageCropPreset.Padded("3:4", "Completa con fondo")
                }
            };

            var gallery = new PublicImageProfile
            {
                Usage = TenantPublicAssetType.BusinessGallery,
                RecommendedAspectRatio = 4d / 5d,
                OutputMaxWidth = settings.GalleryMaxWidth,
                OutputMaxHeight = settings.GalleryMaxHeight,
                DefaultFitMode = PublicImageFitMode.Original,
                Quality = settings.DefaultQuality,
                MaxUploadBytes = settings.MaxGalleryImageBytes,
                CropPresets = new[]
                {
                    PublicImageCropPreset.Original(),
                    PublicImageCropPreset.Cover("4:5", "Vertical 4:5"),
                    PublicImageCropPreset.Cover("1:1", "Cuadrado 1:1")
                }
            };

            var location = new PublicImageProfile
            {
                Usage = TenantPublicAssetType.Location,
                RecommendedAspectRatio = 4d / 3d,
                OutputMaxWidth = settings.LocationMaxWidth,
                OutputMaxHeight = settings.LocationMaxHeight,
                DefaultFitMode = PublicImageFitMode.Cover,
                Quality = settings.DefaultQuality,
                MaxUploadBytes = settings.MaxCoverBytes,
                CropPresets = new[]
                {
                    PublicImageCropPreset.Cover("4:3", "Horizontal 4:3"),
                    PublicImageCropPreset.Original()
                }
            };

            _profiles = new Dictionary<TenantPublicAssetType, PublicImageProfile>
            {
                [TenantPublicAssetType.Logo] = logo,
                [TenantPublicAssetType.Cover] = cover,
                [TenantPublicAssetType.ServiceMain] = service,
                [TenantPublicAssetType.ServiceGallery] = service with { Usage = TenantPublicAssetType.ServiceGallery },
                [TenantPublicAssetType.BusinessGallery] = gallery,
                [TenantPublicAssetType.Location] = location
            };

            _fallback = gallery;
        }

        public PublicImageProfile Get(TenantPublicAssetType usage) =>
            _profiles.TryGetValue(usage, out var profile) ? profile : _fallback;
    }
}
