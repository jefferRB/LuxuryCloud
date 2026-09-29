using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.SaaS
{
    public class TenantCommercialAccessGrant : ITenantEntity
    {
        public Guid Id { get; set; }

        [Required]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Required]
        public Guid PlanId { get; set; }

        public TenantCommercialAccessGrantSource Source { get; set; } = TenantCommercialAccessGrantSource.PromotionalCode;

        public bool Activo { get; set; } = true;

        public bool RequiresBilling { get; set; }

        public DateTime FechaInicioUtc { get; set; } = DateTime.UtcNow;

        public DateTime FechaFinUtc { get; set; }

        public Guid? PromotionalCodeId { get; set; }

        [MaxLength(450)]
        public string? CreadoPorUserId { get; set; }

        [MaxLength(2000)]
        public string? NotasInternas { get; set; }

        public Tenant? Tenant { get; set; }
        public Plan? Plan { get; set; }
        public PromotionalCode? PromotionalCode { get; set; }
        public ICollection<PromotionalCodeRedemption> Redemptions { get; set; } = new List<PromotionalCodeRedemption>();
    }
}
