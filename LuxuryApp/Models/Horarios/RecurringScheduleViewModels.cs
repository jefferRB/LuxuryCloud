using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Horarios
{
    /// <summary>Fila del listado de bloqueos recurrentes.</summary>
    public sealed class RecurringScheduleRuleListItemViewModel
    {
        public int Id { get; init; }

        public string Nombre { get; init; } = string.Empty;

        public string Horario { get; init; } = string.Empty;

        public string Dias { get; init; } = string.Empty;

        public bool Activa { get; init; }

        public RecurringScheduleScope Alcance { get; init; }

        public string AlcanceTexto { get; init; } = string.Empty;

        public int ColaboradoresCount { get; init; }

        public DateOnly VigenteDesde { get; init; }

        public DateOnly? VigenteHasta { get; init; }

        public int ExcepcionesProximas { get; init; }

        public string? Motivo { get; init; }

        /// <summary>True si esta regla es una versión posterior de otra.</summary>
        public bool EsVersion { get; init; }

        public string EstadoTexto => !Activa
            ? "Pausada"
            : VigenteHasta.HasValue ? "Vigente con fecha final" : "Vigente";
    }

    public sealed class RecurringSchedulePageViewModel
    {
        public IReadOnlyList<RecurringScheduleRuleListItemViewModel> Reglas { get; init; } =
            Array.Empty<RecurringScheduleRuleListItemViewModel>();

        public IReadOnlyList<RecurringScheduleFuncionarioOption> Funcionarios { get; init; } =
            Array.Empty<RecurringScheduleFuncionarioOption>();

        public int ReglasActivas => Reglas.Count(regla => regla.Activa);

        public bool TieneReglas => Reglas.Count > 0;
    }

    public sealed record RecurringScheduleFuncionarioOption(int Id, string Nombre, string Color);

    /// <summary>Alta/edición de una regla.</summary>
    public sealed class RecurringScheduleRuleFormViewModel
    {
        public int? Id { get; set; }

        [Required(ErrorMessage = "Indicá un nombre para la regla.")]
        [StringLength(100)]
        [Display(Name = "Nombre de la regla")]
        public string Nombre { get; set; } = "Almuerzo";

        [Display(Name = "Hora inicial")]
        [DataType(DataType.Time)]
        public TimeOnly HoraInicio { get; set; } = new(13, 0);

        [Display(Name = "Hora final")]
        [DataType(DataType.Time)]
        public TimeOnly HoraFin { get; set; } = new(14, 0);

        /// <summary>Índices de DayOfWeek seleccionados (domingo = 0).</summary>
        [Display(Name = "Días de la semana")]
        public List<int> Dias { get; set; } = new() { 1, 2, 3, 4, 5, 6 };

        [Display(Name = "Vigente desde")]
        [DataType(DataType.Date)]
        public DateTime VigenteDesde { get; set; } = DateTime.Today;

        [Display(Name = "Vigente hasta (opcional)")]
        [DataType(DataType.Date)]
        public DateTime? VigenteHasta { get; set; }

        [Display(Name = "Regla activa")]
        public bool Activa { get; set; } = true;

        [Display(Name = "Aplicar a")]
        public RecurringScheduleScope Alcance { get; set; } = RecurringScheduleScope.TodosLosColaboradores;

        [Display(Name = "Incluir automáticamente nuevos colaboradores")]
        public bool IncluirNuevosColaboradores { get; set; } = true;

        [Display(Name = "Colaboradores")]
        public List<int> FuncionarioIds { get; set; } = new();

        [StringLength(60)]
        [Display(Name = "Texto en el calendario")]
        public string? EtiquetaCalendario { get; set; }

        [StringLength(60)]
        [Display(Name = "Motivo o categoría")]
        public string? Motivo { get; set; }

        /// <summary>
        /// Confirmación explícita del usuario cuando hay citas existentes que coinciden.
        /// Sin esto la regla no se activa: la interfaz debe decir cuántas citas se encontraron.
        /// </summary>
        public bool ConfirmarConflictos { get; set; }

        public IReadOnlyList<RecurringScheduleFuncionarioOption> FuncionariosDisponibles { get; set; } =
            Array.Empty<RecurringScheduleFuncionarioOption>();

        public bool EsEdicion => Id.HasValue;

        /// <summary>Conflictos detectados en el último intento de guardado.</summary>
        public IReadOnlyList<RecurringScheduleConflictViewModel> Conflictos { get; set; } =
            Array.Empty<RecurringScheduleConflictViewModel>();

        public int DiasSemanaMask =>
            Dias.Where(dia => dia is >= 0 and <= 6).Aggregate(0, (mask, dia) => mask | (1 << dia));
    }

    /// <summary>Cita existente que coincide con una futura ocurrencia de la regla.</summary>
    public sealed record RecurringScheduleConflictViewModel(
        int CitaId,
        DateTime FechaHoraCita,
        int DuracionMinutos,
        int FuncionarioId,
        string FuncionarioNombre,
        string? Detalle)
    {
        public string FechaTexto => FechaHoraCita.ToString("dd/MM/yyyy HH:mm");
    }

    /// <summary>Resumen de conflictos que la vista muestra antes de activar la regla.</summary>
    public sealed class RecurringScheduleConflictSummaryViewModel
    {
        public IReadOnlyList<RecurringScheduleConflictViewModel> Conflictos { get; init; } =
            Array.Empty<RecurringScheduleConflictViewModel>();

        public int Total => Conflictos.Count;

        public bool TieneConflictos => Total > 0;

        public string Mensaje => Total switch
        {
            0 => "No encontramos citas que coincidan con este horario.",
            1 => "Detectamos 1 cita que coincide con este horario. No se moverá ni se cancelará.",
            _ => $"Detectamos {Total} citas que coinciden con este horario. No se moverán ni se cancelarán."
        };
    }

    /// <summary>Alta de una excepción puntual.</summary>
    public sealed class RecurringScheduleExceptionFormViewModel
    {
        public int RuleId { get; set; }

        public string? RuleNombre { get; set; }

        [Display(Name = "Fecha")]
        [DataType(DataType.Date)]
        public DateTime Fecha { get; set; } = DateTime.Today;

        [Display(Name = "Colaborador")]
        public int? FuncionarioId { get; set; }

        [Display(Name = "Tipo de excepción")]
        public RecurringScheduleExceptionType Tipo { get; set; } = RecurringScheduleExceptionType.Omitir;

        [Display(Name = "Nueva hora inicial")]
        [DataType(DataType.Time)]
        public TimeOnly? HoraInicioAlternativa { get; set; }

        [Display(Name = "Nueva hora final")]
        [DataType(DataType.Time)]
        public TimeOnly? HoraFinAlternativa { get; set; }

        [StringLength(200)]
        [Display(Name = "Motivo")]
        public string? Motivo { get; set; }

        public IReadOnlyList<RecurringScheduleFuncionarioOption> FuncionariosDisponibles { get; set; } =
            Array.Empty<RecurringScheduleFuncionarioOption>();
    }

    /// <summary>Detalle de una regla con sus excepciones.</summary>
    public sealed class RecurringScheduleRuleDetailViewModel
    {
        public int Id { get; init; }

        public string Nombre { get; init; } = string.Empty;

        public string Horario { get; init; } = string.Empty;

        public string Dias { get; init; } = string.Empty;

        public string AlcanceTexto { get; init; } = string.Empty;

        public bool Activa { get; init; }

        public DateOnly VigenteDesde { get; init; }

        public DateOnly? VigenteHasta { get; init; }

        public string? Motivo { get; init; }

        public string? EtiquetaCalendario { get; init; }

        public IReadOnlyList<string> Colaboradores { get; init; } = Array.Empty<string>();

        public IReadOnlyList<RecurringScheduleExceptionRowViewModel> Excepciones { get; init; } =
            Array.Empty<RecurringScheduleExceptionRowViewModel>();

        public IReadOnlyList<RecurringScheduleFuncionarioOption> FuncionariosDisponibles { get; init; } =
            Array.Empty<RecurringScheduleFuncionarioOption>();
    }

    public sealed record RecurringScheduleExceptionRowViewModel(
        int Id,
        DateOnly Fecha,
        string? FuncionarioNombre,
        RecurringScheduleExceptionType Tipo,
        TimeOnly? HoraInicioAlternativa,
        TimeOnly? HoraFinAlternativa,
        string? Motivo)
    {
        public string TipoTexto => Tipo switch
        {
            RecurringScheduleExceptionType.CambiarHorario =>
                $"Cambia a {HoraInicioAlternativa:HH\\:mm} – {HoraFinAlternativa:HH\\:mm}",
            RecurringScheduleExceptionType.ExcluirColaborador => "Colaborador excluido ese día",
            _ => "Sin bloqueo ese día"
        };

        public string AlcanceTexto => FuncionarioNombre ?? "Todos los colaboradores";
    }
}
