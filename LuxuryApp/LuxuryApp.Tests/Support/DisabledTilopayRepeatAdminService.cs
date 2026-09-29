using LuxuryApp.Services.Tilopay;

namespace LuxuryApp.Tests
{
    /// <summary>
    /// Stub del cliente admin de TiloPay para pruebas donde la integración está deshabilitada.
    /// Refleja el comportamiento de producción por defecto (TilopayRepeatAdmin:Enabled=false):
    /// IsEnabled=false y cualquier invocación lanza, igual que el servicio real deshabilitado.
    /// </summary>
    internal sealed class DisabledTilopayRepeatAdminService : ITilopayRepeatAdminService
    {
        public bool IsEnabled => false;

        public Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(int tilopayPlanId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("TiloPay Repeat Admin deshabilitado.");

        public Task<SubscriberResolutionResult> ResolveSubscriberAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("TiloPay Repeat Admin deshabilitado.");

        public Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("TiloPay Repeat Admin deshabilitado.");

        public Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("TiloPay Repeat Admin deshabilitado.");

        public Task<TilopayAdminOperationResult> PauseSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("TiloPay Repeat Admin deshabilitado.");

        public Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("TiloPay Repeat Admin deshabilitado.");

        public Task<TilopayAdminOperationResult> DeleteSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("TiloPay Repeat Admin deshabilitado.");

        public Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(string subscriberId, TilopaySubscriberStatus status, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("TiloPay Repeat Admin deshabilitado.");
    }
}
