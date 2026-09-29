using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.Support
{
    /// <summary>Monitor de opciones fijo, para probar servicios que leen configuración global.</summary>
    internal sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    {
        public TestOptionsMonitor(TOptions value)
        {
            CurrentValue = value;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
