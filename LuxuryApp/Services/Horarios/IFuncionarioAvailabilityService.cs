using LuxuryApp.Models.Horarios;

namespace LuxuryApp.Services.Horarios
{
    /// <summary>Origen de un intervalo ocupado. Estable: viaja en respuestas JSON.</summary>
    public static class BusyIntervalSources
    {
        public const string Cita = "CITA";
        public const string Descanso = "DESCANSO";
        public const string BloqueoRecurrente = "BLOQUEO_RECURRENTE";

        /// <summary>
        /// Solicitud de reserva online en estado Pending. Ocupa agenda exactamente igual que una
        /// cita: mientras el negocio no la confirme o rechace, nadie más puede tomar ese intervalo.
        /// </summary>
        public const string SolicitudPendiente = "SOLICITUD_PENDIENTE";
    }

    /// <summary>
    /// Intervalo en el que un colaborador NO está disponible. Las horas son locales del negocio,
    /// igual que <c>Cita.FechaHoraCita</c>.
    /// </summary>
    public sealed record BusyInterval(
        DateTime Inicio,
        DateTime Fin,
        string Origen,
        string? Titulo = null,
        int? ReferenciaId = null)
    {
        public bool Solapa(DateTime inicio, DateTime fin) =>
            TimeIntervalMath.Overlaps(Inicio, Fin, inicio, fin);

        public bool EsBloqueoRecurrente => Origen == BusyIntervalSources.BloqueoRecurrente;

        public bool EsSolicitudPendiente => Origen == BusyIntervalSources.SolicitudPendiente;
    }

    /// <summary>Resultado de comprobar un horario concreto.</summary>
    public sealed record AvailabilityCheckResult(bool Disponible, string? Motivo, BusyInterval? Conflicto)
    {
        public static AvailabilityCheckResult Libre() => new(true, null, null);

        public static AvailabilityCheckResult Ocupado(string motivo, BusyInterval conflicto) =>
            new(false, motivo, conflicto);
    }

    /// <summary>
    /// Conflicto de UN colaborador en UNA fecha, descrito en datos y no en prosa: quién, cuándo y
    /// qué tipo de bloque. Deliberadamente NO viaja el cliente ni el servicio: para explicar por
    /// qué un día no se puede elegir basta el colaborador y el rango horario.
    /// </summary>
    public sealed record ScheduleConflict(
        DateOnly Fecha,
        int FuncionarioId,
        string FuncionarioNombre,
        DateTime Inicio,
        DateTime Fin,
        string Tipo);

    /// <summary>
    /// Disponibilidad de una fecha para TODO el conjunto de colaboradores consultado. Una fecha
    /// solo está disponible si el intervalo completo lo está para todos: basta un conflicto para
    /// descartarla.
    /// </summary>
    public sealed record DateAvailabilityResult(
        DateOnly Fecha,
        bool Disponible,
        IReadOnlyList<ScheduleConflict> Conflictos);

    /// <summary>
    /// Fuente ÚNICA de disponibilidad de colaboradores. Combina citas/descansos, bloqueos
    /// recurrentes y solicitudes de reserva online pendientes, para que reservas públicas,
    /// creación manual, reprogramación, búsqueda de espacios y calendario respondan exactamente
    /// lo mismo.
    ///
    /// <para>
    /// Antes de este servicio la validación de solapamiento vivía duplicada en
    /// <c>CalendarCommandService</c> y <c>BookingAvailabilityService</c>. Ahora ambas la consumen.
    /// </para>
    /// </summary>
    public interface IFuncionarioAvailabilityService
    {
        /// <summary>
        /// Intervalos ocupados por colaborador en el rango [desde, hasta] (ambos inclusive).
        /// Una sola consulta por origen: no hay N+1.
        /// </summary>
        Task<Dictionary<int, List<BusyInterval>>> GetBusyIntervalsAsync(
            IReadOnlyCollection<int> funcionarioIds,
            DateOnly desde,
            DateOnly hasta,
            int? excludeCitaId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Comprueba un horario concreto de un colaborador. Es el punto que usan la creación de
        /// citas, la edición, el movimiento y el cambio de duración.
        /// </summary>
        Task<AvailabilityCheckResult> CheckAsync(
            int funcionarioId,
            DateTime inicio,
            int duracionMinutos,
            int? excludeCitaId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Evalúa la matriz <c>fechas × funcionarios</c> con el MISMO criterio de solapamiento que
        /// <see cref="CheckAsync"/>, en una sola pasada de consultas (no hay una query por celda).
        /// Es lo que consume la vista previa de fechas repetidas y la revalidación del guardado.
        /// </summary>
        /// <param name="hora">Hora local de inicio, idéntica en todas las fechas.</param>
        Task<IReadOnlyList<DateAvailabilityResult>> CheckManyAsync(
            IReadOnlyCollection<int> funcionarioIds,
            IReadOnlyCollection<DateOnly> fechas,
            TimeOnly hora,
            int duracionMinutos,
            int? excludeCitaId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Ocurrencias de bloqueos recurrentes del rango, para pintarlas en el calendario.
        /// Si <paramref name="funcionarioIds"/> es null se usan todos los colaboradores activos.
        /// </summary>
        Task<IReadOnlyList<RecurringScheduleOccurrence>> GetRecurringBlocksAsync(
            DateOnly desde,
            DateOnly hasta,
            IReadOnlyCollection<int>? funcionarioIds = null,
            CancellationToken cancellationToken = default);
    }
}
