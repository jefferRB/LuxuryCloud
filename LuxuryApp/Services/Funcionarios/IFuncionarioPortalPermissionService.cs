using LuxuryApp.Models.Funcionarios;

namespace LuxuryApp.Services.Funcionarios
{
    /// <summary>
    /// Permisos del portal por funcionario. La verificación es contra base de datos
    /// (no claims), por lo que los cambios del administrador aplican de inmediato.
    /// Todas las operaciones son tenant-safe.
    /// </summary>
    public interface IFuncionarioPortalPermissionService
    {
        /// <summary>Devuelve el conjunto resuelto (defaults + overrides) del funcionario.</summary>
        Task<FuncionarioPortalPermisosSet> ObtenerAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>Verifica un permiso puntual contra base de datos.</summary>
        Task<bool> TienePermisoAsync(
            int funcionarioId,
            string permiso,
            CancellationToken cancellationToken = default);

        /// <summary>Crea las filas de permisos por defecto al habilitar el acceso (idempotente).</summary>
        Task CrearDefaultsAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>Guarda los permisos editados por el administrador.</summary>
        Task<bool> GuardarAsync(
            int funcionarioId,
            IReadOnlyDictionary<string, bool> valores,
            CancellationToken cancellationToken = default);
    }
}
