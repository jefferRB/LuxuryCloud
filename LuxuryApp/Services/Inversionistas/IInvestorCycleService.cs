using LuxuryApp.Models.Inversionistas;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Lectura operacional del inversionista. Separa tres cosas que ANTES se mezclaban en la
    /// pantalla y por eso mentían:
    ///
    /// <list type="number">
    ///   <item><b>Ciclo actual</b>: período abierto, cálculo live, puede cambiar, NO es deuda.</item>
    ///   <item><b>Corte emitido</b>: <c>InvestorStatement</c> real, snapshot congelado, define
    ///   cuánto le correspondió.</item>
    ///   <item><b>Saldo pendiente</b>: solo lo que sale de cortes emitidos. La estimación del
    ///   ciclo en curso NUNCA entra acá.</item>
    /// </list>
    ///
    /// <para>
    /// Distinción que este servicio existe para respetar: <c>PreviousClosedPeriod</c> del resolver
    /// es una fecha TEÓRICA (aritmética de calendario) y no equivale a "último corte". Si no hay
    /// estado emitido, no hay último corte: hay un PRIMER corte por venir.
    /// </para>
    ///
    /// <para>No calcula dinero por su cuenta: reutiliza <c>IPeriodProfitCalculationService</c>.</para>
    /// </summary>
    public interface IInvestorCycleService
    {
        /// <summary>
        /// Resumen completo (último corte + ciclo actual + saldo + cortes recientes). Null si el
        /// inversionista no existe o no pertenece al tenant.
        /// </summary>
        Task<InvestorFinancialSummaryViewModel?> BuildSummaryAsync(
            int investorId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Ciclo en curso con su desglose completo, para la pantalla "Ciclo en curso".
        /// Null si el inversionista no existe.
        /// </summary>
        Task<InvestorCurrentCycleViewModel?> BuildCurrentCycleAsync(
            int investorId,
            CancellationToken cancellationToken = default);
    }
}
