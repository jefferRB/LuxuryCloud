using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.SaaS
{
    public class PromotionalCode
    {
        public Guid Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string Codigo { get; set; } = string.Empty;

        public bool Activo { get; set; } = true;

        public PromotionalBenefitType TipoBeneficio { get; set; } = PromotionalBenefitType.FreeAccessDays;

        public int DiasGratis { get; set; } = 30;

        public Guid PlanId { get; set; }

        public int? MaxUsos { get; set; }

        public int UsosActuales { get; set; }

        public DateTime? FechaExpiracionUtc { get; set; }

        public bool SoloPrimerRegistro { get; set; }

        [MaxLength(256)]
        public string? EmailObjetivo { get; set; }

        [MaxLength(450)]
        public string? CreadoPorUserId { get; set; }

        [MaxLength(2000)]
        public string? NotasInternas { get; set; }

        public DateTime FechaCreacionUtc { get; set; } = DateTime.UtcNow;

        public DateTime? FechaActualizacionUtc { get; set; }

        public Plan? Plan { get; set; }
        public ICollection<PromotionalCodeRedemption> Redemptions { get; set; } = new List<PromotionalCodeRedemption>();
        public ICollection<TenantCommercialAccessGrant> AccessGrants { get; set; } = new List<TenantCommercialAccessGrant>();
    }
}
