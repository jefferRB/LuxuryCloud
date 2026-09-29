using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Inversionistas;

namespace LuxuryApp.Models.Asociados
{
    /// <summary>Fila del listado de asociados.</summary>
    public sealed class AssociateListItemViewModel
    {
        public int Id { get; init; }

        public string Nombre { get; init; } = string.Empty;

        public string? Email { get; init; }

        public string? Telefono { get; init; }

        public string? Puesto { get; init; }

        public bool Activo { get; init; }

        public IReadOnlyList<AssociateType> Tipos { get; init; } = Array.Empty<AssociateType>();

        public string TiposTexto => Tipos.Count == 0
            ? "—"
            : string.Join(" / ", Tipos.Select(AssociateTypeTexts.Describe));

        public AssociateAccessState EstadoAcceso { get; init; }

        public string AccesoTexto => EstadoAcceso switch
        {
            AssociateAccessState.AccesoActivo => "Activo",
            AssociateAccessState.AccesoBloqueado => "Bloqueado",
            _ => "Sin acceso"
        };

        /// <summary>Porcentaje del acuerdo vigente hoy. Null si no participa de la ganancia.</summary>
        public decimal? ParticipacionVigente { get; init; }

        public string ParticipacionTexto => ParticipacionVigente.HasValue
            ? $"{ParticipacionVigente.Value.ToString("0.##")} %"
            : "—";

        /// <summary>
        /// Segunda línea de la celda de participación: "Corte mensual · día 20". Null cuando la
        /// persona no participa de la ganancia, para que la tabla no se llene de ruido.
        /// </summary>
        public string? CorteTexto { get; init; }

        public bool EsInversionista => Tipos.Contains(AssociateType.Inversionista);

        public string EstadoTexto => Activo ? "Activo" : "Inactivo";
    }

    public sealed class AssociatesIndexViewModel
    {
        public IReadOnlyList<AssociateListItemViewModel> Asociados { get; init; } =
            Array.Empty<AssociateListItemViewModel>();

        public int TotalActivos { get; init; }

        public int TotalConAcceso { get; init; }

        /// <summary>Suma de participaciones vigentes de asociados activos.</summary>
        public decimal ParticipacionAsignada { get; init; }

        public bool TieneAsociados => Asociados.Count > 0;

        /// <summary>El usuario puede crear/editar; con solo <c>Associates.View</c> es de lectura.</summary>
        public bool PuedeAdministrar { get; init; }
    }

    /// <summary>Alta de un asociado: información, relación, acceso y participación en un solo paso.</summary>
    public sealed class AssociateFormViewModel
    {
        public int? Id { get; set; }

        [Required(ErrorMessage = "Indicá el nombre del asociado.")]
        [StringLength(150)]
        [Display(Name = "Nombre")]
        public string Nombre { get; set; } = string.Empty;

        [EmailAddress(ErrorMessage = "El correo no tiene un formato válido.")]
        [StringLength(256)]
        [Display(Name = "Correo electrónico")]
        public string? Email { get; set; }

        [StringLength(30)]
        [Display(Name = "Teléfono")]
        public string? Telefono { get; set; }

        [StringLength(120)]
        [Display(Name = "Puesto")]
        public string? Puesto { get; set; }

        [Display(Name = "Asociado activo")]
        public bool Activo { get; set; } = true;

        [StringLength(1000)]
        [Display(Name = "Notas internas")]
        public string? NotasInternas { get; set; }

        [Display(Name = "Tipos")]
        public List<AssociateType> Tipos { get; set; } = new();

        // ─── Acceso al sistema (opcional) ───

        [Display(Name = "Dar acceso al sistema")]
        public bool DarAcceso { get; set; }

        [Display(Name = "Método de activación")]
        public AssociateCredentialMode ModoCredencial { get; set; } = AssociateCredentialMode.Invitacion;

        [Display(Name = "Contraseña temporal")]
        public string? ContrasenaTemporal { get; set; }

        [Display(Name = "Preset de permisos")]
        public AssociatePermissionPreset Preset { get; set; } = AssociatePermissionPreset.Personalizado;

        [Display(Name = "Permisos")]
        public List<string> Permisos { get; set; } = new();

        // ─── Participación (solo si es Inversionista) ───

        [Range(0.01, 100, ErrorMessage = "El porcentaje debe estar entre 0,01 y 100.")]
        [Display(Name = "Porcentaje de participación")]
        public decimal ParticipacionPorcentaje { get; set; } = 10m;

        [Display(Name = "Vigente desde")]
        [DataType(DataType.Date)]
        public DateTime EffectiveFrom { get; set; } = DateTime.Today;

