namespace LuxuryApp.Models.Horarios
{
    /// <summary>
    /// Ocurrencia concreta de una regla recurrente para un colaborador y una fecha.
    /// <see cref="Inicio"/> y <see cref="Fin"/> son hora LOCAL del negocio, igual que
    /// <c>Cita.FechaHoraCita</c>, de modo que comparar solapamientos no necesita conversión.
    /// </summary>
    public sealed record RecurringScheduleOccurrence
    {
        public int RuleId { get; init; }

        public int FuncionarioId { get; init; }

        public DateOnly Fecha { get; init; }

        public DateTime Inicio { get; init; }

        public DateTime Fin { get; init; }

        /// <summary>Texto visible en el calendario (ej. "Almuerzo").</summary>
        public string Titulo { get; init; } = string.Empty;

        public string? Motivo { get; init; }

        /// <summary>True si esa fecha viene de una excepción con horario alternativo.</summary>
        public bool EsExcepcion { get; init; }

        public int DuracionMinutos => (int)(Fin - Inicio).TotalMinutes;

        public bool Solapa(DateTime inicio, DateTime fin) =>
            TimeIntervalMath.Overlaps(Inicio, Fin, inicio, fin);
    }

    /// <summary>
    /// Expande reglas recurrentes en ocurrencias concretas. Función pura (sin base de datos ni
    /// reloj) para que la regla de negocio sea directamente testeable y no se duplique entre
    /// calendario, reservas públicas y validación de citas.
    /// </summary>
    public static class RecurringScheduleOccurrenceCalculator
    {
        /// <summary>
        /// Expande las reglas indicadas sobre el rango [desde, hasta] (ambos inclusive) para los
        /// colaboradores indicados.
        /// </summary>
        /// <param name="reglas">Reglas con <c>Colaboradores</c> y <c>Excepciones</c> ya cargados.</param>
        /// <param name="funcionarioIds">Colaboradores candidatos (típicamente los activos del tenant).</param>
        public static IReadOnlyList<RecurringScheduleOccurrence> Expand(
            IEnumerable<RecurringScheduleRule> reglas,
            IReadOnlyCollection<int> funcionarioIds,
            DateOnly desde,
            DateOnly hasta)
        {
            ArgumentNullException.ThrowIfNull(reglas);
            ArgumentNullException.ThrowIfNull(funcionarioIds);

            var resultado = new List<RecurringScheduleOccurrence>();
            if (funcionarioIds.Count == 0 || hasta < desde)
            {
                return resultado;
            }

            foreach (var regla in reglas)
            {
                if (!regla.Activa)
                {
                    continue;
                }

                // Colaboradores alcanzados. En alcance global la pertenencia es dinámica: se usa la
                // lista de candidatos recibida, así un colaborador nuevo queda cubierto sin tocar
                // la regla ni materializar filas.
                var alcanzados = regla.Alcance == RecurringScheduleScope.TodosLosColaboradores
                    ? funcionarioIds
                    : regla.Colaboradores
                        .Select(target => target.FuncionarioId)
                        .Where(funcionarioIds.Contains)
                        .Distinct()
                        .ToList();

                if (alcanzados.Count == 0)
                {
                    continue;
                }

                var excepcionesPorFecha = regla.Excepciones
                    .GroupBy(exception => exception.Fecha)
                    .ToDictionary(group => group.Key, group => group.ToList());

                for (var fecha = desde; fecha <= hasta; fecha = fecha.AddDays(1))
                {
                    if (!regla.CubreFecha(fecha))
                    {
                        continue;
                    }

                    excepcionesPorFecha.TryGetValue(fecha, out var excepcionesDia);

                    var excepcionGlobal = excepcionesDia?
                        .FirstOrDefault(exception => exception.FuncionarioId is null);

                    // Una excepción global de omisión cancela el bloque de todo el equipo ese día.
                    if (excepcionGlobal is not null &&
                        excepcionGlobal.Tipo != RecurringScheduleExceptionType.CambiarHorario)
                    {
                        continue;
                    }

                    foreach (var funcionarioId in alcanzados)
                    {
                        var excepcionPropia = excepcionesDia?
                            .FirstOrDefault(exception => exception.FuncionarioId == funcionarioId);

                        // La excepción del colaborador manda sobre la global.
                        var excepcion = excepcionPropia ?? excepcionGlobal;

                        if (excepcion is not null &&
                            excepcion.Tipo != RecurringScheduleExceptionType.CambiarHorario)
                        {
                            continue;
                        }

                        var horaInicio = regla.HoraInicio;
                        var horaFin = regla.HoraFin;
                        var esExcepcion = false;

                        if (excepcion is { Tipo: RecurringScheduleExceptionType.CambiarHorario } &&
                            excepcion.HoraInicioAlternativa.HasValue &&
                            excepcion.HoraFinAlternativa.HasValue)
                        {
                            horaInicio = excepcion.HoraInicioAlternativa.Value;
                            horaFin = excepcion.HoraFinAlternativa.Value;
                            esExcepcion = true;
                        }

                        if (horaFin <= horaInicio)
                        {
                            continue;
                        }

                        resultado.Add(new RecurringScheduleOccurrence
                        {
                            RuleId = regla.Id,
                            FuncionarioId = funcionarioId,
                            Fecha = fecha,
                            Inicio = fecha.ToDateTime(horaInicio),
                            Fin = fecha.ToDateTime(horaFin),
                            Titulo = regla.TextoCalendario,
                            Motivo = regla.Motivo,
                            EsExcepcion = esExcepcion
                        });
                    }
                }
            }

            return resultado;
        }

        /// <summary>Agrupa las ocurrencias por colaborador para consultas de disponibilidad.</summary>
        public static Dictionary<int, List<RecurringScheduleOccurrence>> GroupByFuncionario(
            IEnumerable<RecurringScheduleOccurrence> ocurrencias)
        {
            var map = new Dictionary<int, List<RecurringScheduleOccurrence>>();

            foreach (var ocurrencia in ocurrencias)
            {
                if (!map.TryGetValue(ocurrencia.FuncionarioId, out var lista))
                {
                    lista = new List<RecurringScheduleOccurrence>();
                    map[ocurrencia.FuncionarioId] = lista;
                }

                lista.Add(ocurrencia);
            }

            return map;
        }

        /// <summary>Nombres cortos de los días activos de una máscara (ej. "Lun, Mar, Mié").</summary>
        public static string DescribirDias(int diasSemanaMask)
        {
            string[] nombres = ["Dom", "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb"];

            var activos = Enumerable.Range(0, 7)
                .Where(index => (diasSemanaMask & (1 << index)) != 0)
                .Select(index => nombres[index])
                .ToList();

            return activos.Count == 0 ? "Sin días" : string.Join(", ", activos);
        }
    }
}
