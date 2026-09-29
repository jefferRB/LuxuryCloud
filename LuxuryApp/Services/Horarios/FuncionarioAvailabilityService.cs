using LuxuryApp.Models.Horarios;
using LuxuryApp.Models.Reservas;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Horarios
{
    /// <summary>
    /// Implementación de la disponibilidad unificada: citas, descansos, bloqueos recurrentes y
    /// solicitudes de reserva online pendientes.
    ///
    /// <para>
    /// Zona horaria: los bloqueos recurrentes se guardan como hora local del negocio y las citas
    /// también (<c>Cita.FechaHoraCita</c> es hora de pared, sin offset). Por eso las ocurrencias se
    /// expanden directamente a <c>DateTime</c> local y se comparan sin conversión. La conversión a
    /// UTC solo ocurre donde el flujo actual ya la hace (por ejemplo el envío de WhatsApp).
    /// </para>
    /// </summary>
    public sealed class FuncionarioAvailabilityService : IFuncionarioAvailabilityService
    {
        /// <summary>Duración por defecto de una cita sin duración explícita (igual que el calendario).</summary>
        internal const int DefaultDurationMinutes = 30;

        private readonly ApplicationDbContext _context;

        public FuncionarioAvailabilityService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<Dictionary<int, List<BusyInterval>>> GetBusyIntervalsAsync(
            IReadOnlyCollection<int> funcionarioIds,
            DateOnly desde,
            DateOnly hasta,
            int? excludeCitaId = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(funcionarioIds);

            var map = new Dictionary<int, List<BusyInterval>>();
            if (funcionarioIds.Count == 0 || hasta < desde)
            {
                return map;
            }

            // Ventana amplia: incluye citas que arrancaron el día anterior (duraciones largas).
            var rangoInicio = desde.AddDays(-1).ToDateTime(TimeOnly.MinValue);
            var rangoFin = hasta.AddDays(1).ToDateTime(TimeOnly.MinValue);

            var citas = await _context.Citas
                .AsNoTracking()
                .Where(cita =>
                    funcionarioIds.Contains(cita.FuncionarioId) &&
                    cita.FechaHoraCita >= rangoInicio &&
                    cita.FechaHoraCita < rangoFin &&
                    (!excludeCitaId.HasValue || cita.Id != excludeCitaId.Value))
                .Select(cita => new
                {
                    cita.Id,
                    cita.FuncionarioId,
                    cita.FechaHoraCita,
                    cita.Tipo,
                    Duracion = cita.Tipo == "DESCANSO"
                        ? (cita.DuracionMinutos ?? DefaultDurationMinutes)
                        : (cita.DuracionMinutos ?? (cita.Servicio != null ? cita.Servicio.DuracionMinutos : null) ?? DefaultDurationMinutes)
                })
                .ToListAsync(cancellationToken);

            foreach (var cita in citas)
            {
                Add(map, cita.FuncionarioId, new BusyInterval(
                    cita.FechaHoraCita,
                    cita.FechaHoraCita.AddMinutes(cita.Duracion),
                    cita.Tipo == "DESCANSO" ? BusyIntervalSources.Descanso : BusyIntervalSources.Cita,
                    null,
                    cita.Id));
            }

            // ── Solicitudes de reserva online PENDIENTES ──────────────────────────────────────
            // Una solicitud aceptada por el formulario público reserva el intervalo completo del
            // servicio sobre el funcionario que el servidor le asignó. Sólo Pending retiene:
            //   · Confirmed  → la Cita creada es la que ocupa (no se cuenta dos veces).
            //   · Rejected / Expired / CancelledByClient → liberan de inmediato.
            // Se pide en la misma ventana ampliada que las citas, así una solicitud larga que
            // arrancó el día anterior sigue tapando la mañana siguiente.
            var solicitudes = await _context.BookingRequests
                .AsNoTracking()
                .Where(solicitud =>
                    solicitud.Estado == BookingRequestStates.Pending &&
                    solicitud.FechaHoraInicioSolicitada >= rangoInicio &&
                    solicitud.FechaHoraInicioSolicitada < rangoFin &&
                    ((solicitud.FuncionarioAsignadoId != null &&
                      funcionarioIds.Contains(solicitud.FuncionarioAsignadoId.Value)) ||
                     (solicitud.FuncionarioAsignadoId == null &&
                      solicitud.FuncionarioId != null &&
                      funcionarioIds.Contains(solicitud.FuncionarioId.Value))))
                .Select(solicitud => new
                {
                    solicitud.Id,
                    solicitud.FuncionarioAsignadoId,
                    solicitud.FuncionarioId,
                    solicitud.FechaHoraInicioSolicitada,
                    // Duración del servidor (nunca la que mandó el navegador): la guardada al
                    // aceptar la solicitud y, si faltara, la del catálogo de servicios.
                    Duracion = solicitud.DuracionMinutos > 0
                        ? solicitud.DuracionMinutos
                        : ((solicitud.Servicio != null ? solicitud.Servicio.DuracionMinutos : null) ?? DefaultDurationMinutes)
                })
                .ToListAsync(cancellationToken);

            foreach (var solicitud in solicitudes)
            {
                // Legado: una solicitud "cualquiera" anterior a esta función no tiene recurso
                // concreto; no se le inventa uno (bloquear a todos sería peor que no bloquear).
                var funcionarioId = solicitud.FuncionarioAsignadoId ?? solicitud.FuncionarioId;
                if (funcionarioId is null)
                {
                    continue;
                }

                Add(map, funcionarioId.Value, new BusyInterval(
                    solicitud.FechaHoraInicioSolicitada,
                    solicitud.FechaHoraInicioSolicitada.AddMinutes(solicitud.Duracion),
                    BusyIntervalSources.SolicitudPendiente,
                    null,
                    solicitud.Id));
            }

            var bloqueos = await GetRecurringBlocksAsync(desde, hasta, funcionarioIds, cancellationToken);
            foreach (var bloqueo in bloqueos)
            {
                Add(map, bloqueo.FuncionarioId, new BusyInterval(
                    bloqueo.Inicio,
                    bloqueo.Fin,
                    BusyIntervalSources.BloqueoRecurrente,
                    bloqueo.Titulo,
                    bloqueo.RuleId));
            }

            return map;
        }

        public async Task<AvailabilityCheckResult> CheckAsync(
            int funcionarioId,
            DateTime inicio,
            int duracionMinutos,
            int? excludeCitaId = null,
            CancellationToken cancellationToken = default)
        {
            if (duracionMinutos <= 0)
            {
                return AvailabilityCheckResult.Libre();
            }

            var fin = inicio.AddMinutes(duracionMinutos);
            var desde = DateOnly.FromDateTime(inicio);
            var hasta = DateOnly.FromDateTime(fin);

            var map = await GetBusyIntervalsAsync(
                new[] { funcionarioId },
                desde,
                hasta,
                excludeCitaId,
                cancellationToken);

            if (!map.TryGetValue(funcionarioId, out var ocupados))
            {
                return AvailabilityCheckResult.Libre();
            }

            var conflicto = SelectConflict(ocupados, inicio, fin);

            if (conflicto is null)
            {
                return AvailabilityCheckResult.Libre();
            }

            var motivo = conflicto.Origen switch
            {
                BusyIntervalSources.BloqueoRecurrente =>
                    $"Ese horario está bloqueado por «{conflicto.Titulo ?? "bloqueo recurrente"}» " +
                    $"({conflicto.Inicio:HH:mm} a {conflicto.Fin:HH:mm}).",
                BusyIntervalSources.SolicitudPendiente =>
                    $"Ese horario está reservado por una solicitud de reserva online pendiente " +
                    $"({conflicto.Inicio:HH:mm} a {conflicto.Fin:HH:mm}). Confirmala o rechazala desde Reservas.",
                BusyIntervalSources.Descanso => "Ya existe un descanso en ese horario.",
                _ => "Ya existe una cita o descanso en ese horario."
            };

            return AvailabilityCheckResult.Ocupado(motivo, conflicto);
        }

        public async Task<IReadOnlyList<DateAvailabilityResult>> CheckManyAsync(
            IReadOnlyCollection<int> funcionarioIds,
            IReadOnlyCollection<DateOnly> fechas,
            TimeOnly hora,
            int duracionMinutos,
            int? excludeCitaId = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(funcionarioIds);
            ArgumentNullException.ThrowIfNull(fechas);

            var idsUnicos = funcionarioIds.Where(id => id > 0).Distinct().ToList();
            var fechasUnicas = fechas.Distinct().OrderBy(fecha => fecha).ToList();

            if (idsUnicos.Count == 0 || fechasUnicas.Count == 0 || duracionMinutos <= 0)
            {
                return Array.Empty<DateAvailabilityResult>();
            }

            // UNA sola lectura de ocupación para todo el rango y todo el conjunto de colaboradores.
            // Aquí está la diferencia con hacer una consulta por celda de la matriz.
            var ocupacion = await GetBusyIntervalsAsync(
                idsUnicos,
                fechasUnicas[0],
                fechasUnicas[^1],
                excludeCitaId,
                cancellationToken);

            // Y UNA sola lectura de nombres, para que el conflicto pueda decir "Drew" sin que
            // quien presenta tenga que volver a la base.
            var nombres = await _context.Funcionarios
                .AsNoTracking()
                .Where(funcionario => idsUnicos.Contains(funcionario.IdFuncionario))
                .Select(funcionario => new { funcionario.IdFuncionario, funcionario.Nombre })
                .ToDictionaryAsync(
                    funcionario => funcionario.IdFuncionario,
                    funcionario => funcionario.Nombre,
                    cancellationToken);

            var resultados = new List<DateAvailabilityResult>(fechasUnicas.Count);

            foreach (var fecha in fechasUnicas)
            {
                var inicio = fecha.ToDateTime(hora);
                var fin = inicio.AddMinutes(duracionMinutos);
                var conflictos = new List<ScheduleConflict>();

                foreach (var funcionarioId in idsUnicos)
                {
                    if (!ocupacion.TryGetValue(funcionarioId, out var ocupados))
                    {
                        continue;
                    }

                    // Un conflicto por colaborador y fecha: alcanza para explicar por qué el día no
                    // se puede elegir, y evita listas interminables cuando la agenda está llena.
                    var conflicto = SelectConflict(ocupados, inicio, fin);
                    if (conflicto is null)
                    {
                        continue;
                    }

                    conflictos.Add(new ScheduleConflict(
                        fecha,
                        funcionarioId,
                        nombres.TryGetValue(funcionarioId, out var nombre) ? nombre : string.Empty,
                        conflicto.Inicio,
                        conflicto.Fin,
                        conflicto.Origen));
                }

                resultados.Add(new DateAvailabilityResult(fecha, conflictos.Count == 0, conflictos));
            }

            return resultados;
        }

        /// <summary>
        /// Regla ÚNICA de solapamiento y de prioridad del conflicto que se reporta. La comparten
        /// <see cref="CheckAsync"/> y <see cref="CheckManyAsync"/>, así una fecha nunca puede estar
        /// "libre" para la vista previa y "ocupada" para el guardado.
        ///
        /// <para>
        /// El bloqueo recurrente gana: es más útil decir "coincide con Almuerzo" que un genérico
        /// "ya hay una cita". La solicitud pendiente va después, porque también explica algo que el
        /// usuario no ve como cita en la agenda.
        /// </para>
        /// </summary>
        private static BusyInterval? SelectConflict(
            IEnumerable<BusyInterval> ocupados,
            DateTime inicio,
            DateTime fin) =>
            ocupados
                .Where(intervalo => intervalo.Solapa(inicio, fin))
                .OrderByDescending(intervalo => intervalo.EsBloqueoRecurrente)
                .ThenByDescending(intervalo => intervalo.EsSolicitudPendiente)
                .FirstOrDefault();

        public async Task<IReadOnlyList<RecurringScheduleOccurrence>> GetRecurringBlocksAsync(
            DateOnly desde,
            DateOnly hasta,
            IReadOnlyCollection<int>? funcionarioIds = null,
            CancellationToken cancellationToken = default)
        {
            if (hasta < desde)
            {
                return Array.Empty<RecurringScheduleOccurrence>();
            }

            // Solo las reglas activas cuya vigencia toca el rango.
            var reglas = await _context.RecurringScheduleRules
                .AsNoTracking()
                .Include(regla => regla.Colaboradores)
                .Where(regla =>
                    regla.Activa &&
                    regla.VigenteDesde <= hasta &&
                    (regla.VigenteHasta == null || regla.VigenteHasta >= desde))
                .ToListAsync(cancellationToken);

            if (reglas.Count == 0)
            {
                return Array.Empty<RecurringScheduleOccurrence>();
            }

            var reglaIds = reglas.Select(regla => regla.Id).ToList();

            var excepciones = await _context.RecurringScheduleExceptions
                .AsNoTracking()
                .Where(exception =>
                    reglaIds.Contains(exception.RuleId) &&
                    exception.Fecha >= desde &&
                    exception.Fecha <= hasta)
                .ToListAsync(cancellationToken);

            var excepcionesPorRegla = excepciones
                .GroupBy(exception => exception.RuleId)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var regla in reglas)
            {
                regla.Excepciones = excepcionesPorRegla.TryGetValue(regla.Id, out var propias)
                    ? propias
                    : new List<RecurringScheduleException>();
            }

            // Alcance global = colaboradores ACTIVOS evaluados dinámicamente. Un colaborador creado
            // hoy queda cubierto sin tocar la regla; uno inactivo deja de bloquear su agenda.
            var candidatos = funcionarioIds is { Count: > 0 }
                ? await _context.Funcionarios
                    .AsNoTracking()
                    .Where(funcionario => funcionarioIds.Contains(funcionario.IdFuncionario))
                    .Select(funcionario => funcionario.IdFuncionario)
                    .ToListAsync(cancellationToken)
                : await _context.Funcionarios
                    .AsNoTracking()
                    .Where(funcionario => funcionario.Activo)
                    .Select(funcionario => funcionario.IdFuncionario)
                    .ToListAsync(cancellationToken);

            if (candidatos.Count == 0)
            {
                return Array.Empty<RecurringScheduleOccurrence>();
            }

            return RecurringScheduleOccurrenceCalculator.Expand(reglas, candidatos, desde, hasta);
        }

        private static void Add(Dictionary<int, List<BusyInterval>> map, int funcionarioId, BusyInterval intervalo)
        {
            if (!map.TryGetValue(funcionarioId, out var lista))
            {
                lista = new List<BusyInterval>();
                map[funcionarioId] = lista;
            }

            lista.Add(intervalo);
        }
    }
}
