using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Inversionistas;

namespace LuxuryApp.Tests.Inversionistas
{
    /// <summary>
    /// Reglas puras de ganancia/pérdida y periodos. Sin base de datos: si estas fallan, el problema
    /// está en la fórmula, no en el acceso a datos.
    /// </summary>
    public class InvestorProfitRulesTests
    {
        // ─────────────── Ejemplo obligatorio del negocio ───────────────

        [Fact]
        public void Participacion_ConGanancia1Millon_Y45Porciento_Da450Mil()
        {
            var participacion = InvestorStatementService.CalcularParticipacion(1_000_000m, 45m);

            Assert.Equal(450_000m, participacion);
        }

        [Fact]
        public void Participacion_DosInversionistasQueSuman100_RepartenTodaLaGanancia()
        {
            var primera = InvestorStatementService.CalcularParticipacion(1_000_000m, 45m);
            var segunda = InvestorStatementService.CalcularParticipacion(1_000_000m, 55m);

            Assert.Equal(450_000m, primera);
            Assert.Equal(550_000m, segunda);
            Assert.Equal(1_000_000m, primera + segunda);
        }

        // ─────────────── Ganancia y pérdida ───────────────

        [Fact]
        public void ApplyProfitRules_ConAjustes_SumaYRestaAntesDeRepartir()
        {
            var (distribuible, aplicada, pendiente) = InvestorStatementService.ApplyProfitRules(
                resultadoOperativo: 500_000m,
                ajustesPositivos: 100_000m,
                ajustesNegativos: 50_000m,
                perdidaPrevia: 0m,
                InvestorLossTreatment.NoDistribution);

            Assert.Equal(550_000m, distribuible);
            Assert.Equal(0m, aplicada);
            Assert.Equal(0m, pendiente);
        }

        [Fact]
        public void ApplyProfitRules_GananciaNegativaSinArrastre_DejaCeroYNoPasaAlSiguientePeriodo()
        {
            var (distribuible, aplicada, pendiente) = InvestorStatementService.ApplyProfitRules(
                resultadoOperativo: -200_000m,
                ajustesPositivos: 0m,
                ajustesNegativos: 0m,
                perdidaPrevia: 0m,
                InvestorLossTreatment.NoDistribution);

            Assert.Equal(0m, distribuible);
            Assert.Equal(0m, aplicada);
            Assert.Equal(0m, pendiente);
        }

        [Fact]
        public void ApplyProfitRules_GananciaNegativaConArrastre_DejaLaPerdidaPendiente()
        {
            var (distribuible, aplicada, pendiente) = InvestorStatementService.ApplyProfitRules(
                resultadoOperativo: -200_000m,
                ajustesPositivos: 0m,
                ajustesNegativos: 0m,
                perdidaPrevia: 0m,
                InvestorLossTreatment.CarryForward);

            Assert.Equal(0m, distribuible);
            Assert.Equal(0m, aplicada);
            Assert.Equal(200_000m, pendiente);
        }

        [Fact]
        public void ApplyProfitRules_ConArrastre_RecuperaLaPerdidaEnUnPeriodoPosterior()
        {
            // Periodo con ganancia 500k y una pérdida previa de 200k: se reparte solo la diferencia.
            var (distribuible, aplicada, pendiente) = InvestorStatementService.ApplyProfitRules(
                resultadoOperativo: 500_000m,
                ajustesPositivos: 0m,
                ajustesNegativos: 0m,
                perdidaPrevia: 200_000m,
                InvestorLossTreatment.CarryForward);

            Assert.Equal(300_000m, distribuible);
            Assert.Equal(200_000m, aplicada);
            Assert.Equal(0m, pendiente);
        }

        [Fact]
        public void ApplyProfitRules_ConArrastre_GananciaMenorQueLaPerdida_DejaSaldoPendiente()
        {
            var (distribuible, aplicada, pendiente) = InvestorStatementService.ApplyProfitRules(
                resultadoOperativo: 120_000m,
                ajustesPositivos: 0m,
                ajustesNegativos: 0m,
                perdidaPrevia: 200_000m,
                InvestorLossTreatment.CarryForward);

            Assert.Equal(0m, distribuible);
            Assert.Equal(200_000m, aplicada);
            Assert.Equal(80_000m, pendiente);
        }

