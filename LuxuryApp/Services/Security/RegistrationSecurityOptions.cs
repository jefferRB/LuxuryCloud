namespace LuxuryApp.Services.Security
{
    public sealed class RegistrationSecurityOptions
    {
        public const string SectionName = "RegistrationSecurity";

        public bool RequireEmailConfirmation { get; set; } = true;
        public bool EnableHoneypot { get; set; } = true;
        public string HoneypotFieldName { get; set; } = "CompanyWebsite";
        public bool BlockSuspiciousEmailPatterns { get; set; } = true;
        public bool ExpirePendingTenantsEnabled { get; set; }
        public int PendingTenantExpirationDays { get; set; } = 7;
        public int PendingTenantExpirationWorkerInitialDelayMinutes { get; set; } = 15;
        public int PendingTenantExpirationWorkerIntervalHours { get; set; } = 24;

        public EndpointRateLimitOptions Registration { get; set; } = new()
        {
            IpPermitLimit = 5,
            EmailPermitLimit = 3,
            WindowMinutes = 10
        };

        public EndpointRateLimitOptions Login { get; set; } = new()
        {
            IpPermitLimit = 20,
            EmailPermitLimit = 8,
            WindowMinutes = 5
        };

        public EndpointRateLimitOptions ForgotPassword { get; set; } = new()
        {
            IpPermitLimit = 5,
            EmailPermitLimit = 3,
            WindowMinutes = 15
        };

        public TurnstileOptions Turnstile { get; set; } = new();

        public sealed class EndpointRateLimitOptions
        {
            public int IpPermitLimit { get; set; }
            public int EmailPermitLimit { get; set; }
            public int WindowMinutes { get; set; } = 10;
        }

        public sealed class TurnstileOptions
        {
            public bool Enabled { get; set; }
            public string SiteKey { get; set; } = string.Empty;
            public string SecretKey { get; set; } = string.Empty;
            public string ResponseFieldName { get; set; } = "cf-turnstile-response";
        }
    }
}
