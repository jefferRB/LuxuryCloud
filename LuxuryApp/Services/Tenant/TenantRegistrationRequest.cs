namespace LuxuryApp.Services.Tenant
{
    public class TenantRegistrationRequest
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? PhoneNumber { get; set; }
        public string? AccessCode { get; set; }
        public bool AcceptCurrentContract { get; set; }
        public bool RequiresEmailConfirmation { get; set; }
        public Guid? SubmittedContractDocumentId { get; set; }
        public string? ContractIpAddress { get; set; }
        public string? ContractUserAgent { get; set; }
    }
}
