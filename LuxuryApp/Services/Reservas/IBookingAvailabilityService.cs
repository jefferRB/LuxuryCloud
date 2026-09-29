namespace LuxuryApp.Services.Reservas
{
    /// <summary>
    /// Resultado de resolver un horario puntual (al confirmar una solicitud o al crear una).
    /// </summary>
    public sealed class SlotResolution
    {
        public bool Disponible { get; init; }

        /// <summary>Funcionario asignado al slot. Cuando se pidió "cualquiera", es el primero libre.</summary>
        public int? FuncionarioId { get; init; }

        public int DuracionMinutos { get; init; }

        public string? Motivo { get; init; }

        public static SlotResolution NoDisponible(string motivo) =>
            new() { Disponible = false, Motivo = motivo };
    }

    /// <summary>
    /// Sugerencia de próximo espacio disponible: fecha, hora de inicio y funcionario que atendería.
    /// </summary>
    public sealed record AvailableSlotSuggestion(DateOnly Fecha, TimeOnly Hora, int FuncionarioId);

    /// <summary>
    /// Cálculo de disponibilidad pública. Todas las operaciones son tenant-scoped: dependen del
    /// tenant ya resuelto en el contexto (global query filter). Validan jornada, anticipación,
    /// días máximos, citas/descansos existentes y funcionarios activos.
    /// </summary>
    public interface IBookingAvailabilityService
    {
        /// <summary>
        /// Horas (HH:mm) disponibles para un servicio en una fecha, opcionalmente para un
        /// funcionario concreto. Si funcionarioId es null, un slot está disponible si AL MENOS
        /// un funcionario activo lo tiene libre.
        /// </summary>
        Task<IReadOnlyList<string>> GetAvailableSlotsAsync(
            int servicioId,
            DateOnly fecha,
            int? funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resuelve un slot puntual: valida solapamiento y, si funcionarioId es null, asigna el
        /// primer funcionario activo disponible. Se usa al crear y al confirmar la solicitud.
        /// </summary>
        Task<SlotResolution> ResolveSlotAsync(
            int servicioId,
            DateTime inicio,
            int? funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Busca los próximos espacios disponibles desde <paramref name="fromDate"/> hacia adelante,
        /// respetando jornada, días laborales, anticipación mínima, máximo de días y la relación
        /// servicio-funcionario. Acotada: barre el horizonte por ventanas (una sola consulta de
        /// ocupación por ventana, nunca una por día) y corta apenas junta las sugerencias pedidas.
        /// Devuelve como máximo <paramref name="maxSuggestions"/> resultados, con un tope duro
        /// interno para que el llamador no pueda pedir un barrido arbitrario.
        /// </summary>
        Task<IReadOnlyList<AvailableSlotSuggestion>> GetNextAvailableSlotsAsync(
            int servicioId,
            DateOnly fromDate,
            int? funcionarioId,
            int maxSuggestions = 5,
            CancellationToken cancellationToken = default);
    }
}
