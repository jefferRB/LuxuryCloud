using LuxuryApp.Models.Funcionarios;
using Xunit;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// Verifica la resolución de periodos de liquidación (semanal lunes–domingo y quincenal
    /// 1–15 / 16–fin de mes) y la navegación anterior/siguiente. No hay cálculo fiscal.
    /// </summary>
    public class PayrollPeriodCalculatorTests
    {
        [Theory]
        [InlineData(null, PayrollPeriodType.Semanal)]
        [InlineData("", PayrollPeriodType.Semanal)]
        [InlineData("semanal", PayrollPeriodType.Semanal)]
        [InlineData("Semanal", PayrollPeriodType.Semanal)]
        [InlineData("quincenal", PayrollPeriodType.Quincenal)]
        [InlineData("Quincenal", PayrollPeriodType.Quincenal)]
        [InlineData("personalizado", PayrollPeriodType.Personalizado)]
        [InlineData("Personalizado", PayrollPeriodType.Personalizado)]
        [InlineData("otra-cosa", PayrollPeriodType.Semanal)]
        public void ParseTipo_MapeaTexto(string? valor, PayrollPeriodType esperado)
        {
            Assert.Equal(esperado, PayrollPeriodCalculator.ParseTipo(valor));
        }

        [Fact]
        public void ResolvePersonalizado_UsaLasFechasIndicadas_SinAviso()
        {
            var hoy = new DateTime(2026, 7, 2);
            var (p, aviso) = PayrollPeriodCalculator.ResolvePersonalizado(
                new DateTime(2026, 6, 29), new DateTime(2026, 7, 5), hoy);

            Assert.Equal(PayrollPeriodType.Personalizado, p.Tipo);
            Assert.Equal(new DateTime(2026, 6, 29), p.Inicio);
            Assert.Equal(new DateTime(2026, 7, 5), p.Fin);
            Assert.Equal("Pagar rango", p.CtaTexto);
            Assert.Equal("Rango", p.TipoLabel);
            Assert.Null(aviso);
        }

        [Fact]
        public void ResolvePersonalizado_SinFechas_DefaultAlMesEnCurso()
        {
            var hoy = new DateTime(2026, 7, 2);
            var (p, aviso) = PayrollPeriodCalculator.ResolvePersonalizado(null, null, hoy);

            Assert.Equal(new DateTime(2026, 7, 1), p.Inicio);
            Assert.Equal(hoy, p.Fin);
            Assert.Null(aviso);
        }

        [Fact]
        public void ResolvePersonalizado_FechasInvertidas_LasIntercambiaYAvisa()
        {
            var hoy = new DateTime(2026, 7, 2);
            var (p, aviso) = PayrollPeriodCalculator.ResolvePersonalizado(
                new DateTime(2026, 7, 10), new DateTime(2026, 7, 1), hoy);

            Assert.Equal(new DateTime(2026, 7, 1), p.Inicio);
            Assert.Equal(new DateTime(2026, 7, 10), p.Fin);
            Assert.NotNull(aviso);
        }

        [Fact]
        public void ResolvePersonalizado_RangoMuyLargo_RecortaAlMaximoYAvisa()
        {
            var hoy = new DateTime(2026, 7, 2);
            var inicio = new DateTime(2026, 1, 1);
            var (p, aviso) = PayrollPeriodCalculator.ResolvePersonalizado(
                inicio, inicio.AddDays(400), hoy);

            Assert.Equal(inicio, p.Inicio);
            Assert.Equal(PayrollPeriodCalculator.MaxDiasPersonalizado, (p.Fin - p.Inicio).Days + 1);
            Assert.NotNull(aviso);
        }

        [Fact]
        public void Resolve_Semanal_LunesADomingoQueContieneLaFecha()
        {
            var p = PayrollPeriodCalculator.Resolve(PayrollPeriodType.Semanal, new DateTime(2026, 7, 1));

            Assert.Equal(DayOfWeek.Monday, p.Inicio.DayOfWeek);
            Assert.Equal(DayOfWeek.Sunday, p.Fin.DayOfWeek);
            Assert.Equal(6, (p.Fin - p.Inicio).Days);
            Assert.True(p.Inicio <= new DateTime(2026, 7, 1) && new DateTime(2026, 7, 1) <= p.Fin);
            Assert.Equal(p.Inicio.AddDays(-1), p.ReferenciaAnterior);
            Assert.Equal(p.Fin.AddDays(1), p.ReferenciaSiguiente);
            Assert.Equal("Pagar semana", p.CtaTexto);
            Assert.Equal("Semanal", p.TipoLabel);
        }

        [Fact]
        public void Resolve_QuincenalPrimeraMitad_1al15()
        {
            var p = PayrollPeriodCalculator.Resolve(PayrollPeriodType.Quincenal, new DateTime(2026, 7, 10));

            Assert.Equal(new DateTime(2026, 7, 1), p.Inicio);
            Assert.Equal(new DateTime(2026, 7, 15), p.Fin);
            Assert.Equal(new DateTime(2026, 6, 30), p.ReferenciaAnterior);
            Assert.Equal(new DateTime(2026, 7, 16), p.ReferenciaSiguiente);
            Assert.Equal("Pagar quincena", p.CtaTexto);
            Assert.Equal("Quincenal", p.TipoLabel);
        }

        [Fact]
        public void Resolve_QuincenalSegundaMitad_16aFinDeMes()
        {
            var p = PayrollPeriodCalculator.Resolve(PayrollPeriodType.Quincenal, new DateTime(2026, 7, 20));

            Assert.Equal(new DateTime(2026, 7, 16), p.Inicio);
            Assert.Equal(new DateTime(2026, 7, 31), p.Fin);
            Assert.Equal(new DateTime(2026, 7, 15), p.ReferenciaAnterior);
            Assert.Equal(new DateTime(2026, 8, 1), p.ReferenciaSiguiente);
        }

        [Fact]
        public void Resolve_QuincenalFebrero_RespetaUltimoDiaDelMes()
        {
            var p = PayrollPeriodCalculator.Resolve(PayrollPeriodType.Quincenal, new DateTime(2026, 2, 20));

            Assert.Equal(new DateTime(2026, 2, 16), p.Inicio);
            Assert.Equal(new DateTime(2026, 2, 28), p.Fin); // 2026 no es bisiesto
        }

        [Fact]
        public void Navegacion_Quincenal_SiguienteCaeEnLaSegundaMitad()
        {
            var primera = PayrollPeriodCalculator.Resolve(PayrollPeriodType.Quincenal, new DateTime(2026, 7, 5));
            var siguiente = PayrollPeriodCalculator.Resolve(PayrollPeriodType.Quincenal, primera.ReferenciaSiguiente);

            Assert.Equal(new DateTime(2026, 7, 16), siguiente.Inicio);
            Assert.Equal(new DateTime(2026, 7, 31), siguiente.Fin);
        }
    }
}
