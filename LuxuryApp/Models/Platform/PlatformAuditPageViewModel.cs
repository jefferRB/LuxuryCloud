namespace LuxuryApp.Models.Platform
{
    public class PlatformAuditLogRowViewModel
    {
        public DateTime CreatedAtUtc { get; set; }
        public string ActorEmail { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string? TenantName { get; set; }
        public string? TargetUserEmail { get; set; }
        public string? Reason { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
    }

    public class PlatformAuditPageViewModel
    {
        public IReadOnlyList<PlatformAuditLogRowViewModel> Entries { get; set; } = Array.Empty<PlatformAuditLogRowViewModel>();
        public string? FiltroAccion { get; set; }
        public string? FiltroTenant { get; set; }
        public string? FiltroActor { get; set; }
        public int TotalCount { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int PageSize { get; set; } = 50;
        public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;
        public bool HasPreviousPage => CurrentPage > 1;
        public bool HasNextPage => CurrentPage < TotalPages;
    }
}
