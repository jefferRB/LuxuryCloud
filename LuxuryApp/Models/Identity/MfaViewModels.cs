using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Identity
{
    /// <summary>Paso 2 del login: verificación del código TOTP.</summary>
    public sealed class VerificarCodigoViewModel
    {
        [Required(ErrorMessage = "Ingresa el código de tu aplicación de autenticación.")]
        [StringLength(8, MinimumLength = 6, ErrorMessage = "El código tiene 6 dígitos.")]
        [DataType(DataType.Text)]
        [Display(Name = "Código de verificación")]
        public string Codigo { get; set; } = string.Empty;

        public bool RememberMe { get; set; }

        public string? ReturnUrl { get; set; }
    }

    /// <summary>Paso 2 del login usando un código de recuperación de un solo uso.</summary>
    public sealed class CodigoRecuperacionViewModel
    {
        [Required(ErrorMessage = "Ingresa uno de tus códigos de recuperación.")]
        [StringLength(24, MinimumLength = 6, ErrorMessage = "El código de recuperación no es válido.")]
        [DataType(DataType.Text)]
        [Display(Name = "Código de recuperación")]
        public string Codigo { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }
    }

    /// <summary>Pantalla de enrolamiento de TOTP (/Seguridad/Enrolar).</summary>
    public sealed class EnrolarMfaViewModel
    {
        public bool TwoFactorActivo { get; init; }

        public bool PuedeDeshabilitar { get; init; }

        /// <summary>Clave compartida formateada en grupos de 4 para ingreso manual.</summary>
        public string ClaveFormateada { get; init; } = string.Empty;

        /// <summary>URI otpauth:// que el JS vendoreado dibuja como QR client-side.</summary>
        public string OtpauthUri { get; init; } = string.Empty;

        [Required(ErrorMessage = "Ingresa el código que muestra tu aplicación.")]
        [StringLength(8, MinimumLength = 6, ErrorMessage = "El código tiene 6 dígitos.")]
        [DataType(DataType.Text)]
        [Display(Name = "Código de verificación")]
        public string Codigo { get; set; } = string.Empty;
    }

    /// <summary>Códigos de recuperación mostrados una única vez tras enrolar.</summary>
    public sealed class CodigosRecuperacionViewModel
    {
        public IReadOnlyList<string> Codigos { get; init; } = Array.Empty<string>();
    }
}
