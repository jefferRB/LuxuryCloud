using LuxuryApp.Models.Fiscal;

namespace LuxuryApp.Models.Identity
{
    /// <summary>
    /// Modelo de LECTURA de "Mi cuenta". Junta en una sola pantalla lo que antes estaban en tres
    /// (cuenta, doble autenticación, impuestos).
    ///
    /// <para>
    /// <b>Sólo para el GET.</b> Ningún POST bindea este tipo: cada sección conserva su propio
    /// modelo de escritura (<see cref="CuentaViewModel"/>, <see cref="ConfiguracionFiscalViewModel"/>)
    /// y su propio endpoint. Así un error guardando el IVA no impide guardar el teléfono, y no se
    /// abre la puerta a overposting de campos que esa acción no debería tocar.
    /// </para>
    /// </summary>
    public sealed class AccountSettingsPageViewModel
    {
        /// <summary>Perfil personal. Se escribe con <c>POST /Accounts/Cuenta</c>.</summary>
        public CuentaViewModel Profile { get; set; } = new();

        /// <summary>Estado de seguridad (sólo lectura: las acciones viven en Seguridad).</summary>
        public AccountSecurityViewModel Security { get; init; } = new();

        /// <summary>
        /// Configuración fiscal del negocio. <c>null</c> cuando el usuario no es Administrador:
        /// la sección entonces NO se construye ni llega al HTML. Esconderla con CSS no sería
        /// seguridad; el endpoint además sigue siendo <c>[Authorize(Roles="Administrador")]</c>.
        /// </summary>
        public ConfiguracionFiscalViewModel? Fiscal { get; set; }

        /// <summary>Ancla a la que saltar al cargar (p. ej. "fiscal" tras un error de guardado).</summary>
        public string? FocusSection { get; set; }
    }

    /// <summary>
    /// Anclas de las secciones de Mi cuenta. Son parte del contrato público: las rutas antiguas
    /// (/ConfiguracionFiscal) redirigen a estos fragmentos, así que no se escriben a mano.
    /// </summary>
    public static class AccountSettingsSections
    {
        public const string Perfil = "perfil";
        public const string Seguridad = "seguridad";
        public const string Preferencias = "preferencias";
        public const string Fiscal = "fiscal";
    }

    /// <summary>Estado de seguridad de la cuenta que se muestra en Mi cuenta.</summary>
    public sealed class AccountSecurityViewModel
    {
        public string Email { get; init; } = string.Empty;

        /// <summary>Verificación en dos pasos activa (Identity <c>TwoFactorEnabled</c>).</summary>
        public bool TwoFactorEnabled { get; init; }

        /// <summary>
        /// Si se puede apagar el TOTP. Lo decide <see cref="Services.Identity.MfaEnrollmentPolicy"/>,
        /// la misma regla que aplica el endpoint.
        /// </summary>
        public bool PuedeDeshabilitarTwoFactor { get; init; }
    }
}
