using LuxuryApp.Services.Billing;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Parseo del expire date-only de TiloPay y regla de fecha efectiva. Son la base de todo el
    /// blindaje de fechas: si el parseo corta 6h antes, suspenderíamos clientes de madrugada; si el
    /// máximo se equivoca, marcaríamos moroso a alguien que TiloPay aún no va a cobrar.
    /// </summary>
    public class ProviderExpiryDateTests
    {
        // ── Parseo: date-only como fin del día Costa Rica (UTC-6) ──

        [Fact]
        public void Parse_DateOnly_IsEndOfCostaRicaDayInUtc()
        {
            var utc = ProviderExpiryDate.ParseCostaRicaEndOfDayUtc("2026-09-15");

            // Fin del 15/09 Tica (23:59:59.999 UTC-6) = 16/09 05:59:59.999 UTC.
            Assert.NotNull(utc);
            Assert.Equal(new DateTime(2026, 9, 16, 5, 59, 59, 999, DateTimeKind.Utc), utc!.Value, TimeSpan.FromMilliseconds(2));
            Assert.Equal(DateTimeKind.Utc, utc.Value.Kind);
        }

        [Fact]
        public void Parse_DoesNotSuspendEarly_ExpireDayIsStillCoveredAtLocalEvening()
        {
            var utc = ProviderExpiryDate.ParseCostaRicaEndOfDayUtc("2026-09-15")!.Value;

            // Las 8pm Tica del 15/09 (02:00 UTC del 16) todavía están cubiertas.
            var eveningOfExpireDayUtc = new DateTime(2026, 9, 16, 2, 0, 0, DateTimeKind.Utc);
            Assert.True(utc > eveningOfExpireDayUtc);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("no-es-fecha")]
        [InlineData("0000-00-00")]
        public void Parse_Garbage_ReturnsNullWithoutThrowing(string? raw)
        {
            Assert.Null(ProviderExpiryDate.ParseCostaRicaEndOfDayUtc(raw));
        }

        [Fact]
        public void Parse_WithTime_IsTreatedAsCostaRicaLocal()
        {
            var utc = ProviderExpiryDate.ParseCostaRicaEndOfDayUtc("2026-09-15 10:30:00");

            // 10:30 Tica = 16:30 UTC.
            Assert.Equal(new DateTime(2026, 9, 15, 16, 30, 0, DateTimeKind.Utc), utc);
        }

        // ── Fecha efectiva = max(local, provider) ──

        [Fact]
        public void Effective_ProviderLater_UsesProvider()
        {
            var local = new DateTime(2026, 8, 15, 22, 3, 57, DateTimeKind.Utc);
            var provider = new DateTime(2026, 9, 16, 5, 59, 59, DateTimeKind.Utc);

            Assert.Equal(provider, SubscriptionEffectiveDates.GetEffectiveEndUtc(local, provider));
        }

        [Fact]
        public void Effective_ProviderEarlier_KeepsLocal_NeverShortens()
        {
            var local = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
            var provider = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);

            // El máximo se queda con lo local: el proveedor por detrás nunca acorta.
            Assert.Equal(local, SubscriptionEffectiveDates.GetEffectiveEndUtc(local, provider));
        }

        [Fact]
        public void Effective_NoProvider_UsesLocal()
        {
            var local = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(local, SubscriptionEffectiveDates.GetEffectiveEndUtc(local, null));
        }

        [Fact]
        public void Effective_NoLocal_UsesProvider()
        {
            var provider = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(provider, SubscriptionEffectiveDates.GetEffectiveEndUtc(null, provider));
        }

        [Fact]
        public void Effective_Equal_ReturnsThatDate()
        {
            var d = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(d, SubscriptionEffectiveDates.GetEffectiveEndUtc(d, d));
        }

        // ── Dirección del desajuste (con tolerancia) ──

        [Fact]
        public void ProviderIsAhead_OnlyBeyondTolerance()
        {
            var local = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);
            var tolerance = TimeSpan.FromHours(12);

            Assert.True(SubscriptionEffectiveDates.ProviderIsAhead(local, local.AddDays(30), tolerance));
            Assert.False(SubscriptionEffectiveDates.ProviderIsAhead(local, local.AddHours(6), tolerance)); // dentro de tolerancia
            Assert.False(SubscriptionEffectiveDates.ProviderIsAhead(local, local.AddDays(-30), tolerance)); // atrás
        }

        [Fact]
        public void ProviderIsEarlier_OnlyBeyondTolerance()
        {
            var local = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
            var tolerance = TimeSpan.FromHours(12);

            Assert.True(SubscriptionEffectiveDates.ProviderIsEarlier(local, local.AddDays(-30), tolerance));
            Assert.False(SubscriptionEffectiveDates.ProviderIsEarlier(local, local.AddHours(-6), tolerance)); // dentro de tolerancia
            Assert.False(SubscriptionEffectiveDates.ProviderIsEarlier(local, local.AddDays(30), tolerance)); // adelante
        }

        [Fact]
        public void MismatchDirections_AreMutuallyExclusive()
        {
            var local = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);
            var provider = local.AddDays(31);
            var tolerance = TimeSpan.FromHours(12);

            Assert.True(SubscriptionEffectiveDates.ProviderIsAhead(local, provider, tolerance));
            Assert.False(SubscriptionEffectiveDates.ProviderIsEarlier(local, provider, tolerance));
        }
    }
}
