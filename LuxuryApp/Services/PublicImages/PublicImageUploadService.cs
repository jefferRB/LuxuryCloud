using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.Tenant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LuxuryApp.Services.PublicImages
{
    public sealed class PublicImageUploadService : IPublicImageUploadService
    {
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".webp"
        };

        private const string UnsupportedFormatMessage =
            "El archivo no es una imagen JPG, PNG o WEBP valida. Si viene de un iPhone en formato HEIC, " +
            "abri la foto y compartila como JPG, o cambia Ajustes > Camara > Formatos a \"Mas compatible\".";

        private const string UnreadableImageMessage =
            "No pudimos leer la imagen: puede estar danada o incompleta. Intenta con otra foto en JPG, PNG o WEBP.";

        private const string HeicMessage =
            "Las fotos HEIC/HEIF de iPhone no se pueden procesar. En tu iPhone entra a " +
            "Ajustes > Camara > Formatos y elegi \"Mas compatible\", o comparti la foto para convertirla a JPG.";

        private static readonly string[] DangerousExtensions =
        {
            ".svg",
            ".gif",
            ".pdf",
            ".html",
            ".htm",
            ".js",
            ".zip",
            ".exe",
            ".cmd",
            ".bat",
            ".ps1"
        };

        private readonly ApplicationDbContext _context;
        private readonly ITenantProvider _tenantProvider;
        private readonly IPublicImageStorageService _storage;
        private readonly IPublicAssetQuotaService _quotaService;
        private readonly IUploadedFileSecurityScanner _securityScanner;
        private readonly IPublicImageProfileProvider _profileProvider;
        private readonly PublicImageOptions _options;
        private readonly ILogger<PublicImageUploadService> _logger;

        public PublicImageUploadService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            IPublicImageStorageService storage,
            IPublicAssetQuotaService quotaService,
            IUploadedFileSecurityScanner securityScanner,
            IPublicImageProfileProvider profileProvider,
            IOptions<PublicImageOptions> options,
            ILogger<PublicImageUploadService> logger)
        {
            _context = context;
            _tenantProvider = tenantProvider;
            _storage = storage;
            _quotaService = quotaService;
            _securityScanner = securityScanner;
            _profileProvider = profileProvider;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<TenantPublicAsset> UploadPublicPageAssetAsync(
            TenantPublicAssetType assetType,
            IFormFile? file,
            string? userId,
            CancellationToken cancellationToken = default,
            PublicImageCropRequest? crop = null)
        {
            EnsurePublicPageAssetType(assetType);
            var tenantId = ResolveTenantId();
            var page = await GetOrCreatePageAsync(cancellationToken);
            var processed = await ProcessImageAsync(file, assetType, crop, cancellationToken);
            var replacingAsset = IsSingleton(assetType)
                ? await FindActiveSingletonAsync(assetType, null, cancellationToken)
                : null;

            if (assetType == TenantPublicAssetType.BusinessGallery)
            {
                await EnsureGalleryLimitAsync(
                    tenantId,
                    assetType,
                    null,
                    _options.MaxBusinessGalleryImages,
                    cancellationToken);
            }

            try
            {
                return await SaveProcessedAssetAsync(
                    tenantId,
                    page.Id,
                    null,
                    assetType,
                    processed,
                    replacingAsset,
                    userId,
                    cancellationToken);
            }
            finally
            {
                processed.Content.Dispose();
            }
        }

        public async Task<TenantPublicAsset> UploadServiceAssetAsync(
            TenantPublicAssetType assetType,
            int serviceId,
            IFormFile? file,
            string? userId,
            CancellationToken cancellationToken = default,
            PublicImageCropRequest? crop = null)
        {
            // Solo se admite una imagen principal por servicio. La galeria/trabajos por servicio
            // fue retirada del producto.
            if (assetType is not TenantPublicAssetType.ServiceMain)
            {
                throw new PublicImageUploadException("Tipo de imagen de servicio invalido.");
            }

            var tenantId = ResolveTenantId();
            await EnsureServiceBelongsToCurrentTenantAsync(serviceId, cancellationToken);
            var processed = await ProcessImageAsync(file, assetType, crop, cancellationToken);
            var replacingAsset = await FindActiveSingletonAsync(assetType, serviceId, cancellationToken);

            try
            {
                return await SaveProcessedAssetAsync(
                    tenantId,
                    null,
                    serviceId,
                    assetType,
                    processed,
                    replacingAsset,
                    userId,
                    cancellationToken);
            }
            finally
            {
                processed.Content.Dispose();
            }
        }

        public async Task RemovePublicPageSingletonAsync(
            TenantPublicAssetType assetType,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            if (assetType is not TenantPublicAssetType.Logo
                and not TenantPublicAssetType.Cover
                and not TenantPublicAssetType.Location)
            {
                throw new PublicImageUploadException("Tipo de imagen invalido.");
            }

            var asset = await FindActiveSingletonAsync(assetType, null, cancellationToken);
            if (asset is not null)
            {
                await SoftDeleteAndRemoveStorageAsync(asset, userId, cancellationToken);
            }
        }

        public async Task RemoveServiceMainImageAsync(
            int serviceId,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            await EnsureServiceBelongsToCurrentTenantAsync(serviceId, cancellationToken);
            var asset = await FindActiveSingletonAsync(
                TenantPublicAssetType.ServiceMain,
                serviceId,
                cancellationToken);

            if (asset is not null)
            {
                await SoftDeleteAndRemoveStorageAsync(asset, userId, cancellationToken);
            }
        }

        public async Task RemoveAssetAsync(
            Guid assetId,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            if (assetId == Guid.Empty)
            {
                throw new PublicImageUploadException("Imagen invalida.");
            }

            var asset = await _context.TenantPublicAssets
                .FirstOrDefaultAsync(
                    item => item.Id == assetId &&
                            item.IsActive &&
                            item.DeletedAtUtc == null,
                    cancellationToken);

            if (asset is null)
            {
                return;
            }

            await SoftDeleteAndRemoveStorageAsync(asset, userId, cancellationToken);
        }

        private async Task<TenantPublicAsset> SaveProcessedAssetAsync(
            Guid tenantId,
            Guid? tenantPublicPageId,
            int? serviceId,
            TenantPublicAssetType assetType,
            ProcessedPublicImage processed,
            TenantPublicAsset? replacingAsset,
            string? userId,
            CancellationToken cancellationToken)
        {
            var storageKey = PublicImageStorageKeyBuilder.Build(tenantId, assetType, serviceId);
            var uploaded = false;

            await _quotaService.EnsureCanUploadAsync(
                tenantId,
                processed.SizeBytes,
                replacingAsset?.Id,
                cancellationToken);

            try
            {
                await _storage.UploadAsync(
                    storageKey,
                    processed.Content,
                    _options.OutputContentType,
                    cancellationToken);
                uploaded = true;

                var now = DateTime.UtcNow;
                if (replacingAsset is not null)
                {
                    replacingAsset.IsActive = false;
                    replacingAsset.DeletedAtUtc = now;
                    replacingAsset.UpdatedAtUtc = now;
                }

                var asset = new TenantPublicAsset
                {
                    TenantPublicPageId = tenantPublicPageId,
                    ServicioId = serviceId,
                    AssetType = assetType,
                    StorageKey = storageKey,
                    PublicUrl = _storage.BuildPublicUrl(storageKey),
                    ContentType = _options.OutputContentType,
                    SizeBytes = processed.SizeBytes,
                    Width = processed.Width,
                    Height = processed.Height,
                    OriginalFileName = processed.SafeOriginalFileName,
                    SortOrder = await ResolveNextSortOrderAsync(
                        assetType,
                        serviceId,
                        replacingAsset,
                        cancellationToken),
                    IsActive = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };

                _context.TenantPublicAssets.Add(asset);
                await _context.SaveChangesAsync(cancellationToken);

                if (replacingAsset is not null)
                {
                    await TryDeleteStorageAsync(replacingAsset.StorageKey, cancellationToken);
                }

                return asset;
            }
            catch
            {
                if (uploaded)
                {
                    await TryDeleteStorageAsync(storageKey, cancellationToken);
                }

                throw;
            }
        }

        private async Task SoftDeleteAndRemoveStorageAsync(
            TenantPublicAsset asset,
            string? userId,
            CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            asset.IsActive = false;
            asset.DeletedAtUtc = now;
            asset.UpdatedAtUtc = now;

            await _context.SaveChangesAsync(cancellationToken);
            await TryDeleteStorageAsync(asset.StorageKey, cancellationToken);
        }

        private async Task<ProcessedPublicImage> ProcessImageAsync(
            IFormFile? file,
            TenantPublicAssetType assetType,
            PublicImageCropRequest? crop,
            CancellationToken cancellationToken)
        {
            var profile = _profileProvider.Get(assetType);
            ValidateFileBasics(file, profile);

            await using var raw = new MemoryStream();
            await using (var uploadStream = file!.OpenReadStream())
            {
                await uploadStream.CopyToAsync(raw, cancellationToken);
            }

            if (raw.Length == 0)
            {
                throw new PublicImageUploadException("Selecciona una imagen valida.");
            }

            var format = DetectFormat(raw);
            if (format == UploadedImageFormat.Unknown)
            {
                throw new PublicImageUploadException(UnsupportedFormatMessage);
            }

            raw.Position = 0;
            await _securityScanner.ScanAsync(
                raw,
                file.FileName,
                file.ContentType,
                cancellationToken);

            // 1) Se inspecciona sin decodificar: dimensiones y orientacion EXIF salen del header,
            //    asi los limites de seguridad se aplican ANTES de reservar memoria.
            raw.Position = 0;
            ImageInfo info;
            try
            {
                info = await Image.IdentifyAsync(raw, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "No se pudo leer el encabezado de una imagen publica.");
                throw new PublicImageUploadException(UnreadableImageMessage);
            }

            EnsureWithinDecodeLimits(info, format);

            // Espacio de coordenadas del cliente: el navegador ya aplica la orientacion EXIF al
            // mostrar la foto, por lo que el recorte llega en dimensiones YA rotadas.
            var (sourceWidth, _) = ResolveOrientedSize(info);

            // 2) Se decodifica directo al tamano necesario. En JPEG (el formato de casi toda foto
            //    de celular) ImageSharp escala durante la decodificacion: una foto de 48 MP no
            //    reserva 48 MP de memoria.
            raw.Position = 0;
            Image<Rgba32> image;
            try
            {
                image = await Image.LoadAsync<Rgba32>(
                    new DecoderOptions { TargetSize = ResolveDecodeTargetSize(info, profile) },
                    raw,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "No se pudo decodificar una imagen publica.");
                throw new PublicImageUploadException(UnreadableImageMessage);
            }

            using (image)
            {
                // 3) Normalizacion de orientacion ANTES de borrar el EXIF: si se limpia primero,
                //    las fotos verticales de celular quedan giradas.
                image.Mutate(context => context.AutoOrient());
                image.Metadata.ExifProfile = null;
                image.Metadata.IptcProfile = null;
                image.Metadata.XmpProfile = null;

                var decodeScale = sourceWidth > 0 ? (double)image.Width / sourceWidth : 1d;
                var scaledCrop = ScaleCrop(crop, decodeScale);
                var fitMode = ResolveFitMode(profile, crop);
                var targetAspect = ResolveTargetAspect(profile, crop);

                MemoryStream output;
                int outputWidth;
                int outputHeight;

                switch (fitMode)
                {
                    case PublicImageFitMode.Original:
                        ResizeToMax(image, profile.OutputMaxWidth, profile.OutputMaxHeight);
                        FlattenIfOpaqueProfile(image, profile);
                        (output, outputWidth, outputHeight) = await EncodeWebpAsync(image, profile, cancellationToken);
                        break;

                    case PublicImageFitMode.Contain:
                        // Fondo neutro/transparente (util para logos: no se recorta ni se difumina).
                        (output, outputWidth, outputHeight) = await ComposePaddedWebpAsync(
                            image, targetAspect, profile, blurBackground: false, cancellationToken);
                        break;

                    case PublicImageFitMode.Padded:
                        // Fondo blur de la misma foto (portada/fotos verticales).
                        (output, outputWidth, outputHeight) = await ComposePaddedWebpAsync(
                            image, targetAspect, profile, blurBackground: true, cancellationToken);
                        break;

                    default: // Cover (comportamiento historico: recorte al aspecto objetivo)
                        var cropRectangle = ResolveCropRectangle(image.Width, image.Height, scaledCrop, targetAspect);
                        image.Mutate(context => context.Crop(cropRectangle));
                        ResizeToMax(image, profile.OutputMaxWidth, profile.OutputMaxHeight);
                        FlattenIfOpaqueProfile(image, profile);
                        (output, outputWidth, outputHeight) = await EncodeWebpAsync(image, profile, cancellationToken);
                        break;
                }

                return new ProcessedPublicImage(
                    output,
                    output.Length,
                    outputWidth,
                    outputHeight,
                    SanitizeOriginalFileName(file.FileName));
            }
        }

        /// <summary>
        /// Limite de seguridad sobre la resolucion declarada en el header. No es un limite de
        /// producto: una foto de celular normal pasa siempre y se optimiza; lo que se rechaza son
        /// archivos que no caben en memoria o "bombas" de descompresion.
        /// </summary>
        private void EnsureWithinDecodeLimits(ImageInfo info, UploadedImageFormat format)
        {
            var pixels = (long)info.Width * info.Height;
            if (pixels <= 0)
            {
                throw new PublicImageUploadException(UnreadableImageMessage);
            }

            var maxPixels = format == UploadedImageFormat.Jpeg
                ? _options.MaxDecodedPixels
                : Math.Min(_options.MaxNonJpegDecodedPixels, _options.MaxDecodedPixels);

            if (pixels > maxPixels)
            {
                throw new PublicImageUploadException(
                    $"La imagen tiene {FormatMegapixels(pixels)} de resolucion y el maximo que podemos procesar " +
                    $"es {FormatMegapixels(maxPixels)}. Toma la foto en menor resolucion o guardala como JPG.");
            }
        }

        /// <summary>
        /// Caja de decodificacion: el doble del lado mayor que necesita el perfil, para que el
        /// recorte del cliente conserve nitidez. Null = la imagen ya es chica (nunca se amplia).
        /// </summary>
        private Size? ResolveDecodeTargetSize(ImageInfo info, PublicImageProfile profile)
        {
            var oversample = Math.Clamp(_options.DecodeOversampleFactor, 1, 4);
            var target = Math.Max(profile.OutputMaxWidth, profile.OutputMaxHeight) * oversample;
            return Math.Max(info.Width, info.Height) <= target
                ? null
                : new Size(target, target);
        }

        /// <summary>Dimensiones tal como las ve el navegador (con la orientacion EXIF aplicada).</summary>
        private static (int Width, int Height) ResolveOrientedSize(ImageInfo info)
        {
            ushort orientation = 1;
            if (info.Metadata.ExifProfile is not null &&
                info.Metadata.ExifProfile.TryGetValue(ExifTag.Orientation, out var value))
            {
                orientation = value.Value;
            }

            // 5-8 implican rotacion de 90 grados: ancho y alto se intercambian.
            return orientation is >= 5 and <= 8
                ? (info.Height, info.Width)
                : (info.Width, info.Height);
        }

        /// <summary>
        /// Lleva el recorte del cliente (en pixeles de la foto original) al espacio de la imagen
        /// realmente decodificada, que puede venir reducida.
        /// </summary>
        private static PublicImageCropRequest? ScaleCrop(PublicImageCropRequest? crop, double scale)
        {
            if (crop is null || !crop.HasCrop || scale <= 0 || Math.Abs(scale - 1d) < 0.0001)
            {
                return crop;
            }

            return new PublicImageCropRequest
            {
                CropX = (int)Math.Round(crop.CropX!.Value * scale),
                CropY = (int)Math.Round(crop.CropY!.Value * scale),
                CropWidth = Math.Max(1, (int)Math.Round(crop.CropWidth!.Value * scale)),
                CropHeight = Math.Max(1, (int)Math.Round(crop.CropHeight!.Value * scale)),
                TargetAspectRatio = crop.TargetAspectRatio,
                FitMode = crop.FitMode
            };
        }

        /// <summary>
        /// Modo de encuadre efectivo. El perfil manda: un modo que ese uso no ofrece cae al modo
        /// por defecto, para que un cliente viejo o manipulado no rompa el marco de la landing.
        /// </summary>
        private static PublicImageFitMode ResolveFitMode(PublicImageProfile profile, PublicImageCropRequest? crop)
        {
            // Sin FitMode explicito pero con rectangulo de recorte solo tiene sentido Cover.
            var fallback = crop?.HasCrop == true && profile.AllowsFitMode(PublicImageFitMode.Cover)
                ? PublicImageFitMode.Cover
                : profile.DefaultFitMode;

            var requested = crop?.ResolveFitMode(fallback) ?? fallback;
            return profile.AllowsFitMode(requested) ? requested : profile.DefaultFitMode;
        }

        /// <summary>
        /// Solo el logo necesita canal alfa. En el resto de usos la transparencia se rellena con
        /// blanco: la landing tiene fondo claro y el archivo queda mas liviano.
        /// </summary>
        private static void FlattenIfOpaqueProfile(Image<Rgba32> image, PublicImageProfile profile)
        {
            if (profile.PreserveTransparency)
            {
                return;
            }

            image.Mutate(context => context.BackgroundColor(Color.White));
        }

        private static void ResizeToMax(Image image, int maxWidth, int maxHeight)
        {
            if (image.Width > maxWidth || image.Height > maxHeight)
            {
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(maxWidth, maxHeight),
                    Mode = ResizeMode.Max
                }));
            }
        }

        private static async Task<(MemoryStream Output, int Width, int Height)> EncodeWebpAsync(
            Image image,
            PublicImageProfile profile,
            CancellationToken cancellationToken)
        {
            var output = new MemoryStream();
            await image.SaveAsWebpAsync(
                output,
                new WebpEncoder { Quality = profile.Quality },
                cancellationToken);
            output.Position = 0;
            return (output, image.Width, image.Height);
        }

        /// <summary>
        /// Compone la imagen COMPLETA (sin recortar) centrada sobre un canvas del aspecto objetivo,
        /// rellenando los margenes con una copia ampliada y desenfocada de la misma imagen (blur).
        /// </summary>
        private static async Task<(MemoryStream Output, int Width, int Height)> ComposePaddedWebpAsync(
            Image<Rgba32> image,
            double targetAspect,
            PublicImageProfile profile,
            bool blurBackground,
            CancellationToken cancellationToken)
        {
            // La caja se recorta al tamano que la foto puede llenar sin ampliarse: una imagen
            // chica no se convierte en un canvas gigante y borroso.
            var boxWidth = Math.Min(
                profile.OutputMaxWidth,
                (int)Math.Ceiling(Math.Max(image.Width, image.Height * targetAspect)));
            var boxHeight = Math.Min(
                profile.OutputMaxHeight,
                (int)Math.Ceiling(Math.Max(image.Height, image.Width / targetAspect)));

            var (canvasWidth, canvasHeight) = ResolveCanvasSize(
                targetAspect,
                Math.Max(1, boxWidth),
                Math.Max(1, boxHeight));

            // Primer plano: imagen completa contenida dentro del canvas (sin recorte).
            using var foreground = image.Clone(context => context
                .Resize(new ResizeOptions
                {
                    Size = new Size(canvasWidth, canvasHeight),
                    Mode = ResizeMode.Max
                }));

            var offsetX = Math.Max(0, (canvasWidth - foreground.Width) / 2);
            var offsetY = Math.Max(0, (canvasHeight - foreground.Height) / 2);

            // Canvas transparente cuando el perfil conserva alfa (logo); blanco cuando no, para
            // que un PNG con transparencia no deje huecos sobre el fondo claro de la landing.
            using var canvas = profile.PreserveTransparency
                ? new Image<Rgba32>(canvasWidth, canvasHeight)
                : new Image<Rgba32>(canvasWidth, canvasHeight, Color.White.ToPixel<Rgba32>());

            if (blurBackground)
            {
                // Fondo blur: copia que cubre todo el canvas (recorta) + desenfoque + leve oscurecido.
                using var background = image.Clone(context => context
                    .Resize(new ResizeOptions
                    {
                        Size = new Size(canvasWidth, canvasHeight),
                        Mode = ResizeMode.Crop,
                        Position = AnchorPositionMode.Center
                    })
                    .GaussianBlur(Math.Max(8f, canvasWidth / 40f))
                    .Brightness(0.9f));

                canvas.Mutate(context => context
                    .DrawImage(background, new Point(0, 0), 1f)
                    .DrawImage(foreground, new Point(offsetX, offsetY), 1f));
            }
            else
            {
                canvas.Mutate(context => context
                    .DrawImage(foreground, new Point(offsetX, offsetY), 1f));
            }

            return await EncodeWebpAsync(canvas, profile, cancellationToken);
        }

        /// <summary>Canvas del aspecto objetivo, maximizado dentro de la caja (maxWidth x maxHeight).</summary>
        private static (int Width, int Height) ResolveCanvasSize(double targetAspect, int maxWidth, int maxHeight)
        {
            var boxAspect = (double)maxWidth / maxHeight;
            if (targetAspect >= boxAspect)
            {
                var height = Math.Max(1, (int)Math.Round(maxWidth / targetAspect));
                return (maxWidth, Math.Min(height, maxHeight));
            }

            var width = Math.Max(1, (int)Math.Round(maxHeight * targetAspect));
            return (Math.Min(width, maxWidth), maxHeight);
        }

        /// <summary>
        /// Aspecto objetivo. Solo se acepta uno de los que el perfil realmente ofrece: asi el
        /// marco de la landing es siempre el esperado, aunque el cliente mande otra cosa.
        /// </summary>
        private static double ResolveTargetAspect(PublicImageProfile profile, PublicImageCropRequest? crop)
        {
            if (crop?.TargetAspectRatio is not double requested)
            {
                return profile.RecommendedAspectRatio;
            }

            if (double.IsNaN(requested) || double.IsInfinity(requested) || requested <= 0)
            {
                throw new PublicImageUploadException("El formato de imagen solicitado no es valido.");
            }

            var allowed = profile.CropPresets
                .Where(preset => preset.AspectRatio.HasValue)
                .Any(preset => Math.Abs(preset.AspectRatio!.Value - requested) <= 0.01);

            if (!allowed)
            {
                throw new PublicImageUploadException(
                    "El formato de imagen solicitado no esta disponible para este tipo de imagen.");
            }

            return requested;
        }

        private static Rectangle ResolveCropRectangle(
            int imageWidth,
            int imageHeight,
            PublicImageCropRequest? crop,
            double targetAspect)
        {
            return crop is not null && TryBuildClientCrop(imageWidth, imageHeight, crop, out var rectangle)
                ? rectangle
                : BuildCenteredCrop(imageWidth, imageHeight, targetAspect);
        }

        /// <summary>
        /// Valida el recorte recibido del cliente contra la imagen decodificada. Se tolera un
        /// desborde minimo (redondeo al escalar el recorte); cualquier cosa fuera de rango cae al
        /// recorte centrado en vez de producir una imagen degenerada.
        /// </summary>
        private static bool TryBuildClientCrop(
            int imageWidth,
            int imageHeight,
            PublicImageCropRequest crop,
            out Rectangle rectangle)
        {
            const int roundingTolerance = 4;
            rectangle = Rectangle.Empty;

            if (!crop.HasCrop ||
                crop.CropX!.Value < 0 ||
                crop.CropY!.Value < 0 ||
                crop.CropWidth!.Value <= 0 ||
                crop.CropHeight!.Value <= 0 ||
                crop.CropX.Value >= imageWidth ||
                crop.CropY.Value >= imageHeight)
            {
                return false;
            }

            var right = (long)crop.CropX.Value + crop.CropWidth.Value;
            var bottom = (long)crop.CropY.Value + crop.CropHeight.Value;
            if (right > imageWidth + roundingTolerance || bottom > imageHeight + roundingTolerance)
            {
                return false;
            }

            rectangle = new Rectangle(
                crop.CropX.Value,
                crop.CropY.Value,
                Math.Min(crop.CropWidth.Value, imageWidth - crop.CropX.Value),
                Math.Min(crop.CropHeight.Value, imageHeight - crop.CropY.Value));
            return true;
        }

        private static Rectangle BuildCenteredCrop(
            int imageWidth,
            int imageHeight,
            double targetAspectRatio)
        {
            if (imageWidth <= 0 || imageHeight <= 0)
            {
                return Rectangle.Empty;
            }

            var currentAspectRatio = (double)imageWidth / imageHeight;
            var cropWidth = imageWidth;
            var cropHeight = imageHeight;

            if (currentAspectRatio > targetAspectRatio)
            {
                cropWidth = Math.Max(1, (int)Math.Round(imageHeight * targetAspectRatio));
            }
            else if (currentAspectRatio < targetAspectRatio)
            {
                cropHeight = Math.Max(1, (int)Math.Round(imageWidth / targetAspectRatio));
            }

            var cropX = Math.Max(0, (imageWidth - cropWidth) / 2);
            var cropY = Math.Max(0, (imageHeight - cropHeight) / 2);
            return new Rectangle(cropX, cropY, cropWidth, cropHeight);
        }

        private void ValidateFileBasics(IFormFile? file, PublicImageProfile profile)
        {
            if (file is null || file.Length <= 0)
            {
                throw new PublicImageUploadException("Selecciona una imagen valida.");
            }

            if (file.Length > profile.MaxUploadBytes)
            {
                throw new PublicImageUploadException(
                    $"La imagen pesa {FormatBytes(file.Length)} y el maximo permitido es " +
                    $"{FormatBytes(profile.MaxUploadBytes)}. Volve a tomarla en menor calidad o usa otra foto.");
            }

            var safeName = Path.GetFileName(file.FileName ?? string.Empty);
            var extension = Path.GetExtension(safeName);
            var lowerName = safeName.ToLowerInvariant();

            if (IsHeicLike(file.ContentType, extension))
            {
                throw new PublicImageUploadException(HeicMessage);
            }

            if (!_options.AllowedContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            {
                throw new PublicImageUploadException(UnsupportedFormatMessage);
            }

            if (string.IsNullOrWhiteSpace(extension) ||
                !AllowedExtensions.Contains(extension) ||
                DangerousExtensions.Any(lowerName.Contains))
            {
                throw new PublicImageUploadException("Extension no permitida. Usa JPG, PNG o WEBP.");
            }
        }

        private static bool IsHeicLike(string? contentType, string? extension) =>
            (!string.IsNullOrWhiteSpace(contentType) &&
             (contentType.Contains("heic", StringComparison.OrdinalIgnoreCase) ||
              contentType.Contains("heif", StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrWhiteSpace(extension) &&
             (extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
              extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)));

        private async Task<TenantPublicPage> GetOrCreatePageAsync(CancellationToken cancellationToken)
        {
            var page = await _context.TenantPublicPages
                .FirstOrDefaultAsync(cancellationToken);

            if (page is not null)
            {
                return page;
            }

            page = new TenantPublicPage
            {
                ShowServices = true,
                ShowPrices = true,
                ShowLocation = true,
                ShowWhatsAppButton = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _context.TenantPublicPages.Add(page);
            return page;
        }

        private async Task<TenantPublicAsset?> FindActiveSingletonAsync(
            TenantPublicAssetType assetType,
            int? serviceId,
            CancellationToken cancellationToken)
        {
            return await _context.TenantPublicAssets
                .FirstOrDefaultAsync(
                    asset => asset.AssetType == assetType &&
                             asset.ServicioId == serviceId &&
                             asset.IsActive &&
                             asset.DeletedAtUtc == null,
                    cancellationToken);
        }

        private async Task EnsureGalleryLimitAsync(
            Guid tenantId,
            TenantPublicAssetType assetType,
            int? serviceId,
            int maxImages,
            CancellationToken cancellationToken)
        {
            var count = await _context.TenantPublicAssets
                .AsNoTracking()
                .CountAsync(
                    asset => asset.TenantId == tenantId &&
                             asset.AssetType == assetType &&
                             asset.ServicioId == serviceId &&
                             asset.IsActive &&
                             asset.DeletedAtUtc == null,
                    cancellationToken);

            if (count >= maxImages)
            {
                throw new PublicImageUploadException(
                    $"Ya alcanzaste el maximo de {maxImages} imagenes en esta galeria.");
            }
        }

        private async Task<int> ResolveNextSortOrderAsync(
            TenantPublicAssetType assetType,
            int? serviceId,
            TenantPublicAsset? replacingAsset,
            CancellationToken cancellationToken)
        {
            if (replacingAsset is not null)
            {
                return replacingAsset.SortOrder;
            }

            var max = await _context.TenantPublicAssets
                .AsNoTracking()
                .Where(asset => asset.AssetType == assetType &&
                                asset.ServicioId == serviceId)
                .MaxAsync(asset => (int?)asset.SortOrder, cancellationToken);

            return (max ?? 0) + 1;
        }

        private async Task EnsureServiceBelongsToCurrentTenantAsync(
            int serviceId,
            CancellationToken cancellationToken)
        {
            if (serviceId <= 0 ||
                !await _context.Servicios
                    .AsNoTracking()
                    .AnyAsync(service => service.Id == serviceId && service.Activo, cancellationToken))
            {
                throw new PublicImageUploadException("El servicio seleccionado no existe o no pertenece al negocio actual.");
            }
        }

        private Guid ResolveTenantId()
        {
            if (!_tenantProvider.HasTenant())
            {
                throw new PublicImageUploadException("No se pudo determinar el negocio actual.");
            }

            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty)
            {
                throw new PublicImageUploadException("No se pudo determinar el negocio actual.");
            }

            return tenantId;
        }

        private static void EnsurePublicPageAssetType(TenantPublicAssetType assetType)
        {
            if (assetType is not TenantPublicAssetType.Logo
                and not TenantPublicAssetType.Cover
                and not TenantPublicAssetType.BusinessGallery
                and not TenantPublicAssetType.Location)
            {
                throw new PublicImageUploadException("Tipo de imagen de pagina publica invalido.");
            }
        }

        private static bool IsSingleton(TenantPublicAssetType assetType) =>
            assetType is TenantPublicAssetType.Logo
                or TenantPublicAssetType.Cover
                or TenantPublicAssetType.Location
                or TenantPublicAssetType.ServiceMain;

        /// <summary>
        /// Formato real segun los bytes del archivo (no la extension ni el MIME del cliente).
        /// Ademas de seguridad, define el limite de pixeles aplicable: solo JPEG decodifica escalado.
        /// </summary>
        private static UploadedImageFormat DetectFormat(Stream stream)
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[12];
            var read = stream.Read(header);
            stream.Position = 0;

            if (read >= 3 &&
                header[0] == 0xFF &&
                header[1] == 0xD8 &&
                header[2] == 0xFF)
            {
                return UploadedImageFormat.Jpeg;
            }

            if (read >= 8 &&
                header[0] == 0x89 &&
                header[1] == 0x50 &&
                header[2] == 0x4E &&
                header[3] == 0x47 &&
                header[4] == 0x0D &&
                header[5] == 0x0A &&
                header[6] == 0x1A &&
                header[7] == 0x0A)
            {
                return UploadedImageFormat.Png;
            }

            if (read >= 12 &&
                header[0] == 0x52 &&
                header[1] == 0x49 &&
                header[2] == 0x46 &&
                header[3] == 0x46 &&
                header[8] == 0x57 &&
                header[9] == 0x45 &&
                header[10] == 0x42 &&
                header[11] == 0x50)
            {
                return UploadedImageFormat.Webp;
            }

            return UploadedImageFormat.Unknown;
        }

        private static string? SanitizeOriginalFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var safe = Path.GetFileName(fileName)
                .Replace('<', '_')
                .Replace('>', '_')
                .Replace('"', '_')
                .Replace('\'', '_')
                .Replace('`', '_')
                .Trim();

            return safe.Length == 0 ? null : safe[..Math.Min(180, safe.Length)];
        }

        private async Task TryDeleteStorageAsync(
            string storageKey,
            CancellationToken cancellationToken)
        {
            try
            {
                await _storage.TryDeleteAsync(storageKey, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo borrar el asset publico {StorageKey}.", storageKey);
            }
        }

        private static string FormatBytes(long bytes)
        {
            var mb = bytes / 1024m / 1024m;
            return $"{mb:0.#} MB";
        }

        private static string FormatMegapixels(long pixels) =>
            $"{pixels / 1_000_000m:0.#} MP";

        private enum UploadedImageFormat
        {
            Unknown = 0,
            Jpeg = 1,
            Png = 2,
            Webp = 3
        }

        private sealed record ProcessedPublicImage(
            MemoryStream Content,
            long SizeBytes,
            int Width,
            int Height,
            string? SafeOriginalFileName);
    }
}
