using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>Fila del listado de inversionistas.</summary>
    public sealed class InvestorListItemViewModel
    {
        public int Id { get; init; }

        public string Nombre { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public string? Telefono { get; init; }

        public bool Activo { get; init; }

        /// <summary>Porcentaje del acuerdo vigente hoy. Null si no tiene acuerdo vigente.</summary>
        public decimal? PorcentajeVigente { get; init; }

        public InvestorPayoutFrequency? Frecuencia { get; init; }

        public string FrecuenciaTexto =>
            Frecuencia is null ? "—" : InvestorPeriodCalculator.FrecuenciaTexto(Frecuencia.Value);

        /// <summary>Periodo que se reportará la próxima vez.</summary>
        public string? ProximoReporte { get; init; }

        public decimal SaldoPendiente { get; init; }

        public int EstadosPendientes { get; init; }

        public string EstadoTexto => Activo ? "Activo" : "Inactivo";
    }

    public sealed class InvestorsIndexViewModel
    {
        public IReadOnlyList<InvestorListItemViewModel> Inversionistas { get; init; } =
            Array.Empty<InvestorListItemViewModel>();

        public decimal ParticipacionTotalVigente { get; init; }

        public decimal SaldoPendienteTotal { get; init; }

        public InvestorPolicyViewModel Politica { get; init; } = new();

        public bool TieneInversionistas => Inversionistas.Count > 0;
    }

    /// <summary>Alta/edición de un inversionista junto con su acuerdo vigente.</summary>
    public sealed class InvestorFormViewModel
    {
        public int? Id { get; set; }

        [Required(ErrorMessage = "Indicá el nombre del inversionista.")]
        [StringLength(150)]
        [Display(Name = "Nombre")]
        public string Nombre { get; set; } = string.Empty;

        [Required(ErrorMessage = "Indicá el correo del inversionista.")]
        [EmailAddress(ErrorMessage = "El correo no tiene un formato válido.")]
        [StringLength(256)]
        [Display(Name = "Correo electrónico")]
        public string Email { get; set; } = string.Empty;

        [StringLength(30)]
        [Display(Name = "Teléfono")]
        public string? Telefono { get; set; }

        [Display(Name = "Inversionista activo")]
        public bool Activo { get; set; } = true;

        [StringLength(1000)]
        [Display(Name = "Notas internas")]
        public string? NotasInternas { get; set; }

        // ─── Acuerdo de participación ───

        public int? AcuerdoId { get; set; }

        [Range(0.01, 100, ErrorMessage = "El porcentaje debe estar entre 0,01 y 100.")]
        [Display(Name = "Porcentaje de participación")]
        public decimal ParticipacionPorcentaje { get; set; } = 45m;

        [Display(Name = "Vigente desde")]
        [DataType(DataType.Date)]
        public DateTime EffectiveFrom { get; set; } = DateTime.Today;

        [Display(Name = "Vigente hasta (opcional)")]
        [DataType(DataType.Date)]
        public DateTime? EffectiveTo { get; set; }

        [Display(Name = "Frecuencia")]
        public InvestorPayoutFrequency Frecuencia { get; set; } = InvestorPayoutFrequency.Mensual;

        /// <summary>Día de corte para acuerdos mensuales. Null = mes calendario.</summary>
        [Range(1, 31, ErrorMessage = "El día de corte debe estar entre 1 y 31.")]
        [Display(Name = "Día de corte")]
        public int? DiaCorte { get; set; }

        [Display(Name = "Tratamiento de pérdidas")]
        public InvestorLossTreatment TratamientoPerdidas { get; set; } = InvestorLossTreatment.NoDistribution;

        [Display(Name = "Enviar el estado automáticamente")]
        public bool EnvioAutomatico { get; set; }

        [StringLength(1000)]
        [Display(Name = "Notas del acuerdo")]
        public string? Notas { get; set; }

        public bool EsEdicion => Id.HasValue;

        /// <summary>Suma de participaciones vigentes de OTROS inversionistas (ayuda contextual).</summary>
        public decimal ParticipacionOtros { get; set; }

        /// <summary>Primer día del próximo periodo válido para un cambio de porcentaje.</summary>
        public DateOnly? ProximoInicioPeriodo { get; set; }
    }

    /// <summary>Configuración de la fórmula de ganancia distribuible del negocio.</summary>
    public sealed class InvestorPolicyViewModel
    {
        [Display(Name = "Excluir IVA de los ingresos")]
        public bool ExcluirIva { get; set; } = true;

        [Display(Name = "Restar liquidaciones de colaboradores")]
        public bool IncluirLiquidaciones { get; set; } = true;

        [Display(Name = "Base de las liquidaciones")]
        public InvestorSettlementBasis BaseLiquidaciones { get; set; } = InvestorSettlementBasis.Devengado;

        [Display(Name = "Categorías de gasto")]
        public InvestorExpenseCategoryMode ModoCategoriasGasto { get; set; } = InvestorExpenseCategoryMode.Todas;

        public List<int> CategoriasSeleccionadas { get; set; } = new();

        [Display(Name = "Tratamiento de pérdidas por defecto")]
        public InvestorLossTreatment TratamientoPerdidasPorDefecto { get; set; } = InvestorLossTreatment.NoDistribution;

        [Display(Name = "Frecuencia por defecto")]
        public InvestorPayoutFrequency FrecuenciaPorDefecto { get; set; } = InvestorPayoutFrequency.Mensual;

        [Display(Name = "Generar estados automáticamente")]
        public bool GeneracionAutomatica { get; set; }

        [Display(Name = "Enviar automáticamente al generar")]
        public bool EnvioAutomatico { get; set; }

        [Range(0, 15)]
        [Display(Name = "Días de espera tras el cierre")]
        public int DiasEsperaGeneracion { get; set; } = 1;

        [Range(0, 23)]
        [Display(Name = "Hora de generación")]
        public int HoraGeneracion { get; set; } = 8;

        public IReadOnlyList<InvestorCategoriaOption> Categorias { get; set; } =
            Array.Empty<InvestorCategoriaOption>();
    }

    public sealed record InvestorCategoriaOption(int Id, string Nombre);

    /// <summary>Fila del listado de estados de cuenta.</summary>
    public sealed class InvestorStatementListItemViewModel
    {
        public int Id { get; init; }

        public int InvestorId { get; init; }

        public string InvestorNombre { get; init; } = string.Empty;

        public DateOnly PeriodoInicio { get; init; }

        public DateOnly PeriodoFin { get; init; }

        /// <summary>
        /// Fecha en que cerró el corte, congelada en el snapshot. Es el dato con el que el dueño
        /// reconoce el estado ("el corte del 20 de setiembre"), y por eso encabeza la tabla.
        /// </summary>
        public DateOnly FechaCorte { get; init; }

        public string PeriodoEtiqueta { get; init; } = string.Empty;

        public InvestorStatementStatus Estado { get; init; }

        public string EstadoTexto { get; init; } = string.Empty;

        /// <summary>Tono visual del estado. Ver <see cref="InvestorVisuals"/>.</summary>
        public InvestorVisualTone Tono => InvestorVisuals.Tone(Estado, SaldoPendiente);

        public decimal GananciaDistribuible { get; init; }

        public decimal ParticipacionPorcentaje { get; init; }

        public decimal ParticipacionCalculada { get; init; }

        public decimal TotalPagado { get; init; }

        public decimal SaldoPendiente { get; init; }

        public DateTime? EnviadoAtUtc { get; init; }
    }

    /// <summary>Página de estados de cuenta con sus filtros.</summary>
    public sealed class InvestorStatementsPageViewModel
    {
        public IReadOnlyList<InvestorStatementListItemViewModel> Estados { get; init; } =
            Array.Empty<InvestorStatementListItemViewModel>();

        public IReadOnlyList<InvestorCategoriaOption> Inversionistas { get; init; } =
            Array.Empty<InvestorCategoriaOption>();

        public int? FiltroInversionistaId { get; init; }

        public InvestorStatementStatus? FiltroEstado { get; init; }

        public DateOnly? FiltroDesde { get; init; }

        public DateOnly? FiltroHasta { get; init; }

        public decimal TotalParticipaciones { get; init; }

        public decimal TotalPagado { get; init; }

        public decimal TotalPendiente { get; init; }
    }

    /// <summary>
    /// Desglose visible del cálculo. Se usa tanto en la vista previa (antes de finalizar) como
    /// en el detalle de un estado ya congelado; en ambos casos se muestran TODOS los componentes,
    /// nunca un único número.
    /// </summary>
    public sealed class InvestorCalculationBreakdownViewModel
    {
        /// <summary>Nombre del inversionista, para poder redactar la explicación en su idioma.</summary>
        public string InvestorNombre { get; init; } = string.Empty;

        /// <summary>Rango exacto que se calculó. Es lo que hace verificable la explicación.</summary>
        public DateOnly PeriodoInicio { get; init; }

        public DateOnly PeriodoFin { get; init; }

        public decimal IngresosCobrados { get; init; }

        public decimal IvaExcluido { get; init; }

        public decimal IngresosNetos { get; init; }

        public decimal GastosElegibles { get; init; }

        public decimal Liquidaciones { get; init; }

        public decimal AjustesPositivos { get; init; }

        public decimal AjustesNegativos { get; init; }

        public decimal PerdidaArrastrada { get; init; }

        public decimal PerdidaPendiente { get; init; }

        public decimal GananciaDistribuible { get; init; }

        public decimal ParticipacionPorcentaje { get; init; }

        public decimal ParticipacionCalculada { get; init; }

        public string PoliticaVersion { get; init; } = string.Empty;

        public IReadOnlyList<InvestorExpenseLineViewModel> GastosPorCategoria { get; init; } =
            Array.Empty<InvestorExpenseLineViewModel>();

        /// <summary>True si el periodo cerró en pérdida.</summary>
        public bool EsPerdida => GananciaDistribuible <= 0m && (IngresosNetos - GastosElegibles - Liquidaciones) < 0m;

        /// <summary>Total de ajustes con signo, para la línea de costos.</summary>
        public decimal AjustesNetos => AjustesPositivos - AjustesNegativos;

        public bool TieneAjustes => AjustesPositivos > 0m || AjustesNegativos > 0m;

        /// <summary>Resultado operativo antes de ajustes y pérdidas: la línea que explica el resto.</summary>
        public decimal ResultadoOperativo => IngresosNetos - GastosElegibles - Liquidaciones;

        // ─── Trazabilidad ───
        // Cada componente enlaza a la pantalla donde están los movimientos reales del rango, para
        // que "¿de dónde salieron estos ₡10.000?" tenga respuesta en un clic. Se reutilizan las
        // rutas y los filtros que ya existen; no hay pantallas nuevas.

        public string RangoDesdeIso => PeriodoInicio.ToString("yyyy-MM-dd");

        public string RangoHastaIso => PeriodoFin.ToString("yyyy-MM-dd");

        // ─── Pagos (solo en el detalle de un estado ya emitido) ───

        /// <summary>Null en la vista previa: todavía no existe nada que pagar.</summary>
        public decimal? TotalPagado { get; init; }

        public decimal? SaldoPendiente { get; init; }

        public bool MuestraPagos => TotalPagado.HasValue;
    }

    public sealed record InvestorExpenseLineViewModel(
        string CategoriaNombre,
        decimal Monto,
        bool Incluido,
        string? MotivoExclusion);

    /// <summary>Vista previa de un periodo todavía no generado.</summary>
    public sealed class InvestorStatementPreviewViewModel
    {
        public int InvestorId { get; init; }

        public string InvestorNombre { get; init; } = string.Empty;

        public DateOnly PeriodoInicio { get; init; }

        public DateOnly PeriodoFin { get; init; }

        public string PeriodoEtiqueta { get; init; } = string.Empty;

        public InvestorPayoutFrequency Frecuencia { get; init; }

        /// <summary>Día de corte del acuerdo. Null = mes calendario.</summary>
        public int? DiaCorte { get; init; }

        /// <summary>Fecha en que cierra este periodo. Es el dato que el inversionista reconoce.</summary>
        public DateOnly FechaCorte { get; init; }

        /// <summary>"Corte mensual · día 20", resuelto por el resolver de periodos.</summary>
        public string CorteTexto { get; init; } = string.Empty;

        public InvestorCalculationBreakdownViewModel Desglose { get; init; } = new();

        /// <summary>Estado ya existente para ese periodo, si lo hay.</summary>
        public int? EstadoExistenteId { get; init; }

        public string? EstadoExistenteTexto { get; init; }

        public bool TieneAcuerdoVigente { get; init; }

        public string? Advertencia { get; init; }

        /// <summary>
        /// True solo si el período YA cerró (su último día quedó atrás en hora local del negocio).
        /// Un período abierto no puede generar un estado de cuenta: el snapshot congelaría números
        /// que todavía van a cambiar.
        /// </summary>
        public bool PeriodoCerrado { get; init; }

        public bool PuedeGenerar => TieneAcuerdoVigente && PeriodoCerrado && !EstadoExistenteId.HasValue;
    }

    /// <summary>Detalle completo de un estado de cuenta.</summary>
    public sealed class InvestorStatementDetailViewModel
    {
        public int Id { get; init; }

        public int InvestorId { get; init; }

        public string InvestorNombre { get; init; } = string.Empty;

        public string InvestorEmail { get; init; } = string.Empty;

        public string NombreNegocio { get; init; } = string.Empty;

        public DateOnly PeriodoInicio { get; init; }

        public DateOnly PeriodoFin { get; init; }

        public string PeriodoEtiqueta { get; init; } = string.Empty;

        public InvestorPayoutFrequency Frecuencia { get; init; }

        /// <summary>Día de corte congelado en el snapshot. Null = mes calendario.</summary>
        public int? DiaCorte { get; init; }

        /// <summary>Fecha de corte congelada en el snapshot.</summary>
        public DateOnly FechaCorte { get; init; }

        /// <summary>"Corte mensual · día 20" del acuerdo con el que se calculó este estado.</summary>
        public string CorteTexto { get; init; } = string.Empty;

        public InvestorStatementStatus Estado { get; init; }

        public string EstadoTexto { get; init; } = string.Empty;

        public InvestorCalculationBreakdownViewModel Desglose { get; init; } = new();

        public decimal TotalPagado { get; init; }

        public decimal SaldoPendiente { get; init; }

        public DateTime FechaCalculoUtc { get; init; }

        public DateTime? FinalizadoAtUtc { get; init; }

        public DateTime? EnviadoAtUtc { get; init; }

        public DateTime? AnuladoAtUtc { get; init; }

        public string? MotivoAnulacion { get; init; }

        public IReadOnlyList<InvestorAdjustmentRowViewModel> Ajustes { get; init; } =
            Array.Empty<InvestorAdjustmentRowViewModel>();

        public IReadOnlyList<InvestorPaymentRowViewModel> Pagos { get; init; } =
            Array.Empty<InvestorPaymentRowViewModel>();

        public IReadOnlyList<InvestorEmailRowViewModel> Envios { get; init; } =
            Array.Empty<InvestorEmailRowViewModel>();

        public bool EsEditable => Estado == InvestorStatementStatus.Draft;

        public bool AdmitePagos =>
            Estado is InvestorStatementStatus.Finalized
                or InvestorStatementStatus.Sent
                or InvestorStatementStatus.PartiallyPaid
                or InvestorStatementStatus.Paid;

        public bool EstaAnulado => Estado == InvestorStatementStatus.Voided;

        public bool PuedeEnviarse => !EsEditable && !EstaAnulado;

        /// <summary>
        /// Corte anterior y siguiente del MISMO inversionista. El usuario navega por cortes
        /// reales, no escribiendo fechas: ver <c>Navegación entre ciclos</c>.
        /// </summary>
        public InvestorStatementNavigation Navegacion { get; init; } = InvestorStatementNavigation.Vacia;

        public InvestorVisualTone Tono => InvestorVisuals.Tone(Estado, SaldoPendiente);

        /// <summary>Etiqueta corta para el badge ("Pendiente de pago", "Pagado"…).</summary>
        public string EstadoCorto => InvestorVisuals.EstadoCorto(Estado, SaldoPendiente);
    }

    public sealed record InvestorAdjustmentRowViewModel(
        int Id,
        decimal Monto,
        string Descripcion,
        string? CreadoPorEmail,
        DateTime CreatedAtUtc);

    public sealed record InvestorPaymentRowViewModel(
        int Id,
        DateOnly Fecha,
        decimal Monto,
        string MetodoPago,
        string? Referencia,
        string? Notas,
        bool EsReversion,
        string? Motivo,
        string? RegistradoPorEmail,
        DateTime CreatedAtUtc);

    public sealed record InvestorEmailRowViewModel(
        int Id,
        string RecipientEmail,
        string Subject,
        InvestorStatementEmailStatus Status,
        bool IsTest,
        string? ErrorMessage,
        DateTime? SentAtUtc,
        DateTime CreatedAtUtc)
    {
        public string StatusTexto => Status switch
        {
            InvestorStatementEmailStatus.Sent => "Enviado",
            InvestorStatementEmailStatus.Failed => "Fallido",
            InvestorStatementEmailStatus.Skipped => "Omitido",
            _ => "Pendiente"
        };
    }

    /// <summary>Registro de un pago al inversionista.</summary>
    public sealed class InvestorPaymentFormViewModel
    {
        public int StatementId { get; set; }

        [DataType(DataType.Date)]
        [Display(Name = "Fecha del pago")]
        public DateTime Fecha { get; set; } = DateTime.Today;

        [Range(0.01, 999999999, ErrorMessage = "El monto debe ser mayor a cero.")]
        [Display(Name = "Monto")]
        public decimal Monto { get; set; }

        [Required]
        [Display(Name = "Método de pago")]
        public string MetodoPago { get; set; } = "EFECTIVO";

        [StringLength(120)]
        [Display(Name = "Referencia")]
        public string? Referencia { get; set; }

        [StringLength(500)]
        [Display(Name = "Notas")]
        public string? Notas { get; set; }
    }

    /// <summary>Ajuste manual sobre un borrador.</summary>
    public sealed class InvestorAdjustmentFormViewModel
    {
        public int StatementId { get; set; }

        [Display(Name = "Monto del ajuste")]
        public decimal Monto { get; set; }

        [Required(ErrorMessage = "Explicá el motivo del ajuste.")]
        [StringLength(300)]
        [Display(Name = "Descripción")]
        public string Descripcion { get; set; } = string.Empty;
    }
}
