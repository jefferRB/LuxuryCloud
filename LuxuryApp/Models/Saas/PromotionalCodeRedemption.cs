using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.SaaS
{
    public class PromotionalCodeRedemption : ITenantEntity
    {
        public Guid Id { get; set; }

        [Required]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Required]
        public Guid PromotionalCodeId { get; set; }

        public Guid? TenantCommercialAccessGrantId { get; set; }

        [MaxLength(450)]
        public string? ConsumidoPorUserId { get; set; }

        [MaxLength(256)]
        public string EmailConsumidor { get; set; } = string.Empty;

        public DateTime FechaConsumoUtc { get; set; } = DateTime.UtcNow;

        public PromotionalCode? PromotionalCode { get; set; }
        public TenantCommercialAccessGrant? AccessGrant { get; set; }
    }
}
