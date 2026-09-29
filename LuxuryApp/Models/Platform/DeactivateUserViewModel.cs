using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Platform
{
    /// <summary>
    /// Formulario de "zona peligrosa" para desactivar/reactivar un usuario desde plataforma.
    /// El TenantId/Email/Nombre mostrados provienen de la DB; los campos de confirmación los
    /// escribe el SuperAdmin y se contrastan contra la verdad en el servidor.
    /// </summary>
    public class DeactivateUserViewModel
    {
        [Required]
        public string UserId { get; set; } = string.Empty;

        public Guid TenantId { get; set; }

        // Datos de solo lectura (mostrados en la vista).
        public string UserEmail { get; set; } = string.Empty;
        public string? UserName { get; set; }
        public string TenantName { get; set; } = string.Empty;
        public bool IsCurrentlyActive { get; set; }
        public bool IsPlatformSuperAdmin { get; set; }
        public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

        // Indica si la vista es de reactivación (no exige escribir email/tenant).
        public bool IsReactivation { get; set; }

        [Display(Name = "Correo del usuario")]
        public string? ConfirmationEmail { get; set; }

        [Display(Name = "Nombre del negocio")]
        public string? ConfirmationTenantName { get; set; }

        [Required(ErrorMessage = "El motivo es obligatorio.")]
        [StringLength(500, MinimumLength = 5, ErrorMessage = "Describe el motivo (mínimo 5 caracteres).")]
        [Display(Name = "Motivo")]
        public string Reason { get; set; } = string.Empty;

        [Required(ErrorMessage = "Debes ingresar tu contraseña para confirmar.")]
        [DataType(DataType.Password)]
        [Display(Name = "Tu contraseña de SuperAdmin")]
        public string CurrentSuperAdminPassword { get; set; } = string.Empty;

        public bool Acknowledge { get; set; }
    }
}
