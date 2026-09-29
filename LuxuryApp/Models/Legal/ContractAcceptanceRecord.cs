using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Identity;

namespace LuxuryApp.Models.Legal
{
    public class ContractAcceptanceRecord
    {
        public Guid Id { get; set; }

        [Required]
        [MaxLength(450)]
        public string UserId { get; set; } = string.Empty;

        public Guid ContractDocumentId { get; set; }

        [Required]
        [MaxLength(50)]
        public string ContractVersion { get; set; } = string.Empty;

        [Required]
        [MaxLength(64)]
        public string AcceptedContentHash { get; set; } = string.Empty;

        [Required]
        [MaxLength(40)]
        public string AcceptanceSource { get; set; } = string.Empty;

        [MaxLength(64)]
        public string? IpAddress { get; set; }

        [MaxLength(2048)]
        public string? UserAgent { get; set; }

        public DateTime AcceptedAtUtc { get; set; }

        public AppUsuario? User { get; set; }
        public ContractDocument? ContractDocument { get; set; }
    }
}