        [Display(Name = "Frecuencia de liquidación")]
        public InvestorPayoutFrequency Frecuencia { get; set; } = InvestorPayoutFrequency.Mensual;

        /// <summary>Día de corte para acuerdos mensuales. Null = mes calendario.</summary>
        [Range(1, 31, ErrorMessage = "El día de corte debe estar entre 1 y 31.")]
        [Display(Name = "Día de corte")]
        public int? DiaCorte { get; set; }

        [Display(Name = "Tratamiento de pérdidas")]
        public InvestorLossTreatment TratamientoPerdidas { get; set; } = InvestorLossTreatment.NoDistribution;

        [Display(Name = "Enviar el estado automáticamente")]
        public bool EnvioAutomatico { get; set; }

        // ─── Ayuda contextual (no se envía) ───

        /// <summary>Suma de participaciones vigentes de OTROS asociados.</summary>
        public decimal ParticipacionOtros { get; set; }

        /// <summary>Primer día del próximo periodo válido para iniciar una participación.</summary>
        public DateOnly? ProximoInicioPeriodo { get; set; }

        public bool EsInversionista => Tipos.Contains(AssociateType.Inversionista);
    }

    /// <summary>Bloque "Acceso al sistema" del detalle del asociado.</summary>
    public sealed class AssociateAccessViewModel
    {
        public int AssociateId { get; init; }

        public string Nombre { get; init; } = string.Empty;

        public bool AsociadoActivo { get; init; }

        public AssociateAccessState Estado { get; init; }

        public string? Email { get; init; }

        public bool TieneAcceso => Estado != AssociateAccessState.SinAcceso;

        public string EstadoTexto => Estado switch
        {
            AssociateAccessState.AccesoActivo => "Acceso activo",
            AssociateAccessState.AccesoBloqueado => "Acceso bloqueado",
            _ => "Sin acceso"
        };
    }

    /// <summary>Participación financiera vigente y su historial.</summary>
    public sealed class AssociateParticipationViewModel
    {
        public int AssociateId { get; init; }

        /// <summary>Id del perfil de inversionista, para enlazar con estados de cuenta.</summary>
        public int? InvestorId { get; init; }

        public decimal? PorcentajeVigente { get; init; }

        public InvestorPayoutFrequency? Frecuencia { get; init; }

        public string FrecuenciaTexto =>
            Frecuencia is null ? "—" : InvestorPeriodCalculator.FrecuenciaTexto(Frecuencia.Value);

        /// <summary>Día de corte vigente. Null = mes calendario.</summary>
        public int? DiaCorte { get; init; }

        /// <summary>"Corte mensual · día 20". Sale del resolver, nunca de la vista.</summary>
        public string CorteTexto { get; init; } = string.Empty;

        /// <summary>Frase que explica el corte con el día realmente configurado.</summary>
        public string CorteDescripcion { get; init; } = string.Empty;

        /// <summary>Periodo en curso hoy, según el acuerdo vigente.</summary>
        public DateOnly? PeriodoActualInicio { get; init; }

        public DateOnly? PeriodoActualFin { get; init; }

        /// <summary>Fecha del próximo corte (el cierre del periodo en curso).</summary>
        public DateOnly? ProximoCorte { get; init; }

        public InvestorLossTreatment TratamientoPerdidas { get; init; }

        public bool EnvioAutomatico { get; init; }

        public DateOnly? VigenteDesde { get; init; }

        /// <summary>Historial completo de porcentajes (versiones cerradas + la vigente).</summary>
        public IReadOnlyList<AssociateParticipationHistoryRow> Historial { get; init; } =
            Array.Empty<AssociateParticipationHistoryRow>();

        /// <summary>Suma de participaciones vigentes de OTROS asociados.</summary>
        public decimal ParticipacionOtros { get; init; }

        /// <summary>Primer día del próximo periodo donde puede entrar en vigor un cambio.</summary>
        public DateOnly? ProximoInicioPeriodo { get; init; }

        public bool TieneParticipacion => PorcentajeVigente.HasValue;
    }

    public sealed record AssociateParticipationHistoryRow(
        decimal Porcentaje,
        DateOnly EffectiveFrom,
        DateOnly? EffectiveTo,
        InvestorPayoutFrequency Frecuencia,
        int? DiaCorte,
        bool Vigente)
    {
        /// <summary>"Corte mensual · día 20" de ESA versión del acuerdo, no de la vigente.</summary>
        public string CorteTexto => InvestorSettlementPeriodResolver.EtiquetaCorte(Frecuencia, DiaCorte);
    }

