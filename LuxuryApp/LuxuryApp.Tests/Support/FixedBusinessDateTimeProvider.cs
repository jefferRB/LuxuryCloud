using LuxuryApp.Services.BusinessTime;

namespace LuxuryApp.Tests.Support
{
    internal sealed class FixedBusinessDateTimeProvider : IBusinessDateTimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedBusinessDateTimeProvider(DateTime? now = null)
        {
            var localNow = now ?? new DateTime(2026, 5, 26, 10, 30, 0);
            _now = new DateTimeOffset(localNow, TimeSpan.FromHours(-6));
        }

        public DateTime Now() => _now.DateTime;

        public DateTime Today() => Now().Date;

        public DateTimeOffset NowOffset() => _now;
    }
}
