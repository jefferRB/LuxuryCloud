namespace LuxuryApp.Services.Contracts
{
    public static class ContractRequestMetadataResolver
    {
        public static string? ResolveClientIp(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(forwardedFor))
            {
                return forwardedFor.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
            }

            return httpContext.Connection.RemoteIpAddress?.ToString();
        }

        public static string? ResolveUserAgent(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            return httpContext.Request.Headers.UserAgent.ToString();
        }
    }
}
