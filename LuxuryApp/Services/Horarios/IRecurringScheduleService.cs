using LuxuryApp.Models.Horarios;

namespace LuxuryApp.Services.Horarios
{
    /// <summary>
    /// Error de negocio del módulo de bloqueos recurrentes, con mensaje apto para el usuario.
    /// </summary>
    public sealed class RecurringScheduleValidationException : Exception
    {
        public RecurringScheduleValidationException(string message, string? modelStateKey = null)
            : base(message)
        {
            ModelStateKey = modelStateKey;
        }

        public string? ModelStateKey { get; }
    }

    /// <summary>Resultado de guardar una regla.</summary>
    public sealed record RecurringScheduleSaveResult(
        int RuleId,
        bool RequiereConfirmacion,
        RecurringScheduleConflictSummaryViewModel Conflictos)
    {
        public static RecurringScheduleSaveResult NeedsConfirmation(RecurringScheduleConflictSummaryViewModel conflictos) =>
            new(0, true, conflictos);

        public static RecurringScheduleSaveResult Saved(int ruleId, RecurringScheduleConflictSummaryViewModel conflictos) =>
            new(ruleId, false, conflictos);
    }

    /// <summary>
    /// Gestión de reglas recurrentes de indisponibilidad y sus excepciones.
    /// La disponibilidad efectiva la resuelve <see cref="IFuncionarioAvailabilityService"/>.
    /// </summary>
    public interface IRecurringScheduleService
    {
        Task<RecurringSchedulePageViewModel> BuildPageAsync(CancellationToken cancellationToken = default);

        Task<RecurringScheduleRuleFormViewModel> BuildCreateFormAsync(CancellationToken cancellationToken = default);

        Task<RecurringScheduleRuleFormViewModel?> BuildEditFormAsync(int ruleId, CancellationToken cancellationToken = default);

        Task<RecurringScheduleRuleDetailViewModel?> BuildDetailAsync(int ruleId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Citas existentes que coinciden con las futuras ocurrencias de la regla descrita.
        /// Nunca modifica nada: solo informa.
        /// </summary>
        Task<RecurringScheduleConflictSummaryViewModel> DetectConflictsAsync(
            RecurringScheduleRuleFormViewModel form,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Crea la regla. Si hay citas en conflicto y el usuario no confirmó, devuelve
        /// <c>RequiereConfirmacion = true</c> sin guardar nada.
        /// </summary>
        Task<RecurringScheduleSaveResult> CreateAsync(
            RecurringScheduleRuleFormViewModel form,
            string? userId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Actualiza la regla SOLO hacia el futuro. Si la regla ya estuvo vigente y cambian horario,
        /// días o alcance, cierra la versión anterior y crea una nueva desde la fecha efectiva.
        /// </summary>
        Task<RecurringScheduleSaveResult> UpdateAsync(
            int ruleId,
            RecurringScheduleRuleFormViewModel form,
            string? userId,
            CancellationToken cancellationToken = default);

        Task SetActivaAsync(int ruleId, bool activa, string? userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Baja lógica: pone fecha final (hoy) y desactiva. No se borra la fila para conservar la
        /// trazabilidad de por qué una agenda estuvo bloqueada en el pasado.
        /// </summary>
        Task EndAsync(int ruleId, string? userId, CancellationToken cancellationToken = default);

        Task AddExceptionAsync(
            RecurringScheduleExceptionFormViewModel form,
            string? userId,
            CancellationToken cancellationToken = default);

        Task RemoveExceptionAsync(int exceptionId, string? userId, CancellationToken cancellationToken = default);
    }
}
