using LuxuryApp.Services.Billing;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Stub del gestor de cancelación del add-on para tests que no ejercitan esa vía.
    /// IsEnabled=false (igual que producción con TilopayRepeatAdmin:Enabled=false) y las operaciones
    /// no hacen nada / responden de forma segura.
    /// </summary>
    internal sealed class NoOpAddonSubscriptionManager : IAddonSubscriptionManager
    {
        public bool IsEnabled => false;

        public Task<ProviderSubscriptionActionResult> RequestAddonCancellationAtPeriodEndAsync(
            Guid tenantId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderSubscriptionActionResult.Fail("Add-on manager deshabilitado en test."));

        public Task<ProviderCancellationAttemptResult> TryCancelPendingAddonSubscriberAsync(
            Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderCancellationAttemptResult.NotCalled("Add-on manager deshabilitado en test."));

        public Task ScheduleAddonCancellationForBaseCancellationAsync(
            Guid tenantId, string actorUserId, string? reason, bool immediate, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
