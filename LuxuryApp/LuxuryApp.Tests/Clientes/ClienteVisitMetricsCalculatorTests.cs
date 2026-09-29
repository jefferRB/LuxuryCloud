using LuxuryApp.Models.DataBase;
using LuxuryApp.Services.Clientes;

namespace LuxuryApp.Tests.Clientes
{
    /// <summary>
    /// Reglas de negocio de las métricas CRM. Son tests del cálculo PURO: no tocan la base
    /// de datos, así que describen la regla sin ruido de infraestructura.
    /// </summary>
    public class ClienteVisitMetricsCalculatorTests
    {
        private static readonly DateTime Hoy = new(2026, 9, 16);

        [Fact]
        public void SinCitas_NoHayFrecuenciaNiUltimaVisita()
        {
            var metricas = Calc(Array.Empty<DateTime>(), Hoy);

            Assert.Equal(0, metricas.AttendedVisits);
            Assert.Null(metricas.AverageVisitFrequencyDays);
            Assert.Null(metricas.LastVisitDate);
            Assert.Null(metricas.DaysSinceLastVisit);
            Assert.Null(metricas.ExpectedReturnDate);
        }

        [Fact]
        public void UnaSolaCita_TodaviaNoHayFrecuencia()
        {
            // Con un solo día de visita no existe ningún intervalo que promediar: la frecuencia
            // es "todavía no disponible", nunca "0 días".
            var metricas = Calc(
                new[] { new DateTime(2026, 9, 2, 10, 0, 0) },
                Hoy);

            Assert.Equal(1, metricas.AttendedVisits);
            Assert.Null(metricas.AverageVisitFrequencyDays);
            Assert.Equal(new DateTime(2026, 9, 2), metricas.LastVisitDate);
            Assert.Equal(14, metricas.DaysSinceLastVisit);

            // Sin promedio observado, la efectiva es la inicial y con ella se proyecta el regreso.
            Assert.Equal(ClienteDefaults.InitialVisitFrequencyDays, metricas.EffectiveFrequencyDays);
            Assert.Equal(new DateTime(2026, 9, 17), metricas.ExpectedReturnDate);
        }

        // ── FRECUENCIA INICIAL vs EFECTIVA ────────────────────────────────────

        [Fact]
        public void SinCitas_LaFrecuenciaEfectivaEsLaInicial()
        {
            var metricas = Calc(Array.Empty<DateTime>(), Hoy, frecuenciaInicialDias: 15);

            Assert.Equal(15, metricas.EffectiveFrequencyDays);
            Assert.Null(metricas.AverageVisitFrequencyDays);
        }

        [Fact]
        public void SinCitas_RespetaUnaFrecuenciaInicialPersonalizada()
        {
            var metricas = Calc(Array.Empty<DateTime>(), Hoy, frecuenciaInicialDias: 20);

            Assert.Equal(20, metricas.EffectiveFrequencyDays);
        }

        [Fact]
        public void UnaSolaCita_LaFrecuenciaEfectivaSigueSiendoLaInicial()
        {
            var metricas = Calc(
                new[] { new DateTime(2026, 9, 2) },
                Hoy,
                frecuenciaInicialDias: 15);

            Assert.Equal(15, metricas.EffectiveFrequencyDays);
            Assert.Null(metricas.AverageVisitFrequencyDays);
        }

        [Fact]
        public void DosDiasDeVisita_ElPromedioObservadoDesplazaALaInicial()
        {
            // Inicial 15, pero el cliente demostró un patrón de 20 días: manda el observado.
            var metricas = Calc(
                new[] { new DateTime(2026, 8, 1), new DateTime(2026, 8, 21) },
                new DateTime(2026, 8, 25),
                frecuenciaInicialDias: 15);

            Assert.Equal(20, metricas.AverageVisitFrequencyDays);
            Assert.Equal(20, metricas.EffectiveFrequencyDays);
        }

