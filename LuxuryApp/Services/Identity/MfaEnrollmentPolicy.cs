using LuxuryApp.Models.Identity;

namespace LuxuryApp.Services.Identity
{
    /// <summary>
    /// Única respuesta a "¿este usuario puede apagar su verificación en dos pasos?".
    ///
    /// <para>
    /// La regla vivía sólo dentro de <c>SeguridadController</c>. Al mostrar el estado de MFA
    /// también en Mi cuenta habría quedado escrita en dos lugares, y dos copias de una regla de
    /// seguridad terminan divergiendo: la pantalla ofrecería un botón que el endpoint rechaza
    /// (o peor, al revés). Por eso se extrae acá y ambos la consultan.
    /// </para>
    ///
    /// <para>
    /// Esto NO autoriza nada por sí solo: el POST de <c>Seguridad/Deshabilitar</c> sigue
    /// comprobándolo en servidor antes de tocar Identity.
    /// </para>
    /// </summary>
    public static class MfaEnrollmentPolicy
    {
        /// <summary>
        /// Un superadmin de plataforma no puede desactivar su TOTP mientras el enrolamiento
        /// obligatorio esté encendido. Cualquier otro usuario sí.
        /// </summary>
        public static bool PuedeDeshabilitar(AppUsuario usuario, PlatformSecurityOptions options)
        {
            ArgumentNullException.ThrowIfNull(usuario);
            ArgumentNullException.ThrowIfNull(options);

            return !usuario.IsPlatformSuperAdmin || !options.Mfa.SuperAdminEnforcement;
        }
    }
}
