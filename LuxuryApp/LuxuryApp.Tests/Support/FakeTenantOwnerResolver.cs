using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Tenant;

namespace LuxuryApp.Tests.Support
{
    internal sealed class FakeTenantOwnerResolver : ITenantOwnerResolver
    {
        public string? OwnerEmail { get; init; } = "owner@example.com";

        public Task<TenantOwnerResolution> ResolveAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Build(tenantId));

        public Task<Dictionary<Guid, TenantOwnerResolution>> ResolveBatchAsync(
            IReadOnlyList<Guid> tenantIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantIds
                .Distinct()
                .ToDictionary(tenantId => tenantId, Build));

        public Task<string?> ResolveOwnerEmailAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OwnerEmail);

        private TenantOwnerResolution Build(Guid tenantId) =>
            string.IsNullOrWhiteSpace(OwnerEmail)
                ? TenantOwnerResolution.Empty(tenantId)
                : new TenantOwnerResolution
                {
                    TenantId = tenantId,
                    Source = TenantOwnerSource.Administrador,
                    Owner = new TenantUserSummary
                    {
                        UserId = "owner",
                        Email = OwnerEmail,
                        Name = "Owner",
                        State = true,
                        EmailConfirmed = true,
                        Kind = TenantUserKind.Administrador,
                        Roles = new[] { "Administrador" }
                    }
                };
    }
}
