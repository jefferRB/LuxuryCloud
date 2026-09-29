using System.Security.Claims;

namespace LuxuryApp.Services.Asociados
{
    /// <summary>
    /// Decide a dónde llevar a una persona apenas inicia sesión.
    ///
    /// <para>
    /// Existe porque "todos al Dashboard" dejó de ser cierto: alguien de Marketing con solo
    /// <c>PublicWebsite.Manage</c> caería en un Access Denied en su primer segundo dentro del
    /// producto. La regla es simple: el primer destino que la persona SÍ puede abrir.
    /// </para>
    /// </summary>
    public interface IPostLoginDestinationService
    {
        /// <summary>
        /// Ruta local a la que enviar al usuario recién autenticado. Nunca devuelve una ruta que
        /// el usuario no pueda abrir.
        /// </summary>
        Task<string> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);

        /// <summary>
        /// True si el usuario puede abrir la ruta local indicada. Se usa para respetar el
        /// <c>returnUrl</c> solo cuando lleva a un lugar permitido.
        /// </summary>
        Task<bool> PuedeAbrirAsync(
            ClaimsPrincipal principal,
            string localPath,
            CancellationToken cancellationToken = default);
    }
}
