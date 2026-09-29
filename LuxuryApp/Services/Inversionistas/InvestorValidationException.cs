namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Error de negocio del módulo de inversionistas con mensaje apto para mostrar al usuario.
    /// Mismo patrón que <c>CalendarValidationException</c>: el controlador lo traduce a ModelState
    /// o a un mensaje TempData, nunca a un 500.
    /// </summary>
    public sealed class InvestorValidationException : Exception
    {
        public InvestorValidationException(string message, string? modelStateKey = null)
            : base(message)
        {
            ModelStateKey = modelStateKey;
        }

        public string? ModelStateKey { get; }
    }
}
