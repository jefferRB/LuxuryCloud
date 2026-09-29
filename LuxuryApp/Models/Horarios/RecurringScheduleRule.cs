using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.Funcionarios;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Horarios
{
    /// <summary>
    /// Regla recurrente de indisponibilidad (ej. "Almuerzo 1:00 p. m. – 2:00 p. m., lunes a sábado").
    ///
    /// <para>
    /// La REGLA es la fuente de verdad: no se materializan citas falsas ni ocurrencias en base de
    /// datos. Las ocurrencias concretas se calculan al vuelo con
    /// <c>RecurringScheduleOccurrenceCalculator</c>.
    /// </para>
    ///
    /// <para>
    /// Zona horaria: <see cref="HoraInicio"/> y <see cref="HoraFin"/> son horas LOCALES del negocio
    /// (America/Costa_Rica vía <c>IBusinessDateTimeProvider</c>), igual que <c>Cita.FechaHoraCita</c>.
    /// Nunca se guardan como UTC: un bloque de almuerzo es 1:00 p. m. del reloj de pared, no un
    /// instante absoluto.
    /// </para>
    ///
    /// <para>
    /// Versionado: un cambio de horario/días sobre una regla vigente no reescribe el pasado. Se
    /// cierra la vigente con <see cref="VigenteHasta"/> y se crea una nueva versión enlazada por
    /// <see cref="ReglaOrigenId"/>.
    /// </para>
    /// </summary>
    public class RecurringScheduleRule : ITenantEntity
    {
        public const int MinDurationMinutes = 5;
        public const int MaxDurationMinutes = 720;

        /// <summary>Lunes a sábado. Bit por DayOfWeek (domingo = 0).</summary>
        public const int LunesASabadoMask = 0b0111_1110;

        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        [Required(ErrorMessage = "Indicá un nombre para la regla.")]
        [MaxLength(100)]
        [Display(Name = "Nombre de la regla")]
        public string Nombre { get; set; } = string.Empty;

        [Display(Name = "Tipo")]
        public RecurringScheduleRuleType Tipo { get; set; } = RecurringScheduleRuleType.UnavailableBlock;

        [Display(Name = "Hora inicial")]
        public TimeOnly HoraInicio { get; set; } = new(13, 0);

        [Display(Name = "Hora final")]
        public TimeOnly HoraFin { get; set; } = new(14, 0);

        /// <summary>Días de la semana como máscara de bits. Bit index = (int)DayOfWeek (domingo = 0).</summary>
        [Display(Name = "Días de la semana")]
        public int DiasSemanaMask { get; set; } = LunesASabadoMask;

        [Display(Name = "Vigente desde")]
        public DateOnly VigenteDesde { get; set; }

        [Display(Name = "Vigente hasta")]
        public DateOnly? VigenteHasta { get; set; }

        /// <summary>Activo o pausado. Una regla pausada no bloquea nada pero conserva su historial.</summary>
        [Display(Name = "Activa")]
        public bool Activa { get; set; } = true;

        [Display(Name = "Aplicar a")]
        public RecurringScheduleScope Alcance { get; set; } = RecurringScheduleScope.TodosLosColaboradores;

        /// <summary>
        /// Solo aplica cuando el alcance es <see cref="RecurringScheduleScope.TodosLosColaboradores"/>:
        /// si es true, un colaborador creado después queda cubierto automáticamente. Como la
        /// pertenencia se evalúa dinámicamente, esto es simplemente un interruptor de la regla.
        /// </summary>
        [Display(Name = "Incluir automáticamente nuevos colaboradores")]
        public bool IncluirNuevosColaboradores { get; set; } = true;

        /// <summary>Texto que se muestra en el calendario. Si está vacío se usa el nombre.</summary>
        [MaxLength(60)]
        [Display(Name = "Texto en el calendario")]
        public string? EtiquetaCalendario { get; set; }

        [MaxLength(60)]
        [Display(Name = "Motivo o categoría")]
        public string? Motivo { get; set; }

        /// <summary>Versión anterior de esta regla, cuando se editó una regla ya vigente.</summary>
        public int? ReglaOrigenId { get; set; }

        public RecurringScheduleRule? ReglaOrigen { get; set; }

        [MaxLength(450)]
        public string? CreadoPorUserId { get; set; }

        [MaxLength(450)]
        public string? ActualizadoPorUserId { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public ICollection<RecurringScheduleRuleTarget> Colaboradores { get; set; } =
            new List<RecurringScheduleRuleTarget>();

        public ICollection<RecurringScheduleException> Excepciones { get; set; } =
            new List<RecurringScheduleException>();

        public bool AplicaDia(DayOfWeek dia) => (DiasSemanaMask & (1 << (int)dia)) != 0;

        public int DuracionMinutos => (int)(HoraFin.ToTimeSpan() - HoraInicio.ToTimeSpan()).TotalMinutes;

        public string TextoCalendario =>
            string.IsNullOrWhiteSpace(EtiquetaCalendario) ? Nombre : EtiquetaCalendario!;

        /// <summary>True si la regla puede producir ocurrencias en la fecha indicada.</summary>
        public bool CubreFecha(DateOnly fecha) =>
            Activa &&
            VigenteDesde <= fecha &&
            (VigenteHasta is null || VigenteHasta.Value >= fecha) &&
            AplicaDia(fecha.DayOfWeek);
    }

    /// <summary>
    /// Colaborador explícitamente incluido en una regla de alcance
    /// <see cref="RecurringScheduleScope.ColaboradoresSeleccionados"/>.
    /// Con alcance global NO se materializa una fila por colaborador: la pertenencia se evalúa
    /// dinámicamente para que un colaborador nuevo quede cubierto sin tocar la regla.
    /// </summary>
    public class RecurringScheduleRuleTarget : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int RuleId { get; set; }

        public RecurringScheduleRule? Rule { get; set; }

        public int FuncionarioId { get; set; }

        public Funcionario? Funcionario { get; set; }
    }
}
