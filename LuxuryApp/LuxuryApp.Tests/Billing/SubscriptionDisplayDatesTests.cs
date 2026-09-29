using LuxuryApp.Services.Billing;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Fecha VISIBLE de la suscripción. El bug real: el expire "2026-09-15" de TiloPay se guarda
    /// como fin de día Tico en UTC (2026-09-16 05:59 UTC) y la UI mostraba "16/09" — un día de más.
    /// La fecha de calendario Tica correcta es 15/09/2026.
    /// </summary>
    public class SubscriptionDisplayDatesTests
    {
        // 2026-09-15 fin de día Costa Rica en UTC (lo que guarda ProviderExpiresAtUtc).
        private static readonly DateTime ProviderExpiresUtc = new(2026, 9, 16, 5, 59, 59, DateTimeKind.Utc);

        // ── Escenario: provider gana, raw crudo => fecha Tica exacta ──

        [Fact]
        public void Effective_ProviderWins_UsesRawDateOnly_NotUtcNextDay()
        {
            var local = new DateTime(2026, 8, 15, 22, 3, 57, DateTimeKind.Utc);

            var display = SubscriptionDisplayDates.FormatEffective(local, ProviderExpiresUtc, "2026-09-15");

            Assert.Equal("15/09/2026", display); // NO 16/09
        }

        [Fact]
        public void Effective_ProviderWins_AfterExtension_LocalEqualsProvider_StillUsesRaw()
        {
            // Tras la sincronización, FechaFin quedó igual al expire del proveedor (2026-09-16 UTC).
            var display = SubscriptionDisplayDates.FormatEffective(ProviderExpiresUtc, ProviderExpiresUtc, "2026-09-15");

            Assert.Equal("15/09/2026", display);
        }

        [Fact]
        public void Effective_ProviderWinsButNoRaw_ConvertsUtcToCostaRicaDate()
        {
            var local = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);

            // Sin raw: 2026-09-16 05:59 UTC → 2026-09-15 23:59 Tica → 15/09.
            var display = SubscriptionDisplayDates.FormatEffective(local, ProviderExpiresUtc, providerExpiryRaw: null);

            Assert.Equal("15/09/2026", display);
        }

        // ── Sin provider: comportamiento local normal (como antes) ──

        [Fact]
        public void Effective_NoProvider_ShowsLocalDateAsStored()
        {
            var local = new DateTime(2026, 8, 16, 10, 0, 0, DateTimeKind.Utc);

            var display = SubscriptionDisplayDates.FormatEffective(local, providerExpiresAtUtc: null, providerExpiryRaw: null);

            Assert.Equal("16/08/2026", display); // sin conversión, igual que .ToString("dd/MM/yyyy")
        }

        [Fact]
        public void Effective_ProviderEarlierThanLocal_LocalWins_ShowsLocal()
        {
            var local = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
            var providerEarlier = new DateTime(2026, 9, 16, 5, 59, 59, DateTimeKind.Utc);

            var display = SubscriptionDisplayDates.FormatEffective(local, providerEarlier, "2026-09-15");

            // Gana lo local (nunca acortamos): se muestra la fecha local.
            Assert.Equal("20/10/2026", display);
        }

        [Fact]
        public void Effective_NoDates_ReturnsNull()
        {
            Assert.Null(SubscriptionDisplayDates.FormatEffective(null, null, null));
        }

        // ── Conversión UTC → fecha Tica ──

        [Fact]
        public void ToCostaRicaDate_LateUtcEvening_IsSameLocalDay()
        {
            // 2026-09-16 05:59 UTC = 2026-09-15 23:59 Tica.
            Assert.Equal(new DateOnly(2026, 9, 15), SubscriptionDisplayDates.ToCostaRicaDate(ProviderExpiresUtc));
        }

        [Fact]
        public void ToCostaRicaDate_EarlyUtcMorning_IsPreviousLocalDay()
        {
            // 2026-09-15 03:00 UTC = 2026-09-14 21:00 Tica → 14/09.
            var utc = new DateTime(2026, 9, 15, 3, 0, 0, DateTimeKind.Utc);
            Assert.Equal(new DateOnly(2026, 9, 14), SubscriptionDisplayDates.ToCostaRicaDate(utc));
        }

        [Fact]
        public void ToCostaRicaDate_Null_ReturnsNull()
        {
            Assert.Null(SubscriptionDisplayDates.ToCostaRicaDate(null));
        }

        [Theory]
        [InlineData("2026-09-15", "15/09/2026")]
        [InlineData("2026-12-31", "31/12/2026")]
        [InlineData("2027-01-01", "01/01/2027")]
        public void RawDate_IsShownExactly(string raw, string expected)
        {
            var utc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc); // provider gana holgado
            var local = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.Equal(expected, SubscriptionDisplayDates.FormatEffective(local, utc, raw));
        }
    }
}
