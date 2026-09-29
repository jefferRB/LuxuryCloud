using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Legal
{
    public class ContractDocument
    {
        public Guid Id { get; set; }

        [Required]
        [MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string VersionNumber { get; set; } = string.Empty;

        [Required]
        public string ContentHtml { get; set; } = string.Empty;

        [Required]
        [MaxLength(64)]
        public string ContentHash { get; set; } = string.Empty;

        public bool IsActive { get; set; }
        public DateTime EffectiveFromUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public ICollection<ContractAcceptanceRecord> Acceptances { get; set; } = new List<ContractAcceptanceRecord>();
    }
}
