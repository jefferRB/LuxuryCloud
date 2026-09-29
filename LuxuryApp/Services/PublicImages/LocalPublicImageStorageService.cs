using Microsoft.Extensions.Options;

namespace LuxuryApp.Services.PublicImages
{
    public sealed class LocalPublicImageStorageService : IPublicImageStorageService
    {
        private readonly IWebHostEnvironment _environment;
        private readonly PublicImageOptions _options;
        private readonly ILogger<LocalPublicImageStorageService> _logger;

        public LocalPublicImageStorageService(
            IWebHostEnvironment environment,
            IOptions<PublicImageOptions> options,
            ILogger<LocalPublicImageStorageService> logger)
        {
            _environment = environment;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<PublicImageStoredObject> UploadAsync(
            string storageKey,
            Stream content,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            if (!IsValidStorageKey(storageKey))
            {
                throw new InvalidOperationException("La ruta de almacenamiento de imagen no es valida.");
            }

            if (!TryResolveLocalPath(storageKey, out var absolutePath))
            {
                throw new InvalidOperationException("No se pudo resolver la ruta local de imagen.");
            }

            var directory = Path.GetDirectoryName(absolutePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("No se pudo resolver el directorio local de imagen.");
            }

            Directory.CreateDirectory(directory);

            await using var destination = new FileStream(
                absolutePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);

            if (content.CanSeek)
            {
                content.Position = 0;
            }

            await content.CopyToAsync(destination, cancellationToken);

            return new PublicImageStoredObject(
                storageKey,
                BuildPublicUrl(storageKey),
                contentType,
                destination.Length);
        }

        public Task<bool> TryDeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            if (!TryResolveLocalPath(storageKey, out var absolutePath))
            {
                return Task.FromResult(false);
            }

            try
            {
                if (File.Exists(absolutePath))
                {
                    File.Delete(absolutePath);
                    return Task.FromResult(true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo borrar la imagen publica local {StorageKey}.", storageKey);
            }

            return Task.FromResult(false);
        }

        public string BuildPublicUrl(string storageKey)
        {
            if (!IsValidStorageKey(storageKey))
            {
                throw new InvalidOperationException("La ruta de almacenamiento de imagen no es valida.");
            }

            return BuildUrl(
                string.IsNullOrWhiteSpace(_options.CdnBaseUrl)
                    ? "/public-media"
                    : _options.CdnBaseUrl!,
                storageKey);
        }

        public bool IsValidStorageKey(string storageKey) =>
            PublicImageStorageKeyBuilder.IsValidStorageKey(storageKey);

        public bool TryResolveLocalPath(string storageKey, out string absolutePath)
        {
            absolutePath = string.Empty;
            if (!IsValidStorageKey(storageKey))
            {
                return false;
            }

            var root = Path.GetFullPath(Path.Combine(
                _environment.ContentRootPath,
                "App_Data",
                "public-images"));

            var candidate = Path.GetFullPath(Path.Combine(
                root,
                storageKey.Replace('/', Path.DirectorySeparatorChar)));

            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Ruta local de imagen fuera del directorio permitido: {StorageKey}.", storageKey);
                return false;
            }

            absolutePath = candidate;
            return true;
        }

        private static string BuildUrl(string baseUrl, string storageKey)
        {
            var trimmedBase = baseUrl.TrimEnd('/');
            return $"{trimmedBase}/{storageKey}";
        }
    }
}
