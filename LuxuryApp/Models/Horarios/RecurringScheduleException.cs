using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.Funcionarios;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Horarios
{
    /// <summary>
    /// Excepción puntual a una regla recurrente para una fecha concreta. NUNCA modifica la regla
    /// general: solo altera lo que pasa ese día.
    ///
    /// <para>
    /// Ejemplo: la regla es almuerzo 1:00–2:00 p. m., pero el 15 de agosto María almuerza de
    /// 2:00 a 3:00 p. m. → excepción <see cref="RecurringScheduleExceptionType.CambiarHorario"/>
    /// para el 15/08 con <see cref="FuncionarioId"/> = María.
    /// </para>
    ///
    /// <para>
    /// <see cref="FuncionarioId"/> null significa "toda la regla ese día" (por ejemplo, un feriado
    /// en el que nadie almuerza a esa hora).
    /// </para>
    /// </summary>
    public class RecurringScheduleException : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int RuleId { get; set; }

        public RecurringScheduleRule? Rule { get; set; }

        /// <summary>Colaborador afectado. Null = la excepción aplica a todos ese día.</summary>
        public int? FuncionarioId { get; set; }

        public Funcionario? Funcionario { get; set; }

        [Display(Name = "Fecha")]
        public DateOnly Fecha { get; set; }

        [Display(Name = "Tipo de excepción")]
        public RecurringScheduleExceptionType Tipo { get; set; } = RecurringScheduleExceptionType.Omitir;

        /// <summary>Horario alternativo. Obligatorio con <see cref="RecurringScheduleExceptionType.CambiarHorario"/>.</summary>
        [Display(Name = "Nueva hora inicial")]
        public TimeOnly? HoraInicioAlternativa { get; set; }

        [Display(Name = "Nueva hora final")]
        public TimeOnly? HoraFinAlternativa { get; set; }

        [MaxLength(200)]
        [Display(Name = "Motivo")]
        public string? Motivo { get; set; }

        [MaxLength(450)]
        public string? CreadoPorUserId { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
