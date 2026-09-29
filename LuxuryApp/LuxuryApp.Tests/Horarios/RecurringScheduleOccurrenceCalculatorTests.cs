using LuxuryApp.Models.Horarios;

namespace LuxuryApp.Tests.Horarios
{
    /// <summary>
    /// Expansión de reglas recurrentes en ocurrencias. Función pura: acá se prueba la regla de
    /// negocio de días, vigencia, alcance y excepciones sin tocar la base de datos.
    /// </summary>
    public class RecurringScheduleOccurrenceCalculatorTests
    {
        private static readonly int[] Equipo = [1, 2];

        [Fact]
        public void Expand_AlmuerzoLunesASabado_GeneraSeisBloquesPorColaborador()
        {
            var regla = BuildAlmuerzo();

            // Semana del lunes 2026-08-03 al domingo 2026-08-09.
            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                Equipo,
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 9));

            // 6 días (lun–sáb) × 2 colaboradores.
            Assert.Equal(12, ocurrencias.Count);
            Assert.DoesNotContain(ocurrencias, o => o.Fecha.DayOfWeek == DayOfWeek.Sunday);

            var lunesAna = ocurrencias.Single(o =>
                o.FuncionarioId == 1 && o.Fecha == new DateOnly(2026, 8, 3));

            Assert.Equal(new DateTime(2026, 8, 3, 13, 0, 0), lunesAna.Inicio);
            Assert.Equal(new DateTime(2026, 8, 3, 14, 0, 0), lunesAna.Fin);
            Assert.Equal("Almuerzo", lunesAna.Titulo);
        }

        [Fact]
        public void Expand_ConAlcanceGlobal_AlcanzaAUnColaboradorNuevo()
        {
            var regla = BuildAlmuerzo();

            // El colaborador 3 no existía cuando se creó la regla: igual queda cubierto porque la
            // pertenencia se evalúa dinámicamente sobre los candidatos recibidos.
            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                [1, 2, 3],
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 3));

            Assert.Equal(3, ocurrencias.Count);
            Assert.Contains(ocurrencias, o => o.FuncionarioId == 3);
            Assert.Empty(regla.Colaboradores);
        }

        [Fact]
        public void Expand_ConColaboradoresSeleccionados_SoloAlcanzaAEsos()
        {
            var regla = BuildAlmuerzo();
            regla.Alcance = RecurringScheduleScope.ColaboradoresSeleccionados;
            regla.Colaboradores = [new RecurringScheduleRuleTarget { FuncionarioId = 2 }];

            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                [1, 2, 3],
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 3));

            var unica = Assert.Single(ocurrencias);
            Assert.Equal(2, unica.FuncionarioId);
        }

        [Fact]
        public void Expand_ReglaPausada_NoGeneraNada()
        {
            var regla = BuildAlmuerzo();
            regla.Activa = false;

            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                Equipo,
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 9));

            Assert.Empty(ocurrencias);
        }

        [Fact]
        public void Expand_ReglaConFechaFinal_DejaDeGenerarDespuesDeEsaFecha()
        {
            var regla = BuildAlmuerzo();
            regla.VigenteHasta = new DateOnly(2026, 8, 5);

            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                [1],
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 9));

            Assert.Equal(3, ocurrencias.Count);
            Assert.All(ocurrencias, o => Assert.True(o.Fecha <= new DateOnly(2026, 8, 5)));
        }

        [Fact]
        public void Expand_ReglaQueTodaviaNoEmpieza_NoGeneraNada()
        {
            var regla = BuildAlmuerzo();
            regla.VigenteDesde = new DateOnly(2026, 9, 1);

            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                [1],
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 9));

            Assert.Empty(ocurrencias);
        }

        [Fact]
        public void Expand_ExcepcionDeOmisionParaUnColaborador_SoloLoExceptuaAEl()
        {
            var regla = BuildAlmuerzo();
            regla.Excepciones =
            [
                new RecurringScheduleException
                {
                    RuleId = regla.Id,
                    FuncionarioId = 1,
                    Fecha = new DateOnly(2026, 8, 4),
                    Tipo = RecurringScheduleExceptionType.Omitir
                }
            ];

            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                Equipo,
                new DateOnly(2026, 8, 4),
                new DateOnly(2026, 8, 4));

            var unica = Assert.Single(ocurrencias);
            Assert.Equal(2, unica.FuncionarioId);
        }

        [Fact]
        public void Expand_ExcepcionGlobalDeOmision_CancelaElBloqueDeTodoElEquipoEseDia()
        {
            var regla = BuildAlmuerzo();
            regla.Excepciones =
            [
                new RecurringScheduleException
                {
                    RuleId = regla.Id,
                    FuncionarioId = null,
                    Fecha = new DateOnly(2026, 8, 4),
                    Tipo = RecurringScheduleExceptionType.Omitir,
                    Motivo = "Feriado"
                }
            ];

            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                Equipo,
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 5));

            Assert.DoesNotContain(ocurrencias, o => o.Fecha == new DateOnly(2026, 8, 4));
            Assert.Equal(4, ocurrencias.Count);
        }

        [Fact]
        public void Expand_ExcepcionConHorarioAlternativo_CambiaSoloEseDiaYEseColaborador()
        {
            var regla = BuildAlmuerzo();
            regla.Excepciones =
            [
                new RecurringScheduleException
                {
                    RuleId = regla.Id,
                    FuncionarioId = 1,
                    Fecha = new DateOnly(2026, 8, 15),
                    Tipo = RecurringScheduleExceptionType.CambiarHorario,
                    HoraInicioAlternativa = new TimeOnly(14, 0),
                    HoraFinAlternativa = new TimeOnly(15, 0)
                }
            ];

            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                Equipo,
                new DateOnly(2026, 8, 15),
                new DateOnly(2026, 8, 15));

            var maria = ocurrencias.Single(o => o.FuncionarioId == 1);
            var otro = ocurrencias.Single(o => o.FuncionarioId == 2);

            Assert.Equal(new DateTime(2026, 8, 15, 14, 0, 0), maria.Inicio);
            Assert.Equal(new DateTime(2026, 8, 15, 15, 0, 0), maria.Fin);
            Assert.True(maria.EsExcepcion);

            // La regla general no cambió para el resto del equipo.
            Assert.Equal(new DateTime(2026, 8, 15, 13, 0, 0), otro.Inicio);
            Assert.False(otro.EsExcepcion);
            Assert.Equal(new TimeOnly(13, 0), regla.HoraInicio);
        }

        [Fact]
        public void Expand_ExcepcionPropiaGanaSobreLaGlobal()
        {
            var regla = BuildAlmuerzo();
            regla.Excepciones =
            [
                new RecurringScheduleException
                {
                    RuleId = regla.Id,
                    FuncionarioId = null,
                    Fecha = new DateOnly(2026, 8, 4),
                    Tipo = RecurringScheduleExceptionType.CambiarHorario,
                    HoraInicioAlternativa = new TimeOnly(12, 0),
                    HoraFinAlternativa = new TimeOnly(13, 0)
                },
                new RecurringScheduleException
                {
                    RuleId = regla.Id,
                    FuncionarioId = 2,
                    Fecha = new DateOnly(2026, 8, 4),
                    Tipo = RecurringScheduleExceptionType.ExcluirColaborador
                }
            ];

            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                Equipo,
                new DateOnly(2026, 8, 4),
                new DateOnly(2026, 8, 4));

            var unica = Assert.Single(ocurrencias);
            Assert.Equal(1, unica.FuncionarioId);
            Assert.Equal(new DateTime(2026, 8, 4, 12, 0, 0), unica.Inicio);
        }

        [Fact]
        public void Expand_HorasSeInterpretanComoHoraLocalDelNegocio()
        {
            var regla = BuildAlmuerzo();

            var ocurrencia = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { regla },
                [1],
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 3)).Single();

            // 1:00 p. m. del reloj de pared, sin desplazamiento por zona horaria ni conversión a UTC.
            Assert.Equal(13, ocurrencia.Inicio.Hour);
            Assert.Equal(DateTimeKind.Unspecified, ocurrencia.Inicio.Kind);
            Assert.Equal(60, ocurrencia.DuracionMinutos);
        }

        [Fact]
        public void DescribirDias_DevuelveLosNombresCortosDeLaMascara()
        {
            Assert.Equal(
                "Lun, Mar, Mié, Jue, Vie, Sáb",
                RecurringScheduleOccurrenceCalculator.DescribirDias(RecurringScheduleRule.LunesASabadoMask));

            Assert.Equal("Sin días", RecurringScheduleOccurrenceCalculator.DescribirDias(0));
        }

        private static RecurringScheduleRule BuildAlmuerzo() => new()
        {
            Id = 10,
            Nombre = "Almuerzo",
            HoraInicio = new TimeOnly(13, 0),
            HoraFin = new TimeOnly(14, 0),
            DiasSemanaMask = RecurringScheduleRule.LunesASabadoMask,
            VigenteDesde = new DateOnly(2026, 1, 1),
            Activa = true,
            Alcance = RecurringScheduleScope.TodosLosColaboradores
        };
    }
}
