namespace LuxuryApp.Services.PublicImages
{
    /// <summary>
    /// Modo de encuadre solicitado por el cliente. Compatibilidad: si no viene FitMode, se asume
    /// <see cref="Cover"/> (comportamiento historico de recorte al aspecto del tipo).
    /// </summary>
    public enum PublicImageFitMode
    {
        /// <summary>Recorta la imagen para llenar el aspecto objetivo (rect del cliente o centrado).</summary>
        Cover = 0,

        /// <summary>La imagen completa entra en el canvas objetivo, rellenando margenes con fondo blur.</summary>
        Contain = 1,

        /// <summary>Igual que Contain: imagen completa + fondo blur (relleno "padded").</summary>
        Padded = 2,

        /// <summary>Conserva la proporcion original; solo redimensiona a los maximos permitidos.</summary>
        Original = 3
    }

    public sealed class PublicImageCropRequest
    {
        public int? CropX { get; set; }

        public int? CropY { get; set; }

        public int? CropWidth { get; set; }

        public int? CropHeight { get; set; }

        /// <summary>Aspecto objetivo (ancho/alto). Opcional; si no es sano se usa el default del tipo.</summary>
        public double? TargetAspectRatio { get; set; }

        /// <summary>Modo de encuadre en texto (Cover/Contain/Padded/Original). Default Cover.</summary>
        public string? FitMode { get; set; }

        public bool HasCrop =>
            CropX.HasValue &&
            CropY.HasValue &&
            CropWidth.HasValue &&
            CropHeight.HasValue;

        /// <summary>
        /// Parsea <see cref="FitMode"/> de forma tolerante. Si no viene o no se reconoce, decide
        /// el perfil del uso (<see cref="PublicImageProfile.DefaultFitMode"/>), no esta clase.
        /// </summary>
        public PublicImageFitMode ResolveFitMode(PublicImageFitMode fallback) =>
            Enum.TryParse<PublicImageFitMode>(FitMode, ignoreCase: true, out var mode)
                ? mode
                : fallback;
    }
}
