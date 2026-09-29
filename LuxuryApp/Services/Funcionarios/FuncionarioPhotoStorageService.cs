using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace LuxuryApp.Services.Funcionarios
{
    public sealed class FuncionarioPhotoStorageService : IFuncionarioPhotoStorageService
    {
        // 5 MB. Suficiente para una foto de perfil; evita abuso de almacenamiento.
        private const long MaxBytes = 5 * 1024 * 1024;

        private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/jpg", "image/png", "image/webp"
        };

        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp"
        };

        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<FuncionarioPhotoStorageService> _logger;

        public FuncionarioPhotoStorageService(
            IWebHostEnvironment environment,
            ILogger<FuncionarioPhotoStorageService> logger)
        {
            _environment = environment;
            _logger = logger;
        }

        public async Task<FuncionarioPhotoSaveResult> SaveAsync(
            Guid tenantId,
            IFormFile file,
            string? previousStoragePath,
            CancellationToken cancellationToken = default)
        {
            if (tenantId == Guid.Empty)
            {
                return FuncionarioPhotoSaveResult.Fail("No se pudo determinar el negocio para guardar la foto.");
            }

            if (file is null || file.Length == 0)
            {
                return FuncionarioPhotoSaveResult.Fail("Selecciona una imagen válida.");
            }

            if (file.Length > MaxBytes)
            {
                return FuncionarioPhotoSaveResult.Fail("La imagen supera el tamaño máximo permitido (5 MB).");
            }

            if (!AllowedContentTypes.Contains(file.ContentType))
            {
                return FuncionarioPhotoSaveResult.Fail("Formato no permitido. Usa JPG, PNG o WEBP.");
            }

            var extension = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
            {
                return FuncionarioPhotoSaveResult.Fail("Extensión no permitida. Usa JPG, PNG o WEBP.");
            }

            // Lee la cabecera para validar la firma REAL del archivo (no confiar en extensión/mime).
            await using var uploadStream = file.OpenReadStream();
            var header = new byte[12];
            var read = await ReadExactlyAsync(uploadStream, header, cancellationToken);

            var detectedExtension = DetectImageExtension(header, read);
            if (detectedExtension is null)
            {
                return FuncionarioPhotoSaveResult.Fail("El archivo no es una imagen JPG, PNG o WEBP válida.");
            }

            // Carpeta por tenant: evita mezcla y facilita limpieza. Nombre aleatorio: sin path traversal.
            var webRoot = _environment.WebRootPath;
            if (string.IsNullOrWhiteSpace(webRoot))
            {
                webRoot = Path.Combine(_environment.ContentRootPath, "wwwroot");
            }

            var relativeDir = Path.Combine("uploads", "tenants", tenantId.ToString("N"), "funcionarios");
            var absoluteDir = Path.Combine(webRoot, relativeDir);
            Directory.CreateDirectory(absoluteDir);

            var fileName = $"{Guid.NewGuid():N}{detectedExtension}";
            var absolutePath = Path.Combine(absoluteDir, fileName);
            var relativeStoragePath = Path.Combine(relativeDir, fileName).Replace('\\', '/');
            var publicUrl = "/" + relativeStoragePath;

            try
            {
                // Reposiciona el stream al inicio (ya leímos la cabecera) y vuelca a disco.
                if (uploadStream.CanSeek)
                {
                    uploadStream.Position = 0;
                    await using var destination = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await uploadStream.CopyToAsync(destination, cancellationToken);
                }
                else
                {
                    // Fallback: reabrir el IFormFile desde cero.
                    await using var freshStream = file.OpenReadStream();
                    await using var destination = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await freshStream.CopyToAsync(destination, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo guardar la foto del funcionario para el tenant {TenantId}.", tenantId);
                return FuncionarioPhotoSaveResult.Fail("No se pudo guardar la imagen. Intentá de nuevo.");
            }

            // Éxito: elimina la foto anterior (si la había) para no dejar huérfanos.
            Delete(previousStoragePath);

            return FuncionarioPhotoSaveResult.Ok(publicUrl, relativeStoragePath);
        }

        public void Delete(string? storagePath)
        {
            if (string.IsNullOrWhiteSpace(storagePath))
            {
                return;
            }

            try
            {
                var webRoot = _environment.WebRootPath;
                if (string.IsNullOrWhiteSpace(webRoot))
                {
                    webRoot = Path.Combine(_environment.ContentRootPath, "wwwroot");
                }

                // Normaliza y valida que la ruta quede DENTRO de wwwroot (defensa anti path traversal).
                var normalized = storagePath.Replace('\\', '/').TrimStart('/');
                var absolute = Path.GetFullPath(Path.Combine(webRoot, normalized));
                var webRootFull = Path.GetFullPath(webRoot);

                if (!absolute.StartsWith(webRootFull, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Ruta de foto fuera de wwwroot ignorada: {StoragePath}.", storagePath);
                    return;
                }

                if (File.Exists(absolute))
                {
                    File.Delete(absolute);
                }
            }
            catch (Exception ex)
            {
                // No debe romper el flujo si el archivo ya no existe o el disco falla.
                _logger.LogWarning(ex, "No se pudo borrar la foto física {StoragePath}.", storagePath);
            }
        }

        private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        /// <summary>Devuelve la extensión canónica si la cabecera corresponde a JPG/PNG/WEBP; si no, null.</summary>
        private static string? DetectImageExtension(byte[] header, int length)
        {
            // JPEG: FF D8 FF
            if (length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            {
                return ".jpg";
            }

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (length >= 8 &&
                header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            {
                return ".png";
            }

            // WEBP: "RIFF" .... "WEBP"
            if (length >= 12 &&
                header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
            {
                return ".webp";
            }

            return null;
        }
    }
}
