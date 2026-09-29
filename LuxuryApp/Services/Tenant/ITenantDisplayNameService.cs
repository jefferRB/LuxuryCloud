namespace LuxuryApp.Services.Tenant
{
    public interface ITenantDisplayNameService
    {
        Task<string> GetCurrentTenantDisplayNameAsync(CancellationToken cancellationToken = default);

        Task<string> GetTenantDisplayNameAsync(Guid tenantId, CancellationToken cancellationToken = default);

        Task<string?> GetPublicTenantDisplayNameBySlugAsync(string slug, CancellationToken cancellationToken = default);

        string NormalizeDisplayName(string? value);

        bool ContainsInvalidDisplayNameCharacters(string? value);
    }
}
