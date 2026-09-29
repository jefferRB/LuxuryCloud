namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// <see cref="TimeProvider"/> determinista para pruebas: devuelve siempre el instante
    /// configurado y permite avanzarlo manualmente. Evita depender del reloj real.
    /// </summary>
    internal sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);

        public void Set(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
