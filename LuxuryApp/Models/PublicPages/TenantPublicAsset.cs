using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.SaaS;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.PublicPages
{
    public enum TenantPublicAssetType
    {
        Logo = 1,
        Cover = 2,
        BusinessGallery = 3,
        ServiceMain = 4,
        ServiceGallery = 5,
        Location = 6
    }

    public sealed class TenantPublicAsset : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [BindNever]
        public Guid TenantId { get; set; }

        public Tenant? Tenant { get; set; }

        public Guid? TenantPublicPageId { get; set; }

        public TenantPublicPage? TenantPublicPage { get; set; }

        public int? ServicioId { get; set; }

        public Servicio? Servicio { get; set; }

        public TenantPublicAssetType AssetType { get; set; }

        [Required]
        [MaxLength(500)]
        public string StorageKey { get; set; } = string.Empty;

        [Required]
        [MaxLength(800)]
        public string PublicUrl { get; set; } = string.Empty;

        [Required]
        [MaxLength(60)]
        public string ContentType { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        [MaxLength(180)]
        public string? OriginalFileName { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime? DeletedAtUtc { get; set; }
    }
}
