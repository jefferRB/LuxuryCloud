using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Estado de cuenta de un inversionista para un periodo.
    ///
    /// <para>
    /// Todos los montos son un SNAPSHOT: se calculan una vez y no vuelven a leerse de cobros,
    /// gastos ni configuración. Un estado <see cref="InvestorStatementStatus.Draft"/> puede
    /// recalcularse; a partir de <see cref="InvestorStatementStatus.Finalized"/> los valores
    /// quedan congelados y editar un cobro histórico NO los cambia. Las correcciones se hacen
    /// con un ajuste auditado, una anulación o una reapertura explícita.
    /// </para>
    /// </summary>
    public class InvestorStatement : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int InvestorId { get; set; }

        public TenantInvestor? Investor { get; set; }

        /// <summary>Acuerdo vigente usado para calcular. Puede quedar null si el acuerdo se borra.</summary>
        public int? AgreementId { get; set; }

        public InvestorAgreement? Agreement { get; set; }

        // ─────────────── Periodo ───────────────

        public DateOnly PeriodoInicio { get; set; }

        public DateOnly PeriodoFin { get; set; }

        public InvestorPayoutFrequency Frecuencia { get; set; }

        /// <summary>
        /// Día de corte del acuerdo en el momento del cálculo. Null = mes calendario.
        /// Queda congelado igual que el porcentaje: cambiar el corte después NO reescribe este
        /// estado, y así el documento sigue explicando con qué regla se armó su periodo.
        /// </summary>
        public int? DiaCorte { get; set; }

        /// <summary>
        /// Fecha en que cerró el periodo. Es siempre <see cref="PeriodoFin"/>, pero se guarda
        /// explícita porque es el dato que el inversionista reconoce ("mi corte del 20 de agosto")
        /// y el que viaja al correo y al PDF.
        /// </summary>
        public DateOnly FechaCorte { get; set; }

        // ─────────────── Snapshot del cálculo ───────────────

        /// <summary>Ingresos realmente cobrados en el periodo, con IVA incluido.</summary>
        public decimal IngresosCobrados { get; set; }

        /// <summary>IVA contenido en esos ingresos y excluido del cálculo.</summary>
        public decimal IvaExcluido { get; set; }

        /// <summary>Ingresos cobrados sin IVA. Es el punto de partida de la fórmula.</summary>
        public decimal IngresosNetos { get; set; }

        /// <summary>Gastos operativos elegibles según la política.</summary>
        public decimal GastosElegibles { get; set; }

        /// <summary>Liquidaciones/comisiones de colaboradores del periodo.</summary>
        public decimal Liquidaciones { get; set; }

        public decimal AjustesPositivos { get; set; }

        public decimal AjustesNegativos { get; set; }

        /// <summary>Pérdida de periodos anteriores aplicada contra la ganancia de este periodo.</summary>
        public decimal PerdidaArrastrada { get; set; }

        /// <summary>Pérdida que queda pendiente al cerrar este periodo (solo con arrastre activo).</summary>
        public decimal PerdidaPendiente { get; set; }

        /// <summary>Ganancia distribuible. Nunca negativa: una pérdida deja este valor en cero.</summary>
        public decimal GananciaDistribuible { get; set; }

        /// <summary>Porcentaje congelado del acuerdo en el momento del cálculo.</summary>
        public decimal ParticipacionPorcentaje { get; set; }

        /// <summary>Participación del inversionista = ganancia distribuible × porcentaje.</summary>
        public decimal ParticipacionCalculada { get; set; }

        // ─────────────── Pagos ───────────────

        public decimal TotalPagado { get; set; }

        public decimal SaldoPendiente { get; set; }

        // ─────────────── Trazabilidad ───────────────

        public InvestorStatementStatus Estado { get; set; } = InvestorStatementStatus.Draft;

        public DateTime FechaCalculoUtc { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? GeneradoPorUserId { get; set; }

        /// <summary>Descripción de la política usada. Ver <c>InvestorProfitPolicy.BuildVersionDescription</c>.</summary>
        [MaxLength(300)]
        public string PoliticaVersion { get; set; } = InvestorDefaults.PolicyVersion;

        public DateTime? FinalizadoAtUtc { get; set; }

        [MaxLength(450)]
        public string? FinalizadoPorUserId { get; set; }

        public DateTime? EnviadoAtUtc { get; set; }

        public DateTime? AnuladoAtUtc { get; set; }

        [MaxLength(450)]
        public string? AnuladoPorUserId { get; set; }

        [MaxLength(500)]
        public string? MotivoAnulacion { get; set; }

        public DateTime? ReabiertoAtUtc { get; set; }

        [MaxLength(450)]
        public string? ReabiertoPorUserId { get; set; }

        [MaxLength(500)]
        public string? MotivoReapertura { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public ICollection<InvestorStatementAdjustment> Ajustes { get; set; } = new List<InvestorStatementAdjustment>();

        public ICollection<InvestorDistributionPayment> Pagos { get; set; } = new List<InvestorDistributionPayment>();

        /// <summary>Solo un borrador puede recalcularse o recibir ajustes.</summary>
        public bool EsEditable => Estado == InvestorStatementStatus.Draft;

        /// <summary>Estados que ya congelaron el snapshot y admiten pagos.</summary>
        public bool AdmitePagos =>
            Estado is InvestorStatementStatus.Finalized
                or InvestorStatementStatus.Sent
                or InvestorStatementStatus.PartiallyPaid
                or InvestorStatementStatus.Paid;

        public bool EstaAnulado => Estado == InvestorStatementStatus.Voided;

        public string EstadoTexto => Estado switch
        {
            InvestorStatementStatus.Draft => "Borrador",
            InvestorStatementStatus.Finalized => "Finalizado",
            InvestorStatementStatus.Sent => "Enviado",
            InvestorStatementStatus.PartiallyPaid => "Pago parcial",
            InvestorStatementStatus.Paid => "Pagado",
            InvestorStatementStatus.Voided => "Anulado",
            _ => Estado.ToString()
        };
    }
}
