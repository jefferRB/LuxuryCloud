using LuxuryApp.Models.Inversionistas;

namespace LuxuryApp.Services.Finanzas
{
    /// <summary>
    /// Desglose financiero del negocio para un rango de fechas. Es el resultado de la ÚNICA
    /// fórmula de ganancia que existe en LuxuryCloud.
    ///
    /// <code>
    /// TotalCobrado − IvaCobrado          = IngresosNetos
    /// IngresosNetos − GastosOperativos
    ///               − LiquidacionesEquipo
    ///               + AjustesAutorizados = GananciaDistribuible
    /// </code>
    /// </summary>
    public sealed record PeriodProfitBreakdown
    {
        public DateOnly PeriodoInicio { get; init; }

        public DateOnly PeriodoFin { get; init; }

        /// <summary>Dinero realmente cobrado en el rango, con IVA incluido.</summary>
        public decimal TotalCobrado { get; init; }

        /// <summary>IVA contenido en esos cobros. Cero si la política no excluye IVA.</summary>
        public decimal IvaCobrado { get; init; }

        /// <summary>Ingresos base del cálculo (sin IVA si la política lo excluye).</summary>
        public decimal IngresosNetos { get; init; }

        /// <summary>Gastos operativos elegibles según la política.</summary>
        public decimal GastosOperativos { get; init; }

        /// <summary>Liquidaciones/comisiones del equipo. Cero si la política las excluye.</summary>
        public decimal LiquidacionesEquipo { get; init; }

        /// <summary>
        /// Ajustes autorizados que corrigen la ganancia DEL NEGOCIO en este rango.
        ///
        /// <para>
        /// Hoy siempre vale cero: los únicos ajustes que existen en el sistema son los de un estado
        /// de cuenta concreto (<c>InvestorStatementAdjustment</c>), que corrigen lo que se le paga a
        /// UN inversionista y por definición no cambian la ganancia del negocio. Esos se aplican
        /// después, sobre <see cref="GananciaDistribuible"/>, y por eso no aparecen acá ni alteran
        /// el Dashboard. El campo existe para que, el día que se agreguen ajustes de negocio, entren
        /// por este único lugar y los vean los dos consumidores a la vez.
        /// </para>
        /// </summary>
        public decimal AjustesAutorizados { get; init; }

        /// <summary>
        /// Ganancia distribuible del rango. Puede ser negativa: es el resultado real del negocio.
        /// Quien reparte decide qué hacer con una pérdida (no se arrastra o se descuenta después).
        /// </summary>
        public decimal GananciaDistribuible { get; init; }

        /// <summary>Descripción de la política usada, para congelar en el snapshot.</summary>
        public string PoliticaVersion { get; init; } = InvestorDefaults.PolicyVersion;

        /// <summary>Desglose de gastos por categoría, informativo para las pantallas.</summary>
        public IReadOnlyList<PeriodExpenseCategoryBreakdown> GastosPorCategoria { get; init; } =
            Array.Empty<PeriodExpenseCategoryBreakdown>();
    }

    public sealed record PeriodExpenseCategoryBreakdown(
        int CategoriaId,
        string CategoriaNombre,
        decimal Monto,
        bool Incluido,
        string? MotivoExclusion);

    /// <summary>
    /// Motor ÚNICO de la ganancia del negocio por rango de fechas.
    ///
    /// <para>
    /// Invariante que sostiene este servicio: <b>mismo tenant + mismo rango + mismos datos ⇒ mismo
    /// resultado</b>, sin importar quién pregunte. Lo consumen el Dashboard financiero (mes
    /// calendario) y los estados de cuenta de inversionistas (periodo contractual con día de
    /// corte). Los rangos pueden ser distintos; la fórmula no.
    /// </para>
    ///
    /// <para>
    /// No recalcula nada por su cuenta: los ingresos, el IVA y las liquidaciones salen de
    /// <c>ILiquidacionSemanalService</c>, que aplica el motor fiscal línea por línea. Los gastos
    /// salen de Egresos con las exclusiones estructurales del dominio.
    /// </para>
    /// </summary>
    public interface IPeriodProfitCalculationService
    {
        Task<PeriodProfitBreakdown> CalculateAsync(
            DateOnly periodoInicio,
            DateOnly periodoFin,
            InvestorProfitPolicy policy,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Los doce meses calendario de un año, indexados por número de mes (1–12).
        ///
        /// <para>
        /// Existe para que el Dashboard tenga UNA sola llamada y el titular del mes y las barras
        /// del gráfico salgan del mismo cálculo (antes el gráfico usaba una aritmética distinta y
        /// podía contradecir al número grande de arriba).
        /// </para>
        ///
        /// <para>
        /// Hoy resuelve mes por mes. Es el punto donde optimizar si hiciera falta: una sola pasada
        /// por el año beneficiaría a todos los consumidores sin cambiarles el contrato.
        /// </para>
        /// </summary>
        Task<IReadOnlyDictionary<int, PeriodProfitBreakdown>> CalculateMonthlyAsync(
            int anio,
            InvestorProfitPolicy policy,
            CancellationToken cancellationToken = default);
    }
}
