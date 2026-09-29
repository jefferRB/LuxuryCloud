using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Política de cálculo de la ganancia distribuible del tenant (una fila por negocio).
    ///
    /// <para>Fórmula (ver <c>InvestorProfitCalculationService</c>):</para>
    /// <code>
    /// Ingresos cobrados sin IVA
    ///   − gastos operativos elegibles
    ///   − liquidaciones de colaboradores
    ///   ± ajustes autorizados
    ///   − pérdida arrastrada
    ///   = ganancia distribuible
    /// </code>
    /// </summary>
    public class InvestorProfitPolicy : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        /// <summary>
        /// Excluir el IVA de los ingresos usando el motor fiscal existente. Default true:
        /// el IVA no es del negocio, es del fisco, así que no forma parte de la ganancia.
        /// </summary>
        [Display(Name = "Excluir IVA de los ingresos")]
        public bool ExcluirIva { get; set; } = true;

        [Display(Name = "Incluir liquidaciones de colaboradores")]
        public bool IncluirLiquidaciones { get; set; } = true;

        [Display(Name = "Base de las liquidaciones")]
        public InvestorSettlementBasis BaseLiquidaciones { get; set; } = InvestorSettlementBasis.Devengado;

        [Display(Name = "Categorías de gasto")]
        public InvestorExpenseCategoryMode ModoCategoriasGasto { get; set; } = InvestorExpenseCategoryMode.Todas;

        [Display(Name = "Tratamiento de pérdidas por defecto")]
        public InvestorLossTreatment TratamientoPerdidasPorDefecto { get; set; } = InvestorLossTreatment.NoDistribution;

        [Display(Name = "Frecuencia por defecto")]
        public InvestorPayoutFrequency FrecuenciaPorDefecto { get; set; } = InvestorPayoutFrequency.Mensual;

        /// <summary>Generación automática del estado de cuenta al cerrar el periodo.</summary>
        [Display(Name = "Generar estados automáticamente")]
        public bool GeneracionAutomatica { get; set; }

        /// <summary>Envío automático del correo tras generar (requiere estado finalizado).</summary>
        [Display(Name = "Enviar automáticamente")]
        public bool EnvioAutomatico { get; set; }

        /// <summary>Días de espera tras el cierre del periodo antes de generar (0–15).</summary>
        [Range(0, 15)]
        [Display(Name = "Días de espera tras el cierre")]
        public int DiasEsperaGeneracion { get; set; } = 1;

        /// <summary>Hora local del negocio (America/Costa_Rica) en que corre la generación.</summary>
        [Range(0, 23)]
        [Display(Name = "Hora de generación")]
        public int HoraGeneracion { get; set; } = 8;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? UpdatedByUserId { get; set; }

        public ICollection<InvestorPolicyExpenseCategory> CategoriasSeleccionadas { get; set; } =
            new List<InvestorPolicyExpenseCategory>();

        /// <summary>Política por defecto en memoria cuando el tenant todavía no configuró nada.</summary>
        public static InvestorProfitPolicy CreateDefault(Guid tenantId) => new()
        {
            TenantId = tenantId
        };

        /// <summary>
        /// Interpretación ECONÓMICA del negocio: la que usan el Dashboard, el Resumen Ejecutivo
        /// Mensual y el KPI de participación. <b>No es configurable.</b>
        ///
        /// <para>
        /// Esta clase tiene dos lectores con preguntas distintas. El estado de cuenta pregunta
        /// "¿cuánto le toca al inversionista según el CONTRATO?", y ahí los interruptores de arriba
        /// son legítimos: un contrato puede pactar que se reparta solo sobre lo efectivamente
        /// pagado. El Dashboard pregunta otra cosa: "¿cuánto ganó el NEGOCIO este mes?", y eso es
        /// contabilidad, no un acuerdo entre partes.
        /// </para>
        ///
        /// <para>
        /// Mientras el Dashboard leyó la política contractual, un tenant con
        /// <see cref="InvestorSettlementBasis.Pagado"/> veía "Liquidaciones del equipo ₡0" cuando la
        /// semana todavía no se había pagado: el negocio se anotaba como ganancia una plata que ya
        /// le debía al colaborador. El costo lo crea la PRODUCCIÓN, no el pago; pagar solo mueve
        /// caja. Por eso acá el devengado se fija y no se negocia.
        /// </para>
        ///
        /// <para>Los otros tres valores se fijan por la misma razón:</para>
        /// <list type="bullet">
        ///   <item><see cref="ExcluirIva"/>: el IVA es del fisco. Si no se excluyera, el Dashboard
        ///   reportaría IVA ₡0 mientras Ingresos y el Excel muestran el IVA real.</item>
        ///   <item><see cref="IncluirLiquidaciones"/>: el trabajo del equipo es un costo del
        ///   negocio, siempre.</item>
        ///   <item><see cref="ModoCategoriasGasto"/>: "Costos y gastos del mes" son TODOS los
        ///   gastos reales. Excluir categorías es una decisión de reparto, no de rentabilidad.</item>
        /// </list>
        /// </summary>
        public static InvestorProfitPolicy CreateForBusinessResult() => new()
        {
            ExcluirIva = true,
            IncluirLiquidaciones = true,
            BaseLiquidaciones = InvestorSettlementBasis.Devengado,
            ModoCategoriasGasto = InvestorExpenseCategoryMode.Todas
        };

        /// <summary>Descripción corta de la fórmula, congelada en cada snapshot.</summary>
        public string BuildVersionDescription()
        {
            var partes = new List<string>
            {
                InvestorDefaults.PolicyVersion,
                ExcluirIva ? "sin IVA" : "con IVA",
                IncluirLiquidaciones
                    ? (BaseLiquidaciones == InvestorSettlementBasis.Pagado ? "liquidaciones pagadas" : "liquidaciones devengadas")
                    : "sin liquidaciones",
                ModoCategoriasGasto switch
                {
                    InvestorExpenseCategoryMode.SoloSeleccionadas => "gastos: solo seleccionados",
                    InvestorExpenseCategoryMode.TodasExceptoSeleccionadas => "gastos: todos excepto seleccionados",
                    _ => "gastos: todos"
                }
            };

            return string.Join(" · ", partes);
        }
    }

    /// <summary>
    /// Categoría de egreso incluida o excluida del cálculo, según
    /// <see cref="InvestorProfitPolicy.ModoCategoriasGasto"/>.
    /// </summary>
    public class InvestorPolicyExpenseCategory : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int PolicyId { get; set; }

        public InvestorProfitPolicy? Policy { get; set; }

        public int CategoriaId { get; set; }

        public Finanzas.Categoria? Categoria { get; set; }
    }
}
