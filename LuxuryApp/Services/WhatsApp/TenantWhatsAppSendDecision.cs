namespace LuxuryApp.Services.WhatsApp
{
    public sealed record TenantWhatsAppSendDecision(
        bool CanSend,
        string? ErrorCode,
        string? ErrorMessage,
        int TodayUsage,
        int DailyMessageLimit,
        int? MonthlyUsage,
        int? MonthlyMessageLimit)
    {
        public static TenantWhatsAppSendDecision Allowed(
            int todayUsage,
            int dailyMessageLimit,
            int? monthlyUsage = null,
            int? monthlyMessageLimit = null) =>
            new(true, null, null, todayUsage, dailyMessageLimit, monthlyUsage, monthlyMessageLimit);

        public static TenantWhatsAppSendDecision Denied(
            string errorCode,
            string errorMessage,
            int todayUsage,
            int dailyMessageLimit,
            int? monthlyUsage = null,
            int? monthlyMessageLimit = null) =>
            new(false, errorCode, errorMessage, todayUsage, dailyMessageLimit, monthlyUsage, monthlyMessageLimit);
    }
}
