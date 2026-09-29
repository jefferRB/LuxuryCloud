using System.Security.Claims;
using LuxuryApp.Models.Identity;

namespace LuxuryApp.Services.Account
{
    /// <summary>
    /// Arma el modelo de lectura de "Mi cuenta".
    ///
    /// <para>
    /// Existe porque la pantalla la renderizan DOS controladores: <c>AccountsController</c> (su
    /// dueño) y <c>ConfiguracionFiscalController</c> cuando el guardado del IVA vuelve con errores
    /// de validación y tiene que repintar la página con el ModelState del request. Sin esto, el
    /// segundo tendría que copiar el armado del primero y las dos copias se irían separando.
    /// </para>
    /// </summary>
    public interface IAccountSettingsPageBuilder
    {
        /// <summary>
        /// Construye la página para el usuario AUTENTICADO. El tenant y el usuario salen de la
        /// identidad, nunca del request.
        /// </summary>
        /// <returns><c>null</c> si la sesión ya no resuelve a un usuario.</returns>
        Task<AccountSettingsPageViewModel?> BuildAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default);
    }
}
