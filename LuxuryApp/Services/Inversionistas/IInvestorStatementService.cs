using LuxuryApp.Models.Inversionistas;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>Filtros del listado de estados de cuenta.</summary>
    public sealed record InvestorStatementFilter
    {
        public int? InvestorId { get; init; }

        public InvestorStatementStatus? Estado { get; init; }

        public DateOnly? Desde { get; init; }

        public DateOnly? Hasta { get; init; }
    }

    /// <summary>
    /// Ciclo de vida del estado de cuenta: vista previa, generación idempotente, recálculo del
    /// borrador, finalización (congela el snapshot), ajustes, pagos, anulación y reapertura.
    /// </summary>
    public interface IInvestorStatementService
    {
        /// <summary>
        /// Vista previa del cálculo de un periodo SIN persistir nada. Muestra el desglose completo
        /// para que el usuario vea qué reduce la ganancia antes de generar.
        /// </summary>
        Task<InvestorStatementPreviewViewModel> PreviewAsync(
            int investorId,
            DateOnly? referencia,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Crea el borrador del periodo. Idempotente: si ya existe un estado vivo para ese
        /// inversionista y periodo devuelve el existente en lugar de duplicarlo.
        /// </summary>
        Task<int> GenerateDraftAsync(
            int investorId,
            DateOnly referencia,
            string? userId,
            CancellationToken cancellationToken = default);

        /// <summary>Recalcula un borrador contra los datos actuales. Falla si ya está finalizado.</summary>
        Task RecalculateAsync(int statementId, string? userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Congela el snapshot. A partir de acá editar cobros, gastos o configuración histórica
        /// NO cambia los montos. Protegido contra doble finalización concurrente.
        /// </summary>
        Task FinalizeAsync(int statementId, string? userId, CancellationToken cancellationToken = default);

        Task VoidAsync(int statementId, string motivo, string? userId, CancellationToken cancellationToken = default);

        /// <summary>Reapertura explícita de un estado finalizado. Exige motivo y queda auditada.</summary>
        Task ReopenAsync(int statementId, string motivo, string? userId, CancellationToken cancellationToken = default);

        Task AddAdjustmentAsync(
            InvestorAdjustmentFormViewModel form,
            string? userId,
            string? userEmail,
            CancellationToken cancellationToken = default);

        Task RemoveAdjustmentAsync(int adjustmentId, string? userId, CancellationToken cancellationToken = default);

        Task RegisterPaymentAsync(
            InvestorPaymentFormViewModel form,
            string? userId,
            string? userEmail,
            CancellationToken cancellationToken = default);

        /// <summary>Corrige un pago mal registrado creando un movimiento compensatorio auditado.</summary>
        Task ReversePaymentAsync(
            int paymentId,
            string motivo,
            string? userId,
            string? userEmail,
            CancellationToken cancellationToken = default);

        Task<InvestorStatementsPageViewModel> BuildStatementsPageAsync(
            InvestorStatementFilter filter,
            CancellationToken cancellationToken = default);

        Task<InvestorStatementDetailViewModel?> BuildDetailAsync(
            int statementId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Pérdida que arrastra el estado anterior al periodo indicado, según el tratamiento del
        /// acuerdo vigente. Es la ÚNICA lectura de "pérdida previa" del sistema: la usan tanto el
        /// snapshot como la estimación del ciclo en curso, para que los dos cuenten lo mismo.
        /// </summary>
        Task<decimal> GetCarryForwardLossAsync(
            int investorId,
            DateOnly periodoInicio,
            InvestorAgreement? acuerdo,
            CancellationToken cancellationToken = default);

        /// <summary>Marca el estado como enviado tras un envío real exitoso.</summary>
        Task MarkAsSentAsync(int statementId, CancellationToken cancellationToken = default);
    }
}
