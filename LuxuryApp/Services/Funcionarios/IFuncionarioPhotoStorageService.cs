using Microsoft.AspNetCore.Http;

namespace LuxuryApp.Services.Funcionarios
{
    /// <summary>Resultado de guardar una foto de funcionario.</summary>
    public sealed record FuncionarioPhotoSaveResult(
        bool Success,
        string? Url,
        string? StoragePath,
        string? Error)
    {
        public static FuncionarioPhotoSaveResult Ok(string url, string storagePath) =>
            new(true, url, storagePath, null);

        public static FuncionarioPhotoSaveResult Fail(string error) =>
            new(false, null, null, error);
    }

    /// <summary>
    /// Almacenamiento seguro de fotos de funcionarios en disco (wwwroot), aislado por tenant.
    /// Valida extensión, content-type y firma real del archivo (magic bytes). Nunca usa el nombre
    /// original: genera un GUID. Rechaza SVG y cualquier archivo que no sea JPG/PNG/WEBP real.
    /// </summary>
    public interface IFuncionarioPhotoStorageService
    {
        Task<FuncionarioPhotoSaveResult> SaveAsync(
            Guid tenantId,
            IFormFile file,
            string? previousStoragePath,
            CancellationToken cancellationToken = default);

        /// <summary>Borra el archivo físico si existe. No lanza si ya no existe.</summary>
        void Delete(string? storagePath);
    }
}
