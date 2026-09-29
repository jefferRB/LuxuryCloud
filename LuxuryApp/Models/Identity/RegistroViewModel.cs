using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Identity
{
    public class RegistroViewModel
    {
        [Required(ErrorMessage = "The Email is required")]
        [EmailAddress]
        //todas las validaciones con buen manejo
        public string Email { get; set; } = string.Empty;
        [Required(ErrorMessage = "The Password is required ")]
        [StringLength(50, ErrorMessage = "The {0} must be at least {2} characters long", MinimumLength = 8)]
        [DataType(DataType.Password)]
        [Display(Name = "Contraseña")] // Used to choose the name to display
        public string Password { get; set; } = string.Empty;

        //todas las validaciones con buen manejo
        [Required(ErrorMessage = "The Password Confirmation is required ")]
        [Compare("Password", ErrorMessage = " Password and Password Confirmation are not equals")]
        [DataType(DataType.Password)]
        [Display(Name = "Confirmar Contraseña")] // Used to choose the name to display
        public string ConfirmPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "The Name is required  ")]
        [Display(Name = "Nombre")]
        public string Name { get; set; } = string.Empty;
        [Display(Name = "Telefono")]
        public string PhoneNumber { get; set; } = string.Empty;

        [Display(Name = "Código de acceso")]
        [StringLength(100)]
        public string? AccessCode { get; set; }

        public string? CompanyWebsite { get; set; }

        public Guid? SelectedPlanId { get; set; }

        public Guid? CurrentContractDocumentId { get; set; }

        public string CurrentContractTitle { get; set; } = string.Empty;

        public string CurrentContractVersion { get; set; } = string.Empty;

        public DateTime? CurrentContractEffectiveFromUtc { get; set; }

        [Display(Name = "He leido y acepto el Contrato de Uso del Servicio")]
        [Required(ErrorMessage = "Debes aceptar el contrato para crear tu cuenta.")]
        public bool AcceptCurrentContract { get; set; }

        public bool HasCurrentContract => CurrentContractDocumentId.HasValue && CurrentContractDocumentId.Value != Guid.Empty;
    }
}