        [Fact]
        public void TresDiasDeVisita_RecalculaElPromedio()
        {
            // Intervalos de 20 y 10 => 15.
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 8, 1),
                    new DateTime(2026, 8, 21),
                    new DateTime(2026, 8, 31)
                },
                new DateTime(2026, 9, 5),
                frecuenciaInicialDias: 99);

            Assert.Equal(15, metricas.EffectiveFrequencyDays);
        }

        [Fact]
        public void CuatroDiasDeVisita_RecalculaElPromedio()
        {
            // Intervalos de 20, 10 y 15 => 15.
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 8, 1),
                    new DateTime(2026, 8, 21),
                    new DateTime(2026, 8, 31),
                    new DateTime(2026, 9, 15)
                },
                new DateTime(2026, 9, 20),
                frecuenciaInicialDias: 99);

            Assert.Equal(15, metricas.EffectiveFrequencyDays);
        }

        [Fact]
        public void VariasCitasElMismoDia_SoloDeduplicanParaLaFrecuenciaEfectiva()
        {
            // 02/09 dos veces y 10/09: 3 visitas atendidas, pero un único intervalo de 8 días.
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 9, 2, 9, 0, 0),
                    new DateTime(2026, 9, 2, 15, 0, 0),
                    new DateTime(2026, 9, 10, 10, 0, 0)
                },
                Hoy,
                frecuenciaInicialDias: 15);

            Assert.Equal(3, metricas.AttendedVisits);
            Assert.Equal(8, metricas.EffectiveFrequencyDays);
        }

        [Fact]
        public void UnaFrecuenciaInicialCorrupta_CaeAlDefaultDelDominio()
        {
            // Un 0 heredado en la columna no debe mostrarse como "0 días".
            var metricas = Calc(Array.Empty<DateTime>(), Hoy, frecuenciaInicialDias: 0);

            Assert.Equal(ClienteDefaults.InitialVisitFrequencyDays, metricas.EffectiveFrequencyDays);
        }

        [Fact]
        public void DosCitas_CasoReportado_DaDosVisitasYSieteDias()
        {
            // Historial: 02/09 y 09/09. Hoy es 16/09.
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 9, 2, 10, 0, 0),
                    new DateTime(2026, 9, 9, 10, 0, 0)
                },
                Hoy);

            Assert.Equal(2, metricas.AttendedVisits);
            Assert.Equal(new DateTime(2026, 9, 9), metricas.LastVisitDate);
            Assert.Equal(7, metricas.AverageVisitFrequencyDays);
            Assert.Equal(7, metricas.DaysSinceLastVisit);
        }

        [Fact]
        public void VariasCitas_PromedioDeLosIntervalos()
        {
            // Intervalos de 7 y 11 días => 9.
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 9, 2),
                    new DateTime(2026, 9, 9),
                    new DateTime(2026, 9, 20)
                },
                new DateTime(2026, 9, 25));

            Assert.Equal(3, metricas.AttendedVisits);
            Assert.Equal(9, metricas.AverageVisitFrequencyDays);
            Assert.Equal(new DateTime(2026, 9, 20), metricas.LastVisitDate);
        }

        [Fact]
        public void PromedioDecimal_RedondeaHaciaArriba_EnElPuntoMedio()
        {
            // Intervalos de 15 y 18 días => 16.5. La regla es redondeo away-from-zero,
            // así que se muestra 17 y no 16.
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 9, 1),
                    new DateTime(2026, 9, 16),
                    new DateTime(2026, 10, 4)
                },
                new DateTime(2026, 10, 10));

            Assert.Equal(17, metricas.AverageVisitFrequencyDays);
        }

        [Fact]
        public void VariasCitasElMismoDia_CuentanIndividualmenteComoVisitas()
        {
            // Tres citas el mismo día son tres visitas atendidas: el contador cuenta citas.
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 9, 2, 9, 0, 0),
                    new DateTime(2026, 9, 2, 14, 0, 0),
                    new DateTime(2026, 9, 9, 10, 0, 0)
                },
                Hoy);

            Assert.Equal(3, metricas.AttendedVisits);
        }

        [Fact]
        public void VariasCitasElMismoDia_NoGeneranIntervaloDeCero()
        {
            // Los mismos datos del test anterior: para la frecuencia solo cuentan los DÍAS
            // distintos (02/09 y 09/09), así que el promedio es 7 y no 3.5.
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 9, 2, 9, 0, 0),
                    new DateTime(2026, 9, 2, 14, 0, 0),
                    new DateTime(2026, 9, 9, 10, 0, 0)
                },
                Hoy);

            Assert.Equal(7, metricas.AverageVisitFrequencyDays);
        }

        [Fact]
        public void DosCitasElMismoDia_NoDanFrecuenciaTodavia()
        {
            // Dos citas pero un solo día distinto: no hay intervalo que promediar.
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 9, 2, 9, 0, 0),
                    new DateTime(2026, 9, 2, 14, 0, 0)
                },
                Hoy);

            Assert.Equal(2, metricas.AttendedVisits);
            Assert.Null(metricas.AverageVisitFrequencyDays);
        }

        [Fact]
        public void CitasFuturas_NoAlteranNingunaMetrica()
        {
            var soloPasado = Calc(
                new[] { new DateTime(2026, 9, 2), new DateTime(2026, 9, 9) },
                Hoy);

            var conFutura = Calc(
                new[]
                {
                    new DateTime(2026, 9, 2),
                    new DateTime(2026, 9, 9),
                    new DateTime(2026, 10, 20)
                },
                Hoy);

            Assert.Equal(soloPasado, conFutura);
            Assert.Equal(new DateTime(2026, 9, 9), conFutura.LastVisitDate);
            Assert.Equal(2, conFutura.AttendedVisits);
        }

        [Fact]
        public void CitaDeHoy_DaCeroDiasSinVisitar()
        {
            var metricas = Calc(
                new[] { Hoy.AddHours(9) },
                Hoy);

            Assert.Equal(0, metricas.DaysSinceLastVisit);
            Assert.Equal(1, metricas.AttendedVisits);
        }

        [Fact]
        public void DiasSinVisitar_SeMideContraElDiaLocal_NoContraLaHora()
        {
            // Cita a las 23:50 y "hoy" a las 00:10 del día siguiente: es 1 día de diferencia
            // en el calendario local, no 0 por llevar menos de 24 horas.
            var metricas = Calc(
                new[] { new DateTime(2026, 9, 15, 23, 50, 0) },
                new DateTime(2026, 9, 16, 0, 10, 0));

            Assert.Equal(new DateTime(2026, 9, 15), metricas.LastVisitDate);
            Assert.Equal(1, metricas.DaysSinceLastVisit);
        }

        [Fact]
        public void CitasCercaDeMedianoche_ElMismoDiaLocal_SonUnSoloDiaParaLaFrecuencia()
        {
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 9, 15, 0, 5, 0),
                    new DateTime(2026, 9, 15, 23, 55, 0)
                },
                Hoy);

            Assert.Equal(2, metricas.AttendedVisits);
            Assert.Null(metricas.AverageVisitFrequencyDays);
        }

        [Fact]
        public void FechaEsperadaDeRegreso_EsUltimaVisitaMasFrecuencia()
        {
            var metricas = Calc(
                new[] { new DateTime(2026, 9, 2), new DateTime(2026, 9, 9) },
                Hoy);

            Assert.Equal(7, metricas.AverageVisitFrequencyDays);
            Assert.Equal(7, metricas.EffectiveFrequencyDays);
            Assert.Equal(new DateTime(2026, 9, 16), metricas.ExpectedReturnDate);
        }

        [Fact]
        public void FechasDesordenadas_SeOrdenanAntesDeCalcular()
        {
            var metricas = Calc(
                new[]
                {
                    new DateTime(2026, 9, 20),
                    new DateTime(2026, 9, 2),
                    new DateTime(2026, 9, 9)
                },
                new DateTime(2026, 9, 25));

            Assert.Equal(new DateTime(2026, 9, 20), metricas.LastVisitDate);
            Assert.Equal(9, metricas.AverageVisitFrequencyDays);
        }

        /// <summary>
        /// La frecuencia inicial por defecto es la del dominio; los tests que la ejercitan
        /// explícitamente pasan la suya.
        /// </summary>
        private static ClienteVisitMetrics Calc(
            IEnumerable<DateTime> fechasCitas,
            DateTime hoyLocal,
            int frecuenciaInicialDias = ClienteDefaults.InitialVisitFrequencyDays) =>
            ClienteVisitMetricsCalculator.Calculate(fechasCitas, hoyLocal, frecuenciaInicialDias);
    }
}
