using LuxuryApp.Models.Inversionistas;

namespace LuxuryApp.Tests.Inversionistas
{
    /// <summary>
    /// Resolución de periodos con día de corte. Es una función pura, así que se prueba sin base de
    /// datos y sin servicios: si estas cuentas fallan, falla todo el módulo de liquidación.
    /// </summary>
    public class InvestorSettlementPeriodResolverTests
    {
        private static InvestorAgreement Mensual(int? diaCorte) => new()
        {
            Frecuencia = InvestorPayoutFrequency.Mensual,
            DiaCorte = diaCorte
        };

        // ─────────────── Corte 20: el caso del enunciado ───────────────

        [Fact]
        public void Corte20_ProduceLosPeriodosDel21Al20()
        {
            var acuerdo = Mensual(20);

            var agosto = InvestorSettlementPeriodResolver.Resolve(acuerdo, new DateOnly(2026, 8, 5));
            Assert.Equal(new DateOnly(2026, 7, 21), agosto.Inicio);
            Assert.Equal(new DateOnly(2026, 8, 20), agosto.Fin);

            var septiembre = InvestorSettlementPeriodResolver.Next(acuerdo, agosto);
            Assert.Equal(new DateOnly(2026, 8, 21), septiembre.Inicio);
            Assert.Equal(new DateOnly(2026, 9, 20), septiembre.Fin);

            var octubre = InvestorSettlementPeriodResolver.Next(acuerdo, septiembre);
            Assert.Equal(new DateOnly(2026, 9, 21), octubre.Inicio);
            Assert.Equal(new DateOnly(2026, 10, 20), octubre.Fin);
        }

        [Fact]
        public void Corte20_ElDiaDelCorteCierraSuPeriodo_YElSiguienteDiaAbreElOtro()
        {
            var acuerdo = Mensual(20);

            // Un movimiento del día 20 pertenece al periodo que CIERRA ese día.
            var dia20 = InvestorSettlementPeriodResolver.Resolve(acuerdo, new DateOnly(2026, 8, 20));
            Assert.Equal(new DateOnly(2026, 7, 21), dia20.Inicio);
            Assert.Equal(new DateOnly(2026, 8, 20), dia20.Fin);

            // Un movimiento del 21 ya es del periodo siguiente.
            var dia21 = InvestorSettlementPeriodResolver.Resolve(acuerdo, new DateOnly(2026, 8, 21));
            Assert.Equal(new DateOnly(2026, 8, 21), dia21.Inicio);
            Assert.Equal(new DateOnly(2026, 9, 20), dia21.Fin);
        }

        [Fact]
        public void Corte20_ElPeriodoQueCierraHoyTodaviaNoEstaCerrado()
        {
            var acuerdo = Mensual(20);

            // Es 20 de agosto: el corte es HOY, así que el último periodo cerrado es el anterior.
            // Cerrar al inicio del propio día de corte dejaría fuera los cobros del día.
            var cerrado = InvestorSettlementPeriodResolver.PreviousClosedPeriod(acuerdo, new DateOnly(2026, 8, 20));
            Assert.Equal(new DateOnly(2026, 6, 21), cerrado.Inicio);
            Assert.Equal(new DateOnly(2026, 7, 20), cerrado.Fin);

            // El 21 ya se puede liquidar el periodo que cerró el 20.
            var cerradoAlDiaSiguiente = InvestorSettlementPeriodResolver.PreviousClosedPeriod(
                acuerdo,
                new DateOnly(2026, 8, 21));

            Assert.Equal(new DateOnly(2026, 7, 21), cerradoAlDiaSiguiente.Inicio);
            Assert.Equal(new DateOnly(2026, 8, 20), cerradoAlDiaSiguiente.Fin);
        }

        [Fact]
        public void NextCutoff_DevuelveElCierreDelPeriodoEnCurso()
        {
            var acuerdo = Mensual(20);

            Assert.Equal(
                new DateOnly(2026, 9, 20),
                InvestorSettlementPeriodResolver.NextCutoff(acuerdo, new DateOnly(2026, 8, 25)));

            // Si hoy ES el corte, el próximo corte es hoy: todavía no cerró.
            Assert.Equal(
                new DateOnly(2026, 8, 20),
                InvestorSettlementPeriodResolver.NextCutoff(acuerdo, new DateOnly(2026, 8, 20)));
        }

