namespace LuxuryApp.Services.Account
{
    public sealed class AccountEmailOptions
    {
        public const string SectionName = "Email";

        public string SmtpHost     { get; set; } = "smtp.resend.com";
        public int    SmtpPort     { get; set; } = 587;
        public string SmtpUsername { get; set; } = "resend";
        public string SmtpPassword { get; set; } = string.Empty;
        public string FromEmail    { get; set; } = "no-reply@mail.luxurycloud.app";
        public string FromName     { get; set; } = "LuxuryCloud";
        public string ReplyToEmail { get; set; } = string.Empty;
        public string BaseUrl      { get; set; } = string.Empty;
    }
}