    /// <summary>Detalle completo del asociado: cada bloque se muestra solo si aplica.</summary>
    public sealed class AssociateDetailViewModel
    {
        public AssociateFormViewModel Datos { get; init; } = new();

        public AssociateAccessViewModel Acceso { get; init; } = new();

        public AssociatePermissionSet Permisos { get; init; } = AssociatePermissionSet.Ninguno;

        public AssociateParticipationViewModel? Participacion { get; init; }

        /// <summary>
        /// Resumen financiero del inversionista: último corte EMITIDO, ciclo en curso y saldo.
        /// Null cuando el asociado todavía no tiene perfil de inversionista.
        ///
        /// <para>
        /// Vive aparte de <see cref="Participacion"/> a propósito: esa es la CONFIGURACIÓN del
        /// acuerdo (porcentaje, corte, historial) y este es el ESTADO del dinero. Mezclarlos fue lo
        /// que hacía que la pantalla mostrara un "último corte" que nunca existió.
        /// </para>
        /// </summary>
        public InvestorFinancialSummaryViewModel? ResumenFinanciero { get; init; }

        public bool EsInversionista => Datos.Tipos.Contains(AssociateType.Inversionista);

        public bool PuedeAdministrar { get; init; }
    }

    /// <summary>Entrada de la matriz de permisos reutilizable (alta y detalle).</summary>
    public sealed class AssociatePermissionsMatrixViewModel
    {
        public IReadOnlyCollection<string> Concedidos { get; init; } = Array.Empty<string>();

        /// <summary>Sin permiso de administrar, la matriz se muestra pero no se puede tocar.</summary>
        public bool SoloLectura { get; init; }

        public bool MostrarPresets { get; init; } = true;
    }

    /// <summary>Cambio de participación desde el detalle del asociado.</summary>
    public sealed class AssociateParticipationFormViewModel
    {
        public int AssociateId { get; set; }

        [Range(0.01, 100, ErrorMessage = "El porcentaje debe estar entre 0,01 y 100.")]
        [Display(Name = "Porcentaje de participación")]
        public decimal ParticipacionPorcentaje { get; set; }

        [Display(Name = "Vigente desde")]
        [DataType(DataType.Date)]
        public DateTime EffectiveFrom { get; set; } = DateTime.Today;

        [Display(Name = "Frecuencia de liquidación")]
        public InvestorPayoutFrequency Frecuencia { get; set; } = InvestorPayoutFrequency.Mensual;

        /// <summary>Día de corte para acuerdos mensuales. Null = mes calendario.</summary>
        [Range(1, 31, ErrorMessage = "El día de corte debe estar entre 1 y 31.")]
        [Display(Name = "Día de corte")]
        public int? DiaCorte { get; set; }

        [Display(Name = "Tratamiento de pérdidas")]
        public InvestorLossTreatment TratamientoPerdidas { get; set; } = InvestorLossTreatment.NoDistribution;

        [Display(Name = "Enviar el estado automáticamente")]
        public bool EnvioAutomatico { get; set; }
    }

    /// <summary>
    /// KPI de participación de asociados que se muestra en el Dashboard financiero.
    ///
    /// <para>
    /// Solo se construye si hay al menos una participación vigente para el periodo Y el usuario
    /// tiene permiso financiero. Cuando no aplica, el controlador entrega <c>null</c>: la
    /// información no viaja al HTML ni al ViewModel, no se esconde con CSS.
    /// </para>
    /// </summary>
    public sealed class AssociateAllocationKpiViewModel
    {
        public decimal GananciaDistribuible { get; init; }

        public decimal ParticipacionPorcentaje { get; init; }

        public decimal ParticipacionMonto { get; init; }

        public decimal GananciaRestante => GananciaDistribuible - ParticipacionMonto;

        public decimal PorcentajeRestante => 100m - ParticipacionPorcentaje;

        public int CantidadAsociados { get; init; }

        public string PeriodoEtiqueta { get; init; } = string.Empty;

        /// <summary>
        /// Próxima fecha de corte, SOLO cuando todos los que participan comparten la misma. Si cada
        /// uno tiene su propio corte no se muestra ninguna: inventar "la" fecha sería mentir.
        /// </summary>
        public DateOnly? ProximoCorte { get; init; }

        /// <summary>
        /// True cuando el mes calendario del Dashboard NO coincide con el periodo contractual de
        /// los asociados. Es lo que justifica llamar "estimada" a la participación del mes: el
        /// monto que se pague en el corte se calcula sobre otro rango de fechas.
        /// </summary>
        public bool PeriodoDifiereDelCorte { get; init; }
    }
}
