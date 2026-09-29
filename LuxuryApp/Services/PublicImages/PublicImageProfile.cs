using LuxuryApp.Models.PublicPages;

namespace LuxuryApp.Services.PublicImages
{
    /// <summary>
    /// Opcion de encuadre que se ofrece al cliente para un uso concreto de imagen.
    /// El token es el contrato con el cropper del navegador
    /// (<c>wwwroot/js/tenant-public-image-uploader.js</c>): "original" | "&lt;fit&gt;:W:H".
    /// </summary>
    public sealed record PublicImageCropPreset
    {
        private PublicImageCropPreset(
            PublicImageFitMode fitMode,
            string? aspectToken,
            double? aspectRatio,
            string label)
        {
            FitMode = fitMode;
            AspectToken = aspectToken;
            AspectRatio = aspectRatio;
            Label = label;
        }

        public PublicImageFitMode FitMode { get; }

        /// <summary>Aspecto en texto ("1:1", "16:9"). Null en modo Original.</summary>
        public string? AspectToken { get; }

        /// <summary>Aspecto numerico (ancho/alto). Null en modo Original.</summary>
        public double? AspectRatio { get; }

        public string Label { get; }

        public string Token => AspectToken is null
            ? "original"
            : $"{FitMode.ToString().ToLowerInvariant()}:{AspectToken}";

        /// <summary>Recorta para llenar el marco del aspecto indicado.</summary>
        public static PublicImageCropPreset Cover(string aspectToken, string label) =>
            new(PublicImageFitMode.Cover, aspectToken, ParseAspect(aspectToken), label);

        /// <summary>Imagen completa dentro del marco, con fondo desenfocado de la misma foto.</summary>
        public static PublicImageCropPreset Padded(string aspectToken, string label) =>
            new(PublicImageFitMode.Padded, aspectToken, ParseAspect(aspectToken), label);

        /// <summary>Imagen completa dentro del marco, sin fondo (conserva transparencia).</summary>
        public static PublicImageCropPreset Contain(string aspectToken, string label) =>
            new(PublicImageFitMode.Contain, aspectToken, ParseAspect(aspectToken), label);

        /// <summary>Conserva la proporcion original de la foto.</summary>
        public static PublicImageCropPreset Original(string label = "Original") =>
            new(PublicImageFitMode.Original, null, null, label);

        private static double ParseAspect(string aspectToken)
        {
            var parts = aspectToken.Split(':');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width) &&
                double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var height) &&
                width > 0 &&
                height > 0)
            {
                return width / height;
            }

            throw new ArgumentException($"Aspecto invalido: '{aspectToken}'.", nameof(aspectToken));
        }
    }

    /// <summary>
    /// Reglas de una imagen segun PARA QUE se usa (portada, servicio, galeria, ubicacion, logo).
    /// Es la unica fuente de verdad: el backend procesa con estos valores y la vista de
    /// configuracion arma el cropper con los mismos presets. Agregar un uso nuevo
    /// (por ejemplo foto de colaborador) es agregar un perfil, no tocar controllers ni JS.
    /// </summary>
    public sealed record PublicImageProfile
    {
        public required TenantPublicAssetType Usage { get; init; }

        /// <summary>Aspecto sugerido cuando el cliente no manda uno (ancho/alto).</summary>
        public required double RecommendedAspectRatio { get; init; }

        public required int OutputMaxWidth { get; init; }

        public required int OutputMaxHeight { get; init; }

        public required PublicImageFitMode DefaultFitMode { get; init; }

        /// <summary>Opciones que se muestran en el modal de encuadre, en orden.</summary>
        public required IReadOnlyList<PublicImageCropPreset> CropPresets { get; init; }

        /// <summary>Preset preferido cuando la foto viene vertical (celular). Null = el primero.</summary>
        public string? VerticalPresetToken { get; init; }

        /// <summary>Logos: el canvas queda transparente en lugar de rellenarse.</summary>
        public bool PreserveTransparency { get; init; }

        public required int Quality { get; init; }

        /// <summary>Limite de seguridad del archivo subido (no del resultado almacenado).</summary>
        public required long MaxUploadBytes { get; init; }

        /// <summary>Token del preset por defecto, usado por el cropper.</summary>
        public string DefaultPresetToken => CropPresets.Count > 0 ? CropPresets[0].Token : "original";

        /// <summary>Lista de tokens separada por coma para el atributo <c>data-crop-presets</c>.</summary>
        public string CropPresetTokens => string.Join(",", CropPresets.Select(preset => preset.Token));

        /// <summary>Etiquetas separadas por "|", en el mismo orden que <see cref="CropPresetTokens"/>.</summary>
        public string CropPresetLabels => string.Join("|", CropPresets.Select(preset => preset.Label));

        public bool AllowsFitMode(PublicImageFitMode fitMode) =>
            CropPresets.Any(preset => preset.FitMode == fitMode);
    }
}
