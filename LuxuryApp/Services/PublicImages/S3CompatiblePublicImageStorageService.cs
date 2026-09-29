using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace LuxuryApp.Services.PublicImages
{
    /// <summary>
    /// Provider de storage publico S3-compatible para Cloudflare R2 usando el cliente oficial
    /// Minio .NET. Se usa Minio en lugar del SDK de AWS (v4) porque ese SDK emite el trailer
    /// STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER (aws-chunked) que R2 no implementa. Minio hace
    /// un PUT simple con Content-Length, compatible con R2.
    /// </summary>
    public sealed class S3CompatiblePublicImageStorageService : IPublicImageStorageService
    {
        private const string CacheControlHeader = "public, max-age=31536000, immutable";

        /// <summary>
        /// Marcador unico para confirmar en logs (journalctl) que el binario desplegado sube via
        /// Minio (no el SDK de AWS).
        /// </summary>
        internal const string CompatModeMarker = "R2_MINIO_UPLOAD_ACTIVE";

        private static readonly string SdkVersion =
            typeof(MinioClient).Assembly.GetName().Version?.ToString() ?? "unknown";

        private readonly S3StorageOptions _options;
        private readonly string _publicBaseUrl;
        private readonly ILogger<S3CompatiblePublicImageStorageService> _logger;

        public S3CompatiblePublicImageStorageService(
            IOptions<S3StorageOptions> options,
            ILogger<S3CompatiblePublicImageStorageService> logger)
        {
            _options = options.Value;
            _publicBaseUrl = (_options.PublicBaseUrl ?? string.Empty).TrimEnd('/');
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

            EnsureConfigured();

            // R2 rechaza el streaming trailer (aws-chunked). Garantizamos un stream seekable con
            // longitud conocida para subir con Content-Length exacto.
            var (uploadStream, createdStream) = await EnsureSeekableAsync(content, cancellationToken);
            try
            {
                uploadStream.Position = 0;
                var length = uploadStream.Length;
                if (length <= 0)
                {
                    throw new InvalidOperationException("El contenido de la imagen esta vacio.");
                }

                LogUploadDiagnostics(storageKey, contentType, length);

                using var client = CreateClient();
                var args = new PutObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(storageKey)
                    .WithStreamData(uploadStream)
                    .WithObjectSize(length)
                    .WithContentType(contentType)
                    .WithHeaders(BuildObjectHeaders());

                try
                {
                    await client.PutObjectAsync(args, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "{Marker} R2 Minio upload FAILED bucket={Bucket} key={StorageKey} message={Message}",
                        CompatModeMarker,
                        _options.BucketName,
                        storageKey,
                        ex.Message);
                    throw;
                }

                return new PublicImageStoredObject(
                    storageKey,
                    BuildPublicUrl(storageKey),
                    contentType,
                    length);
            }
            finally
            {
                if (createdStream)
                {
                    await uploadStream.DisposeAsync();
                }
            }
        }

        public async Task<bool> TryDeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            if (!IsValidStorageKey(storageKey) ||
                string.IsNullOrWhiteSpace(_options.BucketName))
            {
                return false;
            }

            using var client = CreateClient();
            await client.RemoveObjectAsync(
                new RemoveObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(storageKey),
                cancellationToken);

            return true;
        }

        public string BuildPublicUrl(string storageKey)
        {
            if (!IsValidStorageKey(storageKey))
            {
                throw new InvalidOperationException("La ruta de almacenamiento de imagen no es valida.");
            }

            if (string.IsNullOrWhiteSpace(_publicBaseUrl))
            {
                throw new InvalidOperationException("S3Storage:PublicBaseUrl es requerido.");
            }

            return $"{_publicBaseUrl}/{storageKey}";
        }

        public bool IsValidStorageKey(string storageKey) =>
            PublicImageStorageKeyBuilder.IsValidStorageKey(storageKey);

        /// <summary>
        /// Sube un objeto de diagnostico pequeno a R2 (fuera del arbol de imagenes) usando Minio, y
        /// devuelve la URL publica. Pensado para un healthcheck manual de SuperAdmin. No expone
        /// secretos. La clave es controlada internamente (no viene del cliente).
        /// </summary>
        public async Task<string> UploadHealthCheckObjectAsync(CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            var storageKey = $"diagnostics/r2-healthcheck-{DateTime.UtcNow:yyyyMMddHHmmssfff}.txt";
            var payload = Encoding.UTF8.GetBytes(
                $"r2-healthcheck {DateTime.UtcNow:O} {CompatModeMarker}");
            const string contentType = "text/plain; charset=utf-8";

            await using var content = new MemoryStream(payload, writable: false);
            LogUploadDiagnostics(storageKey, contentType, payload.Length);

            using var client = CreateClient();
            var args = new PutObjectArgs()
                .WithBucket(_options.BucketName)
                .WithObject(storageKey)
                .WithStreamData(content)
                .WithObjectSize(payload.Length)
                .WithContentType(contentType)
                .WithHeaders(BuildObjectHeaders());

            try
            {
                await client.PutObjectAsync(args, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "{Marker} R2 Minio healthcheck upload FAILED message={Message}",
                    CompatModeMarker,
                    ex.Message);
                throw;
            }

            // La clave de diagnostico esta fuera del patron de imagenes; construimos la URL directo
            // (BuildPublicUrl valida el patron de imagenes).
            return $"{_publicBaseUrl}/{storageKey}";
        }

        /// <summary>
        /// Normaliza el endpoint configurado (URL con esquema) al host que Minio requiere y detecta
        /// SSL. Ej: "https://ACCOUNT.r2.cloudflarestorage.com" -> ("ACCOUNT.r2.cloudflarestorage.com", true).
        /// </summary>
        internal static (string Host, bool Secure) ResolveEndpoint(string? endpoint)
        {
            if (!string.IsNullOrWhiteSpace(endpoint) &&
                Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                return (uri.Host, uri.Scheme != Uri.UriSchemeHttp);
            }

            // Fallback: sin esquema. Se asume SSL (R2 siempre es https).
            var host = (endpoint ?? string.Empty)
                .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');

            return (host, true);
        }

        /// <summary>
        /// Garantiza un stream seekable posicionado en 0. Si el contenido no es seekable se copia a
        /// un MemoryStream (indicando que el llamador debe liberarlo).
        /// </summary>
        internal static async Task<(Stream Stream, bool Created)> EnsureSeekableAsync(
            Stream content,
            CancellationToken cancellationToken)
        {
            if (content.CanSeek)
            {
                content.Position = 0;
                return (content, false);
            }

            var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return (buffer, true);
        }

        private IMinioClient CreateClient()
        {
            var (host, secure) = ResolveEndpoint(_options.Endpoint);
            var builder = new MinioClient()
                .WithEndpoint(host)
                .WithCredentials(_options.AccessKey, _options.SecretKey)
                .WithSSL(secure);

            if (!string.IsNullOrWhiteSpace(_options.Region))
            {
                builder = builder.WithRegion(_options.Region);
            }

            return builder.Build();
        }

        private static Dictionary<string, string> BuildObjectHeaders() =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Cache-Control"] = CacheControlHeader
            };

        private void EnsureConfigured()
        {
            if (string.IsNullOrWhiteSpace(_options.Endpoint) ||
                string.IsNullOrWhiteSpace(_options.BucketName) ||
                string.IsNullOrWhiteSpace(_options.AccessKey) ||
                string.IsNullOrWhiteSpace(_options.SecretKey))
            {
                throw new InvalidOperationException("El storage S3-compatible no esta configurado.");
            }
        }

        private void LogUploadDiagnostics(string storageKey, string contentType, long length)
        {
            var (host, secure) = ResolveEndpoint(_options.Endpoint);

            // Nunca se registran AccessKey/SecretKey, URLs firmadas ni el contenido del archivo.
            _logger.LogInformation(
                "{Marker} provider=Minio sdk={Sdk} endpointHost={EndpointHost} ssl={Ssl} region={Region} " +
                "bucket={Bucket} key={StorageKey} contentType={ContentType} length={Length} publicBaseUrl={PublicBaseUrl}",
                CompatModeMarker,
                SdkVersion,
                host,
                secure,
                _options.Region,
                _options.BucketName,
                storageKey,
                contentType,
                length,
                _publicBaseUrl);
        }
    }
}