        [Fact]
        public void ApplyProfitRules_SinArrastre_IgnoraUnaPerdidaPreviaHeredada()
        {
            var (distribuible, aplicada, pendiente) = InvestorStatementService.ApplyProfitRules(
                resultadoOperativo: 500_000m,
                ajustesPositivos: 0m,
                ajustesNegativos: 0m,
                perdidaPrevia: 200_000m,
                InvestorLossTreatment.NoDistribution);

            Assert.Equal(500_000m, distribuible);
            Assert.Equal(0m, aplicada);
            Assert.Equal(0m, pendiente);
        }

        [Fact]
        public void CalcularParticipacion_UsaElRedondeoMonetarioDeLaAplicacion()
        {
            // 1.000,005 × 50 % = 500,0025 → half-even a 2 decimales = 500,00.
            Assert.Equal(500.00m, InvestorStatementService.CalcularParticipacion(1000.005m, 50m));
        }

        // ─────────────── Periodos ───────────────

        [Fact]
        public void Resolve_Mensual_TomaElMesCalendarioCompleto()
        {
            var periodo = InvestorPeriodCalculator.Resolve(
                InvestorPayoutFrequency.Mensual,
                new DateOnly(2026, 7, 15));

            Assert.Equal(new DateOnly(2026, 7, 1), periodo.Inicio);
            Assert.Equal(new DateOnly(2026, 7, 31), periodo.Fin);
        }

        [Fact]
        public void Resolve_Quincenal_UsaLaConvencionDeCostaRica()
        {
            var primera = InvestorPeriodCalculator.Resolve(
                InvestorPayoutFrequency.Quincenal,
                new DateOnly(2026, 7, 10));

            var segunda = InvestorPeriodCalculator.Resolve(
                InvestorPayoutFrequency.Quincenal,
                new DateOnly(2026, 7, 20));

            Assert.Equal(new DateOnly(2026, 7, 1), primera.Inicio);
            Assert.Equal(new DateOnly(2026, 7, 15), primera.Fin);
            Assert.Equal(new DateOnly(2026, 7, 16), segunda.Inicio);
            Assert.Equal(new DateOnly(2026, 7, 31), segunda.Fin);
        }

        [Fact]
        public void Resolve_Semanal_ArrancaElLunes()
        {
            // 2026-07-15 es miércoles.
            var periodo = InvestorPeriodCalculator.Resolve(
                InvestorPayoutFrequency.Semanal,
                new DateOnly(2026, 7, 15));

            Assert.Equal(DayOfWeek.Monday, periodo.Inicio.DayOfWeek);
            Assert.Equal(DayOfWeek.Sunday, periodo.Fin.DayOfWeek);
            Assert.Equal(6, periodo.Fin.DayNumber - periodo.Inicio.DayNumber);
        }

        [Theory]
        [InlineData(2026, 7, 1, true)]
        [InlineData(2026, 7, 15, false)]
        public void EsInicioDePeriodo_Mensual_SoloElPrimerDiaEsValido(int anio, int mes, int dia, bool esperado)
        {
            var resultado = InvestorPeriodCalculator.EsInicioDePeriodo(
                InvestorPayoutFrequency.Mensual,
                new DateOnly(anio, mes, dia));

            Assert.Equal(esperado, resultado);
        }

        [Fact]
        public void LastClosed_NuncaDevuelveUnPeriodoEnCurso()
        {
            var periodo = InvestorPeriodCalculator.LastClosed(
                InvestorPayoutFrequency.Mensual,
                new DateOnly(2026, 7, 15));

            Assert.Equal(new DateOnly(2026, 6, 1), periodo.Inicio);
            Assert.Equal(new DateOnly(2026, 6, 30), periodo.Fin);
        }
    }
}
