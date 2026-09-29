namespace LuxuryApp.Models.SaaS
{
    public class OpcionesTilopay
    {
        public string BaseUrl { get; set; } = "https://app.tilopay.com/";
        public string ApiUser { get; set; } = string.Empty;
        public string ApiPassword { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string MerchantId { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 30;
        public string WebhookAccessToken { get; set; } = string.Empty;
        public string WebhookAccessTokenQueryParameter { get; set; } = "access_token";
    }
}
