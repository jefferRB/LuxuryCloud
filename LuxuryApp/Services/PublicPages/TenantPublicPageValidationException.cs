namespace LuxuryApp.Services.PublicPages
{
    public sealed class TenantPublicPageValidationException : Exception
    {
        public TenantPublicPageValidationException(string message, string? field = null)
            : base(message)
        {
            Field = field;
        }

        public string? Field { get; }
    }
}
