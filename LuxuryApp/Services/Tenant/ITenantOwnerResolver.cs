using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Tenant
{
    /// <summary>
    /// Resuelve de forma determinista el contacto principal (owner/administrador) de un tenant.
    /// UNICA fuente de verdad: no debe existir otro <c>OrderBy(email).First()</c> en el codigo.
    /// </summary>
    public interface ITenantOwnerResolver
    {
        Task<TenantOwnerResolution> ResolveAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Version batch para listados de plataforma (evita N queries). Devuelve una entrada por
        /// cada tenant solicitado, incluidos los que no tienen usuarios.
        /// </summary>
        Task<Dictionary<Guid, TenantOwnerResolution>> ResolveBatchAsync(
            IReadOnlyList<Guid> tenantIds,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Correo del contacto principal, o null si no hay ninguno utilizable. Atajo para los
        /// servicios que solo necesitan "a quien le escribo".
        /// </summary>
        Task<string?> ResolveOwnerEmailAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);
    }
}