        // ─────────────── Días 29, 30 y 31 ───────────────

        [Theory]
        // Enero tiene 31: cierra el 31.
        [InlineData(2026, 1, 15, 2026, 1, 31, 2026, 1, 1)]
        // Febrero no llega al 31: cierra el último día real y el periodo arranca el 1.
        [InlineData(2026, 2, 15, 2026, 2, 28, 2026, 2, 1)]
        // Marzo vuelve a tener 31.
        [InlineData(2026, 3, 15, 2026, 3, 31, 2026, 3, 1)]
        // Abril tiene 30: cierra el 30.
        [InlineData(2026, 4, 15, 2026, 4, 30, 2026, 4, 1)]
        public void Corte31_UsaElUltimoDiaRealDelMes(
            int anio, int mes, int dia,
            int finAnio, int finMes, int finDia,
            int inicioAnio, int inicioMes, int inicioDia)
        {
            var periodo = InvestorSettlementPeriodResolver.Resolve(
                Mensual(31),
                new DateOnly(anio, mes, dia));

            Assert.Equal(new DateOnly(finAnio, finMes, finDia), periodo.Fin);
            Assert.Equal(new DateOnly(inicioAnio, inicioMes, inicioDia), periodo.Inicio);
        }

        [Fact]
        public void Corte31_EnAnioBisiesto_CierraElUltimoDiaDeFebrero()
        {
            var periodo = InvestorSettlementPeriodResolver.Resolve(
                Mensual(31),
                new DateOnly(2028, 2, 10));

            Assert.Equal(new DateOnly(2028, 2, 29), periodo.Fin);
            Assert.Equal(new DateOnly(2028, 2, 1), periodo.Inicio);
        }

        [Fact]
        public void Corte30_EnFebrero_ArrancaElDia31DeEnero()
        {
            // Enero cierra el 30, así que el 31 de enero ya pertenece al periodo de febrero.
            var periodo = InvestorSettlementPeriodResolver.Resolve(
                Mensual(30),
                new DateOnly(2026, 2, 10));

            Assert.Equal(new DateOnly(2026, 1, 31), periodo.Inicio);
            Assert.Equal(new DateOnly(2026, 2, 28), periodo.Fin);
        }

        [Fact]
        public void Corte29_NoDejaHuecosNiSolapesEntrePeriodosConsecutivos()
        {
            var acuerdo = Mensual(29);
            var periodo = InvestorSettlementPeriodResolver.Resolve(acuerdo, new DateOnly(2026, 1, 15));

            // Doce cierres seguidos: cada periodo empieza justo el día después del anterior.
            for (var i = 0; i < 12; i++)
            {
                var siguiente = InvestorSettlementPeriodResolver.Next(acuerdo, periodo);

                Assert.Equal(periodo.Fin.AddDays(1), siguiente.Inicio);
                Assert.True(siguiente.Fin > siguiente.Inicio);

                periodo = siguiente;
            }
        }

        // ─────────────── Compatibilidad hacia atrás ───────────────

        [Fact]
        public void SinDiaDeCorte_ConservaElMesCalendario()
        {
            var periodo = InvestorSettlementPeriodResolver.Resolve(Mensual(null), new DateOnly(2026, 8, 14));

            Assert.Equal(new DateOnly(2026, 8, 1), periodo.Inicio);
            Assert.Equal(new DateOnly(2026, 8, 31), periodo.Fin);
        }

        [Fact]
        public void SinDiaDeCorte_CoincideExactamenteConElCalculadorHistorico()
        {
            // Los acuerdos anteriores a esta función tienen DiaCorte NULL: tienen que resolverse
            // igual que antes, día por día, para no reinterpretar periodos ya liquidados.
            foreach (var frecuencia in Enum.GetValues<InvestorPayoutFrequency>())
            {
                var acuerdo = new InvestorAgreement { Frecuencia = frecuencia, DiaCorte = null };

                for (var dia = 0; dia < 400; dia++)
                {
                    var fecha = new DateOnly(2026, 1, 1).AddDays(dia);

                    var nuevo = InvestorSettlementPeriodResolver.Resolve(acuerdo, fecha);
                    var historico = InvestorPeriodCalculator.Resolve(frecuencia, fecha);

                    Assert.Equal(historico.Inicio, nuevo.Inicio);
                    Assert.Equal(historico.Fin, nuevo.Fin);
                }
            }
        }

