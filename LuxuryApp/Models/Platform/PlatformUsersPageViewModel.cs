namespace LuxuryApp.Models.Platform
{
    public class PlatformTenantUserRowViewModel
    {
        public string UserId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Name { get; set; }
        public Guid TenantId { get; set; }
        public string TenantName { get; set; } = string.Empty;
        public bool TenantActive { get; set; }
        public bool State { get; set; }
        public bool IsPlatformSuperAdmin { get; set; }
        public bool IsFuncionario { get; set; }
        public DateTimeOffset? LockoutEnd { get; set; }
        public string Roles { get; set; } = string.Empty;
    }

    public class PlatformUsersPageViewModel
    {
        public IReadOnlyList<PlatformTenantUserRowViewModel> Users { get; set; } = Array.Empty<PlatformTenantUserRowViewModel>();
        public string? SearchTerm { get; set; }
        public string? StatusFilter { get; set; }
        public int TotalActive { get; set; }
        public int TotalInactive { get; set; }
        public int TotalCount { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int PageSize { get; set; } = 50;
        public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;
        public bool HasPreviousPage => CurrentPage > 1;
        public bool HasNextPage => CurrentPage < TotalPages;
    }
}
