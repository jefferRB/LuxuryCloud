namespace LuxuryApp.Services.Productos
{
    public sealed class ProductoValidationException : Exception
    {
        public ProductoValidationException(string message, string? modelStateKey = null)
            : base(message)
        {
            ModelStateKey = modelStateKey;
        }

        public string? ModelStateKey { get; }
    }
}
