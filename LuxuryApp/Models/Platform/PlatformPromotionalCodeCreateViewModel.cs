using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformPromotionalCodeCreateViewModel
    {
        [Required]
        [StringLength(100)]
        [Display(Name = "Codigo")]
        public string Codigo { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Plan")]
        public Guid PlanId { get; set; }

        [Range(1, 365)]
        [Display(Name = "Dias gratis")]
        public int DiasGratis { get; set; } = 30;

        [Range(1, 100000)]
        [Display(Name = "Maximo de usos")]
        public int? MaxUsos { get; set; } = 1;

        [Display(Name = "Expiracion UTC")]
        public DateTime? FechaExpiracionUtc { get; set; }

        [Display(Name = "Solo primer registro")]
        public bool SoloPrimerRegistro { get; set; } = true;

        [EmailAddress]
        [StringLength(256)]
        [Display(Name = "Email objetivo")]
        public string? EmailObjetivo { get; set; }

        [StringLength(2000)]
        [Display(Name = "Notas internas")]
        public string? NotasInternas { get; set; }

        [Display(Name = "Activo")]
        public bool Activo { get; set; } = true;
    }
}
