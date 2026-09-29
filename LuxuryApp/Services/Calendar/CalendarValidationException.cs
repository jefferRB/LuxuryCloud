namespace LuxuryApp.Services.Calendar
{
    public sealed class CalendarValidationException : Exception
    {
        public CalendarValidationException(string message, string? modelStateKey = null)
            : base(message)
        {
            ModelStateKey = modelStateKey;
        }

        public string? ModelStateKey { get; }
    }
}
