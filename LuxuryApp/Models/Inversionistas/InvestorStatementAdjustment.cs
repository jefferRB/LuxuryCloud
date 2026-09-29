using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Ajuste manual autorizado sobre un estado de cuenta. Exige descripción y usuario
    /// responsable; queda además en PlatformAuditLog. Solo se admite sobre borradores.
    /// </summary>
    public class InvestorStatementAdjustment : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int StatementId { get; set; }

        public InvestorStatement? Statement { get; set; }

        /// <summary>Positivo suma a la ganancia distribuible; negativo resta. Nunca cero.</summary>
        [Display(Name = "Monto del ajuste")]
        public decimal Monto { get; set; }

        [Required(ErrorMessage = "Explicá el motivo del ajuste.")]
        [MaxLength(300)]
        [Display(Name = "Descripción")]
        public string Descripcion { get; set; } = string.Empty;

        [MaxLength(450)]
        public string? CreadoPorUserId { get; set; }

        [MaxLength(256)]
        public string? CreadoPorEmail { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
