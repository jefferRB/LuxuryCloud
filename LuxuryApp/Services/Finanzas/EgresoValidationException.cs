namespace LuxuryApp.Services.Finanzas
{
    public sealed class EgresoValidationException : InvalidOperationException
    {
        public EgresoValidationException(string message, string? modelStateKey = null)
            : base(message)
        {
            ModelStateKey = modelStateKey;
        }

        public string? ModelStateKey { get; }
    }
}
