using System;
using System.Collections.Generic;
using System.Linq;
using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.PublicPages;
using Xunit;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class BusinessScheduleServiceTests
    {
        private static readonly BusinessScheduleService Service = new();

        private static BusinessSchedule AllOpen(params (string open, string close)[] ranges)
        {
            var schedule = new BusinessSchedule();
            for (var i = 0; i < 7; i++)
            {
                schedule.Days.Add(new BusinessScheduleDay
                {
                    Closed = false,
                    Ranges = ranges
                        .Select(r => new BusinessScheduleRange { Open = r.open, Close = r.close })
                        .ToList()
                });
            }

            return schedule;
        }

        [Fact]
        public void BuildStatus_OpenNow_SingleShift_ReportsClosingTime()
        {
            var now = new DateTime(2026, 5, 27, 10, 30, 0);

            var status = Service.BuildStatus(AllOpen(("09:00", "18:00")), now);

            Assert.True(status.HasSchedule);
            Assert.True(status.IsOpenNow);
            Assert.Equal("Abierto ahora", status.StatusLabel);
            Assert.Contains("6:00 p. m.", status.StatusDetail);
        }

        [Fact]
        public void BuildStatus_DoubleShift_ClosedAtLunch_OpensLaterToday()
        {
            var now = new DateTime(2026, 5, 27, 13, 0, 0);

            var status = Service.BuildStatus(AllOpen(("08:00", "12:00"), ("14:00", "18:00")), now);

            Assert.False(status.IsOpenNow);
            Assert.Equal("Cerrado", status.StatusLabel);
            Assert.Equal("Abre hoy a las 2:00 p. m.", status.StatusDetail);
        }

        [Fact]
        public void BuildStatus_AfterClose_OpensTomorrow()
        {
            var now = new DateTime(2026, 5, 27, 20, 0, 0);

            var status = Service.BuildStatus(AllOpen(("08:00", "18:00")), now);

            Assert.False(status.IsOpenNow);
            Assert.Equal("Abre mañana a las 8:00 a. m.", status.StatusDetail);
        }

        [Fact]
        public void BuildStatus_TodayClosed_ReportsNextOpenDay_AndMarksToday()
        {
            var now = new DateTime(2026, 5, 27, 10, 0, 0);
            var todayIndex = ((int)now.DayOfWeek + 6) % 7;

            var schedule = new BusinessSchedule();
            for (var i = 0; i < 7; i++)
            {
                schedule.Days.Add(i == todayIndex
                    ? new BusinessScheduleDay { Closed = true }
                    : new BusinessScheduleDay
                    {
                        Closed = false,
                        Ranges = { new BusinessScheduleRange { Open = "09:00", Close = "17:00" } }
                    });
            }

            var status = Service.BuildStatus(schedule, now);

            Assert.False(status.IsOpenNow);
            Assert.Equal("Abre mañana a las 9:00 a. m.", status.StatusDetail);
            Assert.True(status.Days[todayIndex].Closed);
            Assert.True(status.Days[todayIndex].IsToday);
        }

        [Fact]
        public void BuildStatus_NoOpenDays_HasNoSchedule()
        {
            var schedule = new BusinessSchedule();
            for (var i = 0; i < 7; i++)
            {
                schedule.Days.Add(new BusinessScheduleDay { Closed = true });
            }

            var status = Service.BuildStatus(schedule, new DateTime(2026, 5, 27, 10, 0, 0));

            Assert.False(status.HasSchedule);
        }

        [Fact]
        public void BuildStatus_NullSchedule_HasNoSchedule()
        {
            var status = Service.BuildStatus(null, new DateTime(2026, 5, 27, 10, 0, 0));

            Assert.False(status.HasSchedule);
        }

        [Fact]
        public void BuildFromInputs_Serialize_Deserialize_RoundTrips()
        {
            var inputs = Enumerable.Range(0, 7)
                .Select(i => new BusinessScheduleDayInput
                {
                    DayIndex = i,
                    Closed = i == 6,
                    Open1 = "08:00",
                    Close1 = "12:00",
                    Open2 = "14:00",
                    Close2 = "18:00"
                })
                .ToList();

            var schedule = Service.BuildFromInputs(inputs);
            Assert.NotNull(schedule);

            var json = Service.Serialize(schedule);
            Assert.NotNull(json);

            var round = Service.TryDeserialize(json);
            Assert.NotNull(round);
            Assert.True(round!.Days[6].Closed);
            Assert.Equal(2, round.Days[0].Ranges.Count);

            var inputsBack = Service.BuildInputs(round);
            Assert.Equal(7, inputsBack.Count);
            Assert.Equal("08:00", inputsBack[0].Open1);
            Assert.Equal("18:00", inputsBack[0].Close2);
            Assert.True(inputsBack[6].Closed);
        }

        [Fact]
        public void BuildFromInputs_AllClosed_ReturnsNull()
        {
            var inputs = Enumerable.Range(0, 7)
                .Select(i => new BusinessScheduleDayInput { DayIndex = i, Closed = true })
                .ToList();

            Assert.Null(Service.BuildFromInputs(inputs));
        }

        [Fact]
        public void BuildFromInputs_CloseBeforeOpen_Throws()
        {
            var inputs = new List<BusinessScheduleDayInput>
            {
                new() { DayIndex = 0, Closed = false, Open1 = "18:00", Close1 = "09:00" }
            };

            Assert.Throws<TenantPublicPageValidationException>(() => Service.BuildFromInputs(inputs));
        }

        [Fact]
        public void BuildFromInputs_OverlappingSecondShift_Throws()
        {
            var inputs = new List<BusinessScheduleDayInput>
            {
                new()
                {
                    DayIndex = 0,
                    Closed = false,
                    Open1 = "08:00",
                    Close1 = "14:00",
                    Open2 = "12:00",
                    Close2 = "18:00"
                }
            };

            Assert.Throws<TenantPublicPageValidationException>(() => Service.BuildFromInputs(inputs));
        }

        [Fact]
        public void BuildFromInputs_IncompleteRange_Throws()
        {
            var inputs = new List<BusinessScheduleDayInput>
            {
                new() { DayIndex = 0, Closed = false, Open1 = "08:00", Close1 = null }
            };

            Assert.Throws<TenantPublicPageValidationException>(() => Service.BuildFromInputs(inputs));
        }

        [Fact]
        public void TryDeserialize_InvalidJson_ReturnsNull()
        {
            Assert.Null(Service.TryDeserialize("not-json"));
            Assert.Null(Service.TryDeserialize(null));
            Assert.Null(Service.TryDeserialize(""));
        }
    }
}
