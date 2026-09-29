using LuxuryApp.Models.Asociados;

namespace LuxuryApp.Services.Asociados
{
    /// <summary>
    /// Permisos de asociados. La verificación va contra BASE DE DATOS (nunca contra claims de la
    /// cookie), por lo que un cambio del administrador aplica en el siguiente request sin obligar
    /// a cerrar sesión. Dentro de un mismo request el resultado se cachea.
    ///
    /// <para>Todas las operaciones son tenant-safe por los filtros globales del contexto.</para>
    /// </summary>
    public interface IAssociatePermissionService
    {
        /// <summary>
        /// Permisos efectivos del usuario autenticado. Devuelve
        /// <see cref="AssociatePermissionSet.Ninguno"/> si no es asociado, si el asociado está
        /// inactivo o si su cuenta está bloqueada.
        /// </summary>
        Task<AssociatePermissionSet> ObtenerDelUsuarioActualAsync(CancellationToken cancellationToken = default);

        /// <summary>Permisos concedidos a un asociado concreto (para la pantalla de edición).</summary>
        Task<AssociatePermissionSet> ObtenerDeAsociadoAsync(
            int associateId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reemplaza las concesiones del asociado por las indicadas. Ignora claves fuera del
        /// catálogo. Devuelve false si el asociado no pertenece al tenant actual.
        /// </summary>
        Task<bool> GuardarAsync(
            int associateId,
            IEnumerable<string> permisosConcedidos,
            string? actorUserId,
            CancellationToken cancellationToken = default);

        /// <summary>Quita todas las concesiones del asociado (al eliminar su acceso).</summary>
        Task LimpiarAsync(int associateId, CancellationToken cancellationToken = default);
    }
}
