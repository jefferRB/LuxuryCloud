namespace LuxuryApp.Services.Tenant
{
    public sealed class TenantExecutionContextAccessor : ITenantExecutionContextAccessor
    {
        private static readonly AsyncLocal<TenantExecutionContextState?> State = new();

        public Guid? CurrentTenantId => State.Value?.TenantId;

        public IDisposable BeginScope(Guid tenantId)
        {
            if (tenantId == Guid.Empty)
            {
                throw new ArgumentException("El tenant scope requiere un TenantId válido.", nameof(tenantId));
            }

            return Push(tenantId);
        }

        public IDisposable ClearScope() => Push(null);

        private static IDisposable Push(Guid? tenantId)
        {
            var previous = State.Value;
            State.Value = new TenantExecutionContextState(tenantId, previous);
            return new PopWhenDisposed(previous);
        }

        private sealed record TenantExecutionContextState(Guid? TenantId, TenantExecutionContextState? Parent);

        private sealed class PopWhenDisposed : IDisposable
        {
            private readonly TenantExecutionContextState? _previous;
            private bool _disposed;

            public PopWhenDisposed(TenantExecutionContextState? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                State.Value = _previous;
                _disposed = true;
            }
        }
    }
}
