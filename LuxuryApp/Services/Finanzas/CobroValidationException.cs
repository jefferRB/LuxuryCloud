namespace LuxuryApp.Services.Finanzas
{
    public sealed class CobroValidationException : InvalidOperationException
    {
        public CobroValidationException(string message, string? modelStateKey = null)
            : base(message)
        {
            ModelStateKey = modelStateKey;
        }

        public string? ModelStateKey { get; }
    }
}
