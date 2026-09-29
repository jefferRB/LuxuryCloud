using LuxuryApp.Services.Tilopay;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// La regla que decide si un suscriptor de TiloPay cobra o no. Estaba duplicada en tres
    /// lugares y ninguno reconocía "Delete" (singular), el valor real del proveedor: por eso un
    /// suscriptor ya eliminado bloqueó un cambio de plan legítimo en producción.
    ///
    /// La asimetría es deliberada: para ACTIVO y para INACTIVO hace falta evidencia explícita.
    /// Lo que no reconocemos es Unknown y se manda a revisión manual, nunca se asume libre.
    /// </summary>
    public class ProviderSubscriberStatusRulesTests
    {
        [Theory]
        [InlineData("1")]
        [InlineData("active")]
        [InlineData("Active")]
        [InlineData("ACTIVE")]
        [InlineData("activo")]
        [InlineData("Activa")]
        [InlineData("  Active  ")]
        public void Classify_ActiveValues_AreActive(string status)
        {
            Assert.Equal(ProviderSubscriberState.Active, ProviderSubscriberStatusRules.Classify(status));
            Assert.True(ProviderSubscriberStatusRules.IsProviderSubscriberActive(status));
            Assert.True(ProviderSubscriberStatusRules.MayStillCharge(status));
        }

        [Theory]
        [InlineData("Delete")]     // ← el valor que devuelve el proveedor y que antes no se reconocía
        [InlineData("delete")]
        [InlineData("Deleted")]
        [InlineData("deleted")]
        [InlineData("Eliminado")]
        [InlineData("eliminada")]
        [InlineData("removed")]
        [InlineData("Cancelled")]
        [InlineData("Canceled")]
        [InlineData("cancelado")]
        [InlineData("Inactive")]
        [InlineData("inactivo")]
        [InlineData("4")]
        public void Classify_InactiveValues_AreInactiveAndCannotCharge(string status)
        {
            Assert.Equal(ProviderSubscriberState.Inactive, ProviderSubscriberStatusRules.Classify(status));
            Assert.True(ProviderSubscriberStatusRules.IsProviderSubscriberInactive(status));
            Assert.False(ProviderSubscriberStatusRules.IsProviderSubscriberActive(status));
            Assert.False(ProviderSubscriberStatusRules.MayStillCharge(status));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("pending")]
        [InlineData("lo-que-sea")]
        public void Classify_UnknownValues_AreUnknownAndTreatedAsStillCharging(string? status)
        {
            Assert.Equal(ProviderSubscriberState.Unknown, ProviderSubscriberStatusRules.Classify(status));
            Assert.False(ProviderSubscriberStatusRules.IsProviderSubscriberActive(status));
            Assert.False(ProviderSubscriberStatusRules.IsProviderSubscriberInactive(status));

            // Clave: sin prueba de que NO cobra, se asume que puede cobrar. Fail-closed.
            Assert.True(ProviderSubscriberStatusRules.MayStillCharge(status));
        }

        [Theory]
        [InlineData("3")]           // ← status confirmado por soporte TiloPay = Pausado
        [InlineData("Paused")]
        [InlineData("paused")]
        [InlineData("pausado")]
        [InlineData("Pausada")]
        [InlineData("  pause  ")]
        [InlineData("Pause By Commerce")]    // ← valor observado en la API del proveedor
        [InlineData("pause by commerce")]
        [InlineData("Paused By Commerce")]
        [InlineData("PauseByCommerce")]
        [InlineData("PausedByCommerce")]
        [InlineData("  Pause By Commerce  ")]
        public void Classify_PausedValues_ArePausedAndMayStillCharge(string status)
        {
            Assert.Equal(ProviderSubscriberState.Paused, ProviderSubscriberStatusRules.Classify(status));
            Assert.True(ProviderSubscriberStatusRules.IsProviderSubscriberPaused(status));
            Assert.False(ProviderSubscriberStatusRules.IsProviderSubscriberActive(status));
            Assert.False(ProviderSubscriberStatusRules.IsProviderSubscriberInactive(status));

            // Un pausado puede volver a cobrar al reactivarse: nunca cuenta como baja verificada.
            Assert.True(ProviderSubscriberStatusRules.MayStillCharge(status));
        }

        [Fact]
        public void Sanitize_KnownStatus_IsEmittedAsIs()
        {
            Assert.Equal("Delete", ProviderSubscriberStatusRules.Sanitize("Delete"));
            Assert.Equal("Active", ProviderSubscriberStatusRules.Sanitize("Active"));
        }

        [Fact]
        public void Sanitize_UnknownStatus_IsMarkedAndTruncated()
        {
            var sanitized = ProviderSubscriberStatusRules.Sanitize(new string('x', 80));

            Assert.StartsWith("(desconocido:", sanitized);
            Assert.True(sanitized.Length < 40);
        }

        [Fact]
        public void Sanitize_NullStatus_IsReadable()
        {
            Assert.Equal("(sin status)", ProviderSubscriberStatusRules.Sanitize(null));
        }

        // ── Tabla de decisión del plan destino ──

        [Fact]
        public void FromMatches_NoSubscribers_IsFree()
        {
            var assessment = TargetSubscriberAssessment.FromMatches(Array.Empty<TilopaySubscriber>(), 7102);

            Assert.Equal(TargetSubscriberVerdict.Free, assessment.Verdict);
        }

        [Fact]
        public void FromMatches_OnlyInactive_IsFreeButKeepsThemVisible()
        {
            var assessment = TargetSubscriberAssessment.FromMatches(
                new[]
                {
                    new TilopaySubscriber { SubscriberId = "900117", Status = "Delete" },
                    new TilopaySubscriber { SubscriberId = "380001", Status = "4" }
                },
                7102);

            Assert.Equal(TargetSubscriberVerdict.Free, assessment.Verdict);
            Assert.Equal(2, assessment.Inactive.Count);
            Assert.Empty(assessment.Active);
        }

        [Fact]
        public void FromMatches_SingleActive_IsSingleActive()
        {
            var assessment = TargetSubscriberAssessment.FromMatches(
                new[] { new TilopaySubscriber { SubscriberId = "386130", Status = "Active" } },
                7103);

            Assert.Equal(TargetSubscriberVerdict.SingleActive, assessment.Verdict);
        }

        [Fact]
        public void FromMatches_ActiveAndInactiveTogether_CountsOnlyTheActiveOne()
        {
            var assessment = TargetSubscriberAssessment.FromMatches(
                new[]
                {
                    new TilopaySubscriber { SubscriberId = "900117", Status = "Delete" },
                    new TilopaySubscriber { SubscriberId = "386130", Status = "Active" }
                },
                7103);

            Assert.Equal(TargetSubscriberVerdict.SingleActive, assessment.Verdict);
            Assert.Single(assessment.Active);
            Assert.Single(assessment.Inactive);
        }

        [Fact]
        public void FromMatches_MultipleActive_IsMultipleActive()
        {
            var assessment = TargetSubscriberAssessment.FromMatches(
                new[]
                {
                    new TilopaySubscriber { SubscriberId = "386130", Status = "Active" },
                    new TilopaySubscriber { SubscriberId = "386131", Status = "1" }
                },
                7103);

            Assert.Equal(TargetSubscriberVerdict.MultipleActive, assessment.Verdict);
        }

        [Fact]
        public void FromMatches_UnknownStatus_WinsOverEveryOtherVerdict()
        {
            // Con una fila que no entendemos no sabemos cuántos cobran: nunca "libre", nunca
            // "un solo activo". Decide soporte.
            var assessment = TargetSubscriberAssessment.FromMatches(
                new[]
                {
                    new TilopaySubscriber { SubscriberId = "900117", Status = "Delete" },
                    new TilopaySubscriber { SubscriberId = "386130", Status = "Active" },
                    new TilopaySubscriber { SubscriberId = "386140", Status = "Paused" }
                },
                7103);

            Assert.Equal(TargetSubscriberVerdict.UnknownStatus, assessment.Verdict);
            Assert.Single(assessment.Unknown);
        }

        [Fact]
        public void FromMatches_PausedTargetSubscriber_BlocksAsUnknownStatus()
        {
            // Un suscriptor PAUSADO en el plan destino no deja el plan libre: puede volver a cobrar
            // al reactivarse. Debe bloquear igual que un status desconocido (nunca Free), o pagar
            // crearía un segundo suscriptor sobre uno que puede resucitar. Regresión money-critical.
            var assessment = TargetSubscriberAssessment.FromMatches(
                new[]
                {
                    new TilopaySubscriber { SubscriberId = "386140", Status = "3" }
                },
                7103);

            Assert.Equal(TargetSubscriberVerdict.UnknownStatus, assessment.Verdict);
            Assert.Single(assessment.Unknown);
            Assert.Empty(assessment.Active);
            Assert.Empty(assessment.Inactive);
        }
    }
}
