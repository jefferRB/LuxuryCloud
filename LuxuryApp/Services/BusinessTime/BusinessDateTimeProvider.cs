using Microsoft.Extensions.Options;

namespace LuxuryApp.Services.BusinessTime
{
    public sealed class BusinessDateTimeProvider : IBusinessDateTimeProvider
    {
        private const string CostaRicaIanaTimeZoneId = "America/Costa_Rica";
        private const string CostaRicaWindowsTimeZoneId = "Central America Standard Time";

        private readonly TimeZoneInfo _timeZone;

        public BusinessDateTimeProvider(
            IOptions<BusinessDateTimeOptions> options,
            ILogger<BusinessDateTimeProvider> logger)
        {
            var configuredTimeZoneId = string.IsNullOrWhiteSpace(options.Value.TimeZoneId)
                ? CostaRicaIanaTimeZoneId
                : options.Value.TimeZoneId.Trim();

            _timeZone = ResolveTimeZone(configuredTimeZoneId, logger);
        }

        public DateTime Now() => NowOffset().DateTime;

        public DateTime Today() => Now().Date;

        public DateTimeOffset NowOffset() =>
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone);

        private static TimeZoneInfo ResolveTimeZone(
            string configuredTimeZoneId,
            ILogger<BusinessDateTimeProvider> logger)
        {
            foreach (var timeZoneId in BuildCandidateTimeZoneIds(configuredTimeZoneId))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                }
                catch (TimeZoneNotFoundException)
                {
                }
                catch (InvalidTimeZoneException)
                {
                }
            }

            logger.LogWarning(
                "No se pudo resolver la zona horaria de negocio {TimeZoneId}. Se usara UTC.",
                configuredTimeZoneId);

            return TimeZoneInfo.Utc;
        }

        private static IEnumerable<string> BuildCandidateTimeZoneIds(string configuredTimeZoneId)
        {
            yield return configuredTimeZoneId;

            if (!string.Equals(configuredTimeZoneId, CostaRicaIanaTimeZoneId, StringComparison.OrdinalIgnoreCase))
            {
                yield return CostaRicaIanaTimeZoneId;
            }

            if (!string.Equals(configuredTimeZoneId, CostaRicaWindowsTimeZoneId, StringComparison.OrdinalIgnoreCase))
            {
                yield return CostaRicaWindowsTimeZoneId;
            }
        }
    }
}
