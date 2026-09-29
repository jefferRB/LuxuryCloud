using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Identity
{
    public sealed class CuentaViewModel
    {
        public string Email { get; init; } = string.Empty;

        [Required(ErrorMessage = "El nombre es requerido.")]
        [StringLength(100, ErrorMessage = "El nombre no puede superar 100 caracteres.")]
        [RegularExpression(@"^[^\p{Cc}\p{Cf}]*$", ErrorMessage = "El nombre no puede contener saltos de línea ni caracteres de control.")]
        [Display(Name = "Nombre visible")]
        public string Name { get; set; } = string.Empty;

        [Phone(ErrorMessage = "El formato del telefono no es valido.")]
        [StringLength(20, ErrorMessage = "El telefono no puede superar 20 caracteres.")]
        [Display(Name = "Telefono")]
        public string? PhoneNumber { get; set; }
    }
}
