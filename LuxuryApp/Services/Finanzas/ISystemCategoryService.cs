using LuxuryApp.Models.Finanzas;

namespace LuxuryApp.Services.Finanzas
{
    /// <summary>
    /// Único dueño de la identidad de las categorías del sistema (<see cref="SystemCategoryCodes"/>).
    ///
    /// <para>
    /// Existe para que ningún servicio vuelva a implementar por su cuenta el patrón
    /// "si no existe una categoría llamada X, créala": ese patrón es el que permitió que una
    /// fórmula financiera dependiera de un nombre editable.
    /// </para>
    /// </summary>
    public interface ISystemCategoryService
    {
        /// <summary>
        /// Devuelve la categoría del tenant actual para <paramref name="systemCode"/>, creándola si
        /// no existe. Idempotente y seguro ante concurrencia (respaldado por el índice único
        /// <c>UX_Categorias_TenantId_SystemCode</c>).
        ///
        /// <para>
        /// Si ya existe una categoría con el nombre histórico pero sin código, la ADOPTA (le asigna
        /// el código) en vez de crear una duplicada.
        /// </para>
        /// </summary>
        Task<Categoria> EnsureAsync(string systemCode, CancellationToken cancellationToken = default);

        /// <summary>La categoría del tenant para ese código, o null si todavía no existe. No escribe.</summary>
        Task<Categoria?> FindAsync(string systemCode, CancellationToken cancellationToken = default);
    }
}
