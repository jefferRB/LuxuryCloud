using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Identity
{
    public sealed class ResetPasswordViewModel
    {
        [Required]
        public string UserId { get; set; } = string.Empty;

        [Required]
        public string Token { get; set; } = string.Empty;

        [Required(ErrorMessage = "La contrasena es requerida.")]
        [StringLength(50, MinimumLength = 8, ErrorMessage = "La contrasena debe tener entre 8 y 50 caracteres.")]
        [DataType(DataType.Password)]
        [Display(Name = "Nueva contrasena")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Confirma la nueva contrasena.")]
        [DataType(DataType.Password)]
        [Display(Name = "Confirmar contrasena")]
        [Compare("Password", ErrorMessage = "Las contrasenas no coinciden.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
