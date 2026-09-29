namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformTenantUsageViewModel
    {
        public int Citas7d { get; init; }
        public int Citas30d { get; init; }
        public int Cobros7d { get; init; }
        public int Cobros30d { get; init; }
        public decimal MontoCobros30d { get; init; }
        public int BookingRequests30d { get; init; }
        public int BookingRequestsPending { get; init; }
        public int BookingRequestsConfirmed30d { get; init; }
        public int BookingRequestsRejected30d { get; init; }
        public int WhatsAppEnviados30d { get; init; }
        public DateTime? LastActivityUtc { get; init; }

        public bool HasRecentActivity7d  => LastActivityUtc.HasValue && LastActivityUtc.Value >= DateTime.UtcNow.AddDays(-7);
        public bool HasRecentActivity14d => LastActivityUtc.HasValue && LastActivityUtc.Value >= DateTime.UtcNow.AddDays(-14);
        public bool HasRecentActivity30d => LastActivityUtc.HasValue && LastActivityUtc.Value >= DateTime.UtcNow.AddDays(-30);
    }
}
