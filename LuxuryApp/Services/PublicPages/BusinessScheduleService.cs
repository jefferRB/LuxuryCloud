using System.Globalization;
using System.Text.Json;
using LuxuryApp.Models.PublicPages;

namespace LuxuryApp.Services.PublicPages
{
    public sealed class BusinessScheduleService : IBusinessScheduleService
    {
        private const int DayCount = 7;

        // Nombres en espanol, orden Lunes(0) -> Domingo(6).
        private static readonly string[] DayNames =
        {
            "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo"
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };

        public BusinessSchedule? TryDeserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var schedule = JsonSerializer.Deserialize<BusinessSchedule>(json, JsonOptions);
                if (schedule?.Days is null || !schedule.HasAnyOpenDay)
                {
                    return null;
                }

                // Normaliza a exactamente 7 dias por si el JSON viene corto/largo.
                var normalized = new BusinessSchedule();
                for (var i = 0; i < DayCount; i++)
                {
                    var source = i < schedule.Days.Count ? schedule.Days[i] : new BusinessScheduleDay { Closed = true };
                    normalized.Days.Add(SanitizeDay(source));
                }

                return normalized.HasAnyOpenDay ? normalized : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public BusinessSchedule? BuildFromInputs(IEnumerable<BusinessScheduleDayInput>? inputs)
        {
            if (inputs is null)
            {
                return null;
            }

            var byIndex = inputs
                .Where(input => input.DayIndex is >= 0 and < DayCount)
                .GroupBy(input => input.DayIndex)
                .ToDictionary(group => group.Key, group => group.First());

            var schedule = new BusinessSchedule();
            for (var dayIndex = 0; dayIndex < DayCount; dayIndex++)
            {
                var day = new BusinessScheduleDay();
                if (!byIndex.TryGetValue(dayIndex, out var input) || input.Closed)
                {
                    day.Closed = true;
                    schedule.Days.Add(day);
                    continue;
                }

                var ranges = new List<BusinessScheduleRange>();
                var first = ParseRange(input.Open1, input.Close1, dayIndex, "1");
                if (first is not null)
                {
                    ranges.Add(first);
                }

                var second = ParseRange(input.Open2, input.Close2, dayIndex, "2");
                if (second is not null)
                {
                    if (first is null)
                    {
                        throw new TenantPublicPageValidationException(
                            $"En {DayNames[dayIndex]}, completa el primer turno antes del segundo.",
                            nameof(EditTenantPublicPageViewModel.ScheduleDays));
                    }

                    if (ToTime(second.Open) < ToTime(first.Close))
                    {
                        throw new TenantPublicPageValidationException(
                            $"En {DayNames[dayIndex]}, el segundo turno debe empezar después de que cierra el primero.",
                            nameof(EditTenantPublicPageViewModel.ScheduleDays));
                    }

                    ranges.Add(second);
                }

                if (ranges.Count == 0)
                {
                    day.Closed = true;
                }
                else
                {
                    day.Closed = false;
                    day.Ranges = ranges;
                }

                schedule.Days.Add(day);
            }

            return schedule.HasAnyOpenDay ? schedule : null;
        }

        public string? Serialize(BusinessSchedule? schedule)
        {
            if (schedule is null || !schedule.HasAnyOpenDay)
            {
                return null;
            }

            return JsonSerializer.Serialize(schedule, JsonOptions);
        }

        public IReadOnlyList<BusinessScheduleDayInput> BuildInputs(BusinessSchedule? schedule)
        {
            var result = new List<BusinessScheduleDayInput>(DayCount);
            for (var dayIndex = 0; dayIndex < DayCount; dayIndex++)
            {
                var day = schedule?.Days is not null && dayIndex < schedule.Days.Count
                    ? schedule.Days[dayIndex]
                    : null;

                var input = new BusinessScheduleDayInput
                {
                    DayIndex = dayIndex,
                    Closed = day is null || day.Closed || day.Ranges.Count == 0
                };

                if (day is { Closed: false })
                {
                    if (day.Ranges.Count > 0)
                    {
                        input.Open1 = day.Ranges[0].Open;
                        input.Close1 = day.Ranges[0].Close;
                    }

                    if (day.Ranges.Count > 1)
                    {
                        input.Open2 = day.Ranges[1].Open;
                        input.Close2 = day.Ranges[1].Close;
                    }
                }

                result.Add(input);
            }

            return result;
        }

