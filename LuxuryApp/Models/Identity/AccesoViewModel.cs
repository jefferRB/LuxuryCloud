using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Identity
{
    public class AccesoViewModel
    {
        [Required(ErrorMessage = "The Email is required")]
        [EmailAddress]
        //todas las validaciones con buen manejo
        public string Email { get; set; } = string.Empty;
        [Required(ErrorMessage = "The Password is required ")]
        [DataType(DataType.Password)]
        [Display(Name = "Contraseña")] // Used to choose the name to display
        public string Password { get; set; } = string.Empty;
        // RememberMe controla ÚNICAMENTE la persistencia de la cookie de autenticación
        // (sesión vs. dispositivo de confianza). No guarda ni transmite la contraseña: el
        // navegador o gestor de contraseñas es quien decide ofrecer guardar las credenciales.
        [Display(Name = "Recordar este dispositivo y mantener mi sesión iniciada")]
        public bool RememberMe { get; set; }
    }
}
