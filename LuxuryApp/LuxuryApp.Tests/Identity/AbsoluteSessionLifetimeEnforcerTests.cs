using System.Globalization;
using LuxuryApp.Services.Identity;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.Identity
{
    public class AbsoluteSessionLifetimeEnforcerTests
    {
        private static readonly DateTimeOffset Now =
            new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Evaluate_WithoutMarker_ShouldNeedInitialization()
        {
            var enforcer = CreateEnforcer(Now);

            var decision = enforcer.Evaluate(new Dictionary<string, string?>());

            Assert.Equal(AbsoluteSessionLifetimeEnforcer.Decision.NeedsInitialization, decision);
        }

        [Fact]
        public void Evaluate_WithEmptyMarker_ShouldNeedInitialization()
        {
            var enforcer = CreateEnforcer(Now);

            var decision = enforcer.Evaluate(new Dictionary<string, string?>
            {
                [AbsoluteSessionLifetimeEnforcer.SessionStartedItemKey] = "   "
            });

            Assert.Equal(AbsoluteSessionLifetimeEnforcer.Decision.NeedsInitialization, decision);
        }

        [Fact]
        public void Evaluate_SessionYoungerThan90Days_ShouldStayWithinLimit()
        {
            var enforcer = CreateEnforcer(Now);
            var started = Now.AddDays(-89).AddHours(-23);

            var decision = enforcer.Evaluate(MarkerItems(started));

            Assert.Equal(AbsoluteSessionLifetimeEnforcer.Decision.WithinLimit, decision);
        }

        [Fact]
        public void Evaluate_SessionOlderThan90Days_ShouldExpire()
        {
            var enforcer = CreateEnforcer(Now);
            var started = Now.AddDays(-90).AddMinutes(-1);

            var decision = enforcer.Evaluate(MarkerItems(started));

            Assert.Equal(AbsoluteSessionLifetimeEnforcer.Decision.Expired, decision);
        }

        [Fact]
        public void Evaluate_ShouldUseUtcRegardlessOfMarkerOffset()
        {
            var enforcer = CreateEnforcer(Now);
            // Marca con offset -06:00 (Costa Rica) equivalente a 88 días atrás en UTC.
            var startedLocal = Now.ToOffset(TimeSpan.FromHours(-6)).AddDays(-88);

            var decision = enforcer.Evaluate(MarkerItems(startedLocal));

            Assert.Equal(AbsoluteSessionLifetimeEnforcer.Decision.WithinLimit, decision);
        }

        [Fact]
        public void Evaluate_WithCorruptMarker_ShouldExpire()
        {
            var enforcer = CreateEnforcer(Now);

            var decision = enforcer.Evaluate(new Dictionary<string, string?>
            {
                [AbsoluteSessionLifetimeEnforcer.SessionStartedItemKey] = "not-a-date"
            });

            Assert.Equal(AbsoluteSessionLifetimeEnforcer.Decision.Expired, decision);
        }

        [Fact]
        public void Evaluate_WithFutureDatedMarker_ShouldExpire()
        {
            var enforcer = CreateEnforcer(Now);
            var started = Now.AddHours(1);

            var decision = enforcer.Evaluate(MarkerItems(started));

            Assert.Equal(AbsoluteSessionLifetimeEnforcer.Decision.Expired, decision);
        }

        [Fact]
        public void Evaluate_SlidingRenewalDoesNotResetOriginalStart()
        {
            var clock = new FixedTimeProvider(Now);
            var enforcer = new AbsoluteSessionLifetimeEnforcer(clock);
            var started = Now.AddDays(-89);
            var items = MarkerItems(started);

            // Aún válida hoy.
            Assert.Equal(AbsoluteSessionLifetimeEnforcer.Decision.WithinLimit, enforcer.Evaluate(items));

            // El usuario sigue activo dos días más: la MISMA marca original supera los 90 días.
            clock.Advance(TimeSpan.FromDays(2));
            Assert.Equal(AbsoluteSessionLifetimeEnforcer.Decision.Expired, enforcer.Evaluate(items));
        }

        [Fact]
        public void CreateStartMarker_ShouldRoundTripAsWithinLimit()
        {
            var enforcer = CreateEnforcer(Now);

            var marker = enforcer.CreateStartMarker();

            var decision = enforcer.Evaluate(new Dictionary<string, string?>
            {
                [AbsoluteSessionLifetimeEnforcer.SessionStartedItemKey] = marker
            });

            Assert.Equal(AbsoluteSessionLifetimeEnforcer.Decision.WithinLimit, decision);
        }

        private static AbsoluteSessionLifetimeEnforcer CreateEnforcer(DateTimeOffset now) =>
            new(new FixedTimeProvider(now));

        private static Dictionary<string, string?> MarkerItems(DateTimeOffset started) =>
            new()
            {
                [AbsoluteSessionLifetimeEnforcer.SessionStartedItemKey] =
                    started.ToString("O", CultureInfo.InvariantCulture)
            };
    }
}
