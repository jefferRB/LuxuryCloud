namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformRecentUserViewModel
    {
        public string Email { get; init; } = string.Empty;
        public string? Name { get; init; }
        public string? TenantName { get; init; }
        public bool IsPlatformSuperAdmin { get; init; }
    }
}
