using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Horarios;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Reservas
{
    public sealed class BookingAvailabilityService : IBookingAvailabilityService
    {
        /// <summary>Tope duro de sugerencias por consulta: el cliente nunca puede pedir más.</summary>
        private const int MaxSuggestionsCeiling = 10;

        /// <summary>
        /// Tamaño de la ventana de barrido al buscar próximos espacios. La ocupación se consulta
        /// una vez por ventana y el barrido corta apenas se completan las sugerencias, así el caso
        /// normal (hay espacio esta semana) cuesta UNA consulta en vez de cargar todo el horizonte.
        /// </summary>
        private const int ScanChunkDays = 7;

        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IBookingCatalogService _catalogService;
        private readonly IFuncionarioAvailabilityService _availabilityService;

        public BookingAvailabilityService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IBookingCatalogService catalogService,
            IFuncionarioAvailabilityService availabilityService)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _catalogService = catalogService;
            _availabilityService = availabilityService;
        }

        public async Task<IReadOnlyList<string>> GetAvailableSlotsAsync(
            int servicioId,
            DateOnly fecha,
            int? funcionarioId,
            CancellationToken cancellationToken = default)
        {
            var (settings, schedule) = await LoadScheduleAsync(cancellationToken);

            if (settings is null || !settings.PublicBookingEnabled)
            {
                return Array.Empty<string>();
            }

            // Validar rango de fechas permitido y día abierto.
            var today = DateOnly.FromDateTime(_businessDateTimeProvider.Today());
            var maxDate = today.AddDays(Math.Max(0, settings.PublicBookingMaxDaysAhead));

            if (fecha < today || fecha > maxDate || !schedule.IsOpen(fecha.DayOfWeek))
            {
                return Array.Empty<string>();
            }

            var duracion = await ResolveServicioDuracionAsync(servicioId, cancellationToken);
            if (duracion is null)
            {
                return Array.Empty<string>();
            }

            var candidatos = await ResolveCandidatosAsync(servicioId, funcionarioId, cancellationToken);
            if (candidatos.Count == 0)
            {
                return Array.Empty<string>();
            }

            var busyByFuncionario = await LoadBusyIntervalsAsync(candidatos, fecha, cancellationToken);

            var now = _businessDateTimeProvider.Now();
            var earliest = now.AddMinutes(Math.Max(0, settings.PublicBookingMinAdvanceMinutes));

            var resultados = new List<string>();

            foreach (var (hora, inicio, fin) in EnumerateDaySlots(schedule, settings.SlotIntervalMinutes, fecha, duracion.Value))
            {
                if (inicio >= earliest && EstaLibre(busyByFuncionario, candidatos, inicio, fin))
                {
                    resultados.Add(hora.ToString("HH:mm"));
                }
            }

            return resultados;
        }

        public async Task<SlotResolution> ResolveSlotAsync(
            int servicioId,
            DateTime inicio,
            int? funcionarioId,
            CancellationToken cancellationToken = default)
        {
            var duracion = await ResolveServicioDuracionAsync(servicioId, cancellationToken);
            if (duracion is null)
            {
                return SlotResolution.NoDisponible("El servicio ya no está disponible.");
            }

            var fecha = DateOnly.FromDateTime(inicio);
            var fin = inicio.AddMinutes(duracion.Value);

            // Revalida día abierto y ventana horaria del negocio en backend (no se confía en el
            // frontend: un POST manipulado podría pedir un día cerrado u hora fuera de jornada).
            // Punto único compartido por la solicitud pública y la confirmación del admin: la
            // jornada sale del MISMO BookingWeeklySchedule que ofrece los horarios.
            var (settings, schedule) = await LoadScheduleAsync(cancellationToken);

            if (settings is not null)
            {
                if (!schedule.IsOpen(fecha.DayOfWeek))
                {
                    return SlotResolution.NoDisponible("Ese día no está disponible para reservas.");
                }

                if (fin.Date != inicio.Date ||
                    !schedule.Fits(fecha.DayOfWeek, TimeOnly.FromDateTime(inicio), TimeOnly.FromDateTime(fin)))
                {
                    return SlotResolution.NoDisponible("Ese horario está fuera de la jornada del negocio.");
                }
            }

            var candidatos = await ResolveCandidatosAsync(servicioId, funcionarioId, cancellationToken);
            if (candidatos.Count == 0)
            {
                return SlotResolution.NoDisponible("No hay funcionarios disponibles para esta reserva.");
            }

            var busyByFuncionario = await LoadBusyIntervalsAsync(candidatos, fecha, cancellationToken);

            foreach (var candidato in candidatos)
            {
                if (!Solapa(busyByFuncionario, candidato, inicio, fin))
                {
                    return new SlotResolution
                    {
                        Disponible = true,
                        FuncionarioId = candidato,
                        DuracionMinutos = duracion.Value
                    };
                }
            }

            return SlotResolution.NoDisponible("Ese horario ya no está disponible.");
        }

        public async Task<IReadOnlyList<AvailableSlotSuggestion>> GetNextAvailableSlotsAsync(
            int servicioId,
            DateOnly fromDate,
            int? funcionarioId,
            int maxSuggestions = 5,
            CancellationToken cancellationToken = default)
        {
            maxSuggestions = maxSuggestions <= 0 ? 5 : Math.Min(maxSuggestions, MaxSuggestionsCeiling);

            var (settings, schedule) = await LoadScheduleAsync(cancellationToken);

            if (settings is null || !settings.PublicBookingEnabled)
            {
                return Array.Empty<AvailableSlotSuggestion>();
            }

            var today = DateOnly.FromDateTime(_businessDateTimeProvider.Today());
            var maxDate = today.AddDays(Math.Max(0, settings.PublicBookingMaxDaysAhead));
            var start = fromDate < today ? today : fromDate;
            if (start > maxDate)
            {
                return Array.Empty<AvailableSlotSuggestion>();
            }

            var duracion = await ResolveServicioDuracionAsync(servicioId, cancellationToken);
            if (duracion is null)
            {
                return Array.Empty<AvailableSlotSuggestion>();
            }

            var candidatos = await ResolveCandidatosAsync(servicioId, funcionarioId, cancellationToken);
            if (candidatos.Count == 0)
            {
                return Array.Empty<AvailableSlotSuggestion>();
            }

            var now = _businessDateTimeProvider.Now();
            var earliest = now.AddMinutes(Math.Max(0, settings.PublicBookingMinAdvanceMinutes));

            var results = new List<AvailableSlotSuggestion>();

            // Barrido por ventanas de ScanChunkDays con corte temprano: una sola consulta de
            // ocupación por ventana (nunca una por día) y se abandona apenas hay sugerencias
            // suficientes. Esto acota el costo del endpoint público, que ahora se consulta en
            // cuanto el cliente elige servicio y profesional.
            for (var chunkStart = start;
                 chunkStart <= maxDate && results.Count < maxSuggestions;
                 chunkStart = chunkStart.AddDays(ScanChunkDays))
            {
                var chunkEnd = chunkStart.AddDays(ScanChunkDays - 1);
                if (chunkEnd > maxDate)
                {
                    chunkEnd = maxDate;
                }

                // Ventana sin ningún día abierto: no vale la pena consultar la ocupación.
                if (!schedule.HasOpenDay(chunkStart, chunkEnd))
                {
                    continue;
                }

                var busy = await LoadBusyIntervalsRangeAsync(candidatos, chunkStart, chunkEnd, cancellationToken);

                for (var fecha = chunkStart; fecha <= chunkEnd && results.Count < maxSuggestions; fecha = fecha.AddDays(1))
                {
                    if (!schedule.IsOpen(fecha.DayOfWeek))
                    {
                        continue;
                    }

                    foreach (var (hora, inicio, fin) in EnumerateDaySlots(schedule, settings.SlotIntervalMinutes, fecha, duracion.Value))
                    {
                        if (results.Count >= maxSuggestions)
                        {
                            break;
                        }

                        if (inicio < earliest)
                        {
                            continue;
                        }

                        var funcId = FindFreeCandidate(busy, candidatos, inicio, fin);
                        if (funcId.HasValue)
                        {
                            results.Add(new AvailableSlotSuggestion(fecha, hora, funcId.Value));
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Recorrido ÚNICO de los slots de un día: mismo paso, la jornada PROPIA de ese día de la
        /// semana y la misma regla de "el bloque completo debe caber antes del cierre". Lo comparten
        /// la disponibilidad del día y la búsqueda de próximos espacios, para que no diverjan nunca.
        /// </summary>
        private static IEnumerable<(TimeOnly Hora, DateTime Inicio, DateTime Fin)> EnumerateDaySlots(
            BookingWeeklySchedule schedule,
            int slotIntervalMinutes,
            DateOnly fecha,
            int duracionMinutos)
        {
            var ventana = schedule.GetDay(fecha.DayOfWeek);
            if (!ventana.Abierto)
            {
                yield break;
            }

            var intervalo = Math.Max(5, slotIntervalMinutes);
            var cursor = ventana.Apertura;

            while (true)
            {
                var inicio = fecha.ToDateTime(cursor);
                var fin = inicio.AddMinutes(duracionMinutos);

                // Terminar exactamente a la hora de cierre es válido: los intervalos son [inicio, fin).
                if (fin.Date != inicio.Date || TimeOnly.FromDateTime(fin) > ventana.Cierre)
                {
                    yield break;
                }

                yield return (cursor, inicio, fin);

                var siguiente = cursor.AddMinutes(intervalo);
                // Evita loop infinito si AddMinutes envuelve el día.
                if (siguiente <= cursor)
                {
                    yield break;
                }

                cursor = siguiente;
            }
        }

        /// <summary>
        /// Carga la configuración y su jornada semanal en UNA pasada. Devolverlas juntas evita que
        /// cada método vuelva a consultar la base y, sobre todo, que alguno resuelva la jornada por
        /// su cuenta: <see cref="BookingWeeklySchedule"/> es el único lugar donde vive esa regla.
        /// </summary>
        private async Task<(TenantBookingSettings? Settings, BookingWeeklySchedule Schedule)> LoadScheduleAsync(
            CancellationToken cancellationToken)
        {
            var settings = await _context.TenantBookingSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);

            var horarios = await _context.TenantBookingBusinessHours
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            return (settings, BookingWeeklySchedule.From(settings, horarios));
        }

        private static int? FindFreeCandidate(
            Dictionary<int, List<BusyInterval>> busyByFuncionario,
            IReadOnlyList<int> candidatos,
            DateTime inicio,
            DateTime fin)
        {
            foreach (var candidato in candidatos)
            {
                if (!Solapa(busyByFuncionario, candidato, inicio, fin))
                {
                    return candidato;
                }
            }

            return null;
        }

        private async Task<int?> ResolveServicioDuracionAsync(int servicioId, CancellationToken cancellationToken)
        {
            if (servicioId <= 0)
            {
                return null;
            }

            var servicio = await _context.Servicios
                .AsNoTracking()
                .Where(s => s.Id == servicioId && s.Activo)
                .Select(s => new { s.DuracionMinutos })
                .SingleOrDefaultAsync(cancellationToken);

            if (servicio is null)
            {
                return null;
            }

            return servicio.DuracionMinutos ?? CalendarCommandService.DefaultDurationMinutes;
        }

        private async Task<IReadOnlyList<int>> ResolveCandidatosAsync(
            int servicioId,
            int? funcionarioId,
            CancellationToken cancellationToken)
        {
            // Funcionarios que PUEDEN atender el servicio (activos + relación servicio-funcionario,
            // con fallback a todos los activos si no hay configuración explícita).
            var compatibles = await _catalogService.GetCompatibleFuncionarioIdsAsync(servicioId, cancellationToken);
            if (compatibles.Count == 0)
            {
                return Array.Empty<int>();
            }

            if (funcionarioId.HasValue && funcionarioId.Value > 0)
            {
                // Solo válido si el funcionario elegido puede atender ESTE servicio.
                return compatibles.Contains(funcionarioId.Value)
                    ? new[] { funcionarioId.Value }
                    : Array.Empty<int>();
            }

            return compatibles;
        }

        private Task<Dictionary<int, List<BusyInterval>>> LoadBusyIntervalsAsync(
            IReadOnlyList<int> funcionarioIds,
            DateOnly fecha,
            CancellationToken cancellationToken) =>
            LoadBusyIntervalsRangeAsync(funcionarioIds, fecha, fecha, cancellationToken);

        /// <summary>
        /// Ocupación del rango desde la ÚNICA fuente de disponibilidad: incluye citas, descansos y
        /// bloqueos recurrentes. Así una reserva pública nunca puede caer en el almuerzo del equipo.
        /// </summary>
        private Task<Dictionary<int, List<BusyInterval>>> LoadBusyIntervalsRangeAsync(
            IReadOnlyList<int> funcionarioIds,
            DateOnly fechaInicio,
            DateOnly fechaFin,
            CancellationToken cancellationToken) =>
            _availabilityService.GetBusyIntervalsAsync(
                funcionarioIds,
                fechaInicio,
                fechaFin,
                excludeCitaId: null,
                cancellationToken);

        private static bool EstaLibre(
            Dictionary<int, List<BusyInterval>> busyByFuncionario,
            IReadOnlyList<int> candidatos,
            DateTime inicio,
            DateTime fin)
        {
            foreach (var candidato in candidatos)
            {
                if (!Solapa(busyByFuncionario, candidato, inicio, fin))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Solapa(
            Dictionary<int, List<BusyInterval>> busyByFuncionario,
            int funcionarioId,
            DateTime inicio,
            DateTime fin)
        {
            if (!busyByFuncionario.TryGetValue(funcionarioId, out var ocupados))
            {
                return false;
            }

            return ocupados.Any(intervalo => intervalo.Solapa(inicio, fin));
        }
    }
}
