using LuxuryApp.Models.Asociados;

namespace LuxuryApp.Services.Asociados
{
    /// <summary>
    /// Participación de los asociados sobre la ganancia del negocio, para mostrarla en el
    /// Dashboard financiero.
    ///
    /// <para>
    /// FUENTE ÚNICA: la ganancia distribuible sale de
    /// <c>IPeriodProfitCalculationService</c>, el mismo motor que alimenta los estados de cuenta
    /// (que a su vez reutiliza <c>ILiquidacionSemanalService</c> y el motor fiscal). Acá NO se
    /// recalcula ingresos, IVA, gastos ni liquidaciones: solo se aplica el porcentaje vigente.
    /// </para>
    ///
    /// <para>
    /// El KPI existe únicamente cuando hay al menos una participación vigente en el periodo. Si no
    /// la hay, se devuelve <c>null</c> y el cálculo caro ni siquiera se ejecuta.
    /// </para>
    /// </summary>
    public interface IAssociateProfitAllocationService
    {
        /// <summary>
        /// KPI del mes indicado, o null si no hay participaciones vigentes en ese periodo.
        /// El llamador debe verificar el permiso financiero ANTES de invocarlo: este servicio
        /// devuelve dinero real del negocio.
        /// </summary>
        Task<AssociateAllocationKpiViewModel?> BuildMonthlyKpiAsync(
            int mes,
            int anio,
            CancellationToken cancellationToken = default);
    }
}
