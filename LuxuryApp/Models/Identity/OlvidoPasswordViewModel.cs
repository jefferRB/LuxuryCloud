using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Identity
{
    public class OlvidoPasswordViewModel
    {
        [Required(ErrorMessage = "El correo electrónico es requerido.")]
        [EmailAddress(ErrorMessage = "El formato del correo no es válido.")]
        public string Email { get; set; } = string.Empty;
    }
}