        [Fact]
        public void ElDiaDeCorte_SoloAplicaAAcuerdosMensuales()
        {
            // Una frecuencia semanal con un día de corte cargado por error lo ignora.
            var semanal = new InvestorAgreement
            {
                Frecuencia = InvestorPayoutFrequency.Semanal,
                DiaCorte = 20
            };

            var periodo = InvestorSettlementPeriodResolver.Resolve(semanal, new DateOnly(2026, 8, 5));
            var historico = InvestorPeriodCalculator.Resolve(InvestorPayoutFrequency.Semanal, new DateOnly(2026, 8, 5));

            Assert.Equal(historico.Inicio, periodo.Inicio);
            Assert.Equal(historico.Fin, periodo.Fin);

            Assert.Null(InvestorSettlementPeriodResolver.NormalizarDiaCorte(InvestorPayoutFrequency.Semanal, 20));
            Assert.Equal(20, InvestorSettlementPeriodResolver.NormalizarDiaCorte(InvestorPayoutFrequency.Mensual, 20));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(32)]
        [InlineData(-1)]
        public void DiaDeCorteFueraDeRango_SeTrataComoMesCalendario(int diaCorte)
        {
            Assert.Null(InvestorSettlementPeriodResolver.NormalizarDiaCorte(
                InvestorPayoutFrequency.Mensual,
                diaCorte));
        }

        // ─────────────── Límite para cambiar un acuerdo ───────────────

        [Fact]
        public void ConCorte20_UnCambioSoloPuedeArrancarElDia21()
        {
            var frecuencia = InvestorPayoutFrequency.Mensual;

            Assert.True(InvestorSettlementPeriodResolver.EsInicioDePeriodo(frecuencia, 20, new DateOnly(2026, 8, 21)));
            Assert.False(InvestorSettlementPeriodResolver.EsInicioDePeriodo(frecuencia, 20, new DateOnly(2026, 8, 1)));

            // Estando a mitad de periodo, el próximo arranque válido es el día siguiente al corte.
            Assert.Equal(
                new DateOnly(2026, 8, 21),
                InvestorSettlementPeriodResolver.ProximoInicioDePeriodo(frecuencia, 20, new DateOnly(2026, 8, 5)));

            // Si hoy ya abre un periodo, el cambio puede entrar hoy mismo.
            Assert.Equal(
                new DateOnly(2026, 8, 21),
                InvestorSettlementPeriodResolver.ProximoInicioDePeriodo(frecuencia, 20, new DateOnly(2026, 8, 21)));
        }

        [Fact]
        public void SinCorte_UnCambioSoloPuedeArrancarElDia1()
        {
            var frecuencia = InvestorPayoutFrequency.Mensual;

            Assert.True(InvestorSettlementPeriodResolver.EsInicioDePeriodo(frecuencia, null, new DateOnly(2026, 8, 1)));
            Assert.False(InvestorSettlementPeriodResolver.EsInicioDePeriodo(frecuencia, null, new DateOnly(2026, 8, 21)));

            Assert.Equal(
                new DateOnly(2026, 9, 1),
                InvestorSettlementPeriodResolver.ProximoInicioDePeriodo(frecuencia, null, new DateOnly(2026, 8, 5)));
        }

        // ─────────────── Textos derivados del corte ───────────────

        [Fact]
        public void LasEtiquetasSalenDelCorteConfigurado_NuncaHardcodeadas()
        {
            Assert.Equal(
                "Corte mensual · día 20",
                InvestorSettlementPeriodResolver.EtiquetaCorte(InvestorPayoutFrequency.Mensual, 20));

            Assert.Equal(
                "Corte mensual · fin de mes",
                InvestorSettlementPeriodResolver.EtiquetaCorte(InvestorPayoutFrequency.Mensual, null));

            var descripcion = InvestorSettlementPeriodResolver.DescribirCorte(InvestorPayoutFrequency.Mensual, 20);
            Assert.Contains("día 21", descripcion);
            Assert.Contains("día 20", descripcion);
        }
    }
}
