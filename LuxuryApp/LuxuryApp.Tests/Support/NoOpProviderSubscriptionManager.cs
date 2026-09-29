using LuxuryApp.Services.Billing;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Fake inerte de <see cref="IProviderSubscriptionManager"/> para construir controllers en tests
    /// que no ejercitan el ciclo de vida. Los tests que SÍ lo verifican usan el manager real.
    /// </summary>
    public sealed class NoOpProviderSubscriptionManager : IProviderSubscriptionManager
    {
        public bool IsEnabled => false;

        public Task<ProviderCancellationAttemptResult> TryCancelOldSubscriberForUpgradeAsync(
            Guid tenantId, Guid? intentId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderCancellationAttemptResult.NotCalled("no-op"));

        public Task<ProviderSubscriptionActionResult> RequestCancellationAtPeriodEndAsync(
            Guid tenantId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderSubscriptionActionResult.Fail("no-op"));

        public Task<ProviderSubscriptionActionResult> CancelAsync(
            Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderSubscriptionActionResult.Fail("no-op"));

        public Task<ProviderSubscriptionActionResult> PauseAsync(
            Guid tenantId, string actorUserId, string actorEmail, bool immediate = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderSubscriptionActionResult.Fail("no-op"));

        public Task<ProviderSubscriptionActionResult> ReactivateAsync(
            Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderSubscriptionActionResult.Fail("no-op"));

        public Task<ProviderSubscriptionActionResult> ReactivateRenewalAsync(
            Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderSubscriptionActionResult.Fail("no-op"));

        public Task<ProviderSubscriptionActionResult> SyncProviderStatusAsync(
            Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderSubscriptionActionResult.Fail("no-op"));
    }
}