        public BusinessScheduleStatusViewModel BuildStatus(BusinessSchedule? schedule, DateTime businessLocalNow)
        {
            if (schedule?.Days is null || !schedule.HasAnyOpenDay)
            {
                return new BusinessScheduleStatusViewModel { HasSchedule = false };
            }

            var todayIndex = ToDayIndex(businessLocalNow.DayOfWeek);
            var nowTime = TimeOnly.FromDateTime(businessLocalNow);

            var days = BuildDayRows(schedule, todayIndex);

            // 1) ¿Abierto ahora? Buscamos el tramo de hoy que contiene la hora actual.
            var openRange = GetDay(schedule, todayIndex).Ranges
                .Select(range => (Open: ToTime(range.Open), Close: ToTime(range.Close)))
                .Where(range => range.Open <= nowTime && nowTime < range.Close)
                .Select(range => (TimeOnly?)range.Close)
                .FirstOrDefault();

            if (openRange is not null)
            {
                return new BusinessScheduleStatusViewModel
                {
                    HasSchedule = true,
                    IsOpenNow = true,
                    StatusLabel = "Abierto ahora",
                    StatusDetail = $"Cierra a las {FormatTime(openRange.Value)}",
                    Days = days
                };
            }

            // 2) Cerrado: ¿abre mas tarde hoy?
            var laterToday = GetDay(schedule, todayIndex).Ranges
                .Select(range => ToTime(range.Open))
                .Where(open => open > nowTime)
                .OrderBy(open => open)
                .Select(open => (TimeOnly?)open)
                .FirstOrDefault();

            if (laterToday is not null)
            {
                return new BusinessScheduleStatusViewModel
                {
                    HasSchedule = true,
                    IsOpenNow = false,
                    StatusLabel = "Cerrado",
                    StatusDetail = $"Abre hoy a las {FormatTime(laterToday.Value)}",
                    Days = days
                };
            }

            // 3) Cerrado el resto de hoy: buscar el proximo dia con horario.
            for (var offset = 1; offset <= DayCount; offset++)
            {
                var nextIndex = (todayIndex + offset) % DayCount;
                var nextOpen = GetDay(schedule, nextIndex).Ranges
                    .Select(range => ToTime(range.Open))
                    .OrderBy(open => open)
                    .Select(open => (TimeOnly?)open)
                    .FirstOrDefault();

                if (nextOpen is null)
                {
                    continue;
                }

                var whenLabel = offset == 1 ? "mañana" : DayNames[nextIndex].ToLowerInvariant();
                return new BusinessScheduleStatusViewModel
                {
                    HasSchedule = true,
                    IsOpenNow = false,
                    StatusLabel = "Cerrado",
                    StatusDetail = $"Abre {whenLabel} a las {FormatTime(nextOpen.Value)}",
                    Days = days
                };
            }

            return new BusinessScheduleStatusViewModel
            {
                HasSchedule = true,
                IsOpenNow = false,
                StatusLabel = "Cerrado",
                Days = days
            };
        }

        private static IReadOnlyList<BusinessScheduleDayRowViewModel> BuildDayRows(
            BusinessSchedule schedule,
            int todayIndex)
        {
            var rows = new List<BusinessScheduleDayRowViewModel>(DayCount);
            for (var dayIndex = 0; dayIndex < DayCount; dayIndex++)
            {
                var day = GetDay(schedule, dayIndex);
                var isOpen = !day.Closed && day.Ranges.Count > 0;
                var hoursText = isOpen
                    ? string.Join(", ", day.Ranges.Select(range =>
                        $"{FormatTime(ToTime(range.Open))} – {FormatTime(ToTime(range.Close))}"))
                    : "Cerrado";

                rows.Add(new BusinessScheduleDayRowViewModel
                {
                    DayName = DayNames[dayIndex],
                    IsToday = dayIndex == todayIndex,
                    Closed = !isOpen,
                    HoursText = hoursText
                });
            }

            return rows;
        }

        private static BusinessScheduleDay GetDay(BusinessSchedule schedule, int dayIndex) =>
            dayIndex < schedule.Days.Count ? schedule.Days[dayIndex] : new BusinessScheduleDay { Closed = true };

        private static BusinessScheduleDay SanitizeDay(BusinessScheduleDay source)
        {
            if (source.Closed || source.Ranges is null)
            {
                return new BusinessScheduleDay { Closed = true };
            }

            var ranges = source.Ranges
                .Where(range => TryToTime(range.Open, out _) && TryToTime(range.Close, out _))
                .Where(range => ToTime(range.Open) < ToTime(range.Close))
                .OrderBy(range => ToTime(range.Open))
                .Take(2)
                .Select(range => new BusinessScheduleRange
                {
                    Open = Normalize(range.Open),
                    Close = Normalize(range.Close)
                })
                .ToList();

            return ranges.Count == 0
                ? new BusinessScheduleDay { Closed = true }
                : new BusinessScheduleDay { Closed = false, Ranges = ranges };
        }

        private BusinessScheduleRange? ParseRange(string? open, string? close, int dayIndex, string slot)
        {
            var hasOpen = !string.IsNullOrWhiteSpace(open);
            var hasClose = !string.IsNullOrWhiteSpace(close);

            if (!hasOpen && !hasClose)
            {
                return null;
            }

            if (hasOpen != hasClose)
            {
                throw new TenantPublicPageValidationException(
                    $"En {DayNames[dayIndex]}, completa la hora de apertura y de cierre del turno {slot}.",
                    nameof(EditTenantPublicPageViewModel.ScheduleDays));
            }

            if (!TryToTime(open, out var openTime) || !TryToTime(close, out var closeTime))
            {
                throw new TenantPublicPageValidationException(
                    $"En {DayNames[dayIndex]}, la hora del turno {slot} no es válida.",
                    nameof(EditTenantPublicPageViewModel.ScheduleDays));
            }

            if (closeTime <= openTime)
            {
                throw new TenantPublicPageValidationException(
                    $"En {DayNames[dayIndex]}, la hora de cierre del turno {slot} debe ser mayor que la de apertura.",
                    nameof(EditTenantPublicPageViewModel.ScheduleDays));
            }

            return new BusinessScheduleRange
            {
                Open = Normalize(open!),
                Close = Normalize(close!)
            };
        }

        private static int ToDayIndex(DayOfWeek dayOfWeek) => ((int)dayOfWeek + 6) % DayCount;

        private static TimeOnly ToTime(string value) =>
            TryToTime(value, out var time) ? time : TimeOnly.MinValue;

        private static bool TryToTime(string? value, out TimeOnly time) =>
            TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

        private static string Normalize(string value) =>
            ToTime(value).ToString("HH:mm", CultureInfo.InvariantCulture);

        private static string FormatTime(TimeOnly time)
        {
            var hour12 = time.Hour % 12;
            if (hour12 == 0)
            {
                hour12 = 12;
            }

            var suffix = time.Hour < 12 ? "a. m." : "p. m.";
            return $"{hour12}:{time.Minute:D2} {suffix}";
        }
    }
}
