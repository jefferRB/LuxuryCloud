using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Perfil de inversionista dentro del negocio: el contacto al que se le envían estados de
    /// cuenta y el ancla de los acuerdos de participación y del histórico financiero.
    ///
    /// <para>
    /// Desde el módulo de Asociados esta entidad dejó de ser "la persona" y pasó a ser
    /// "la participación financiera de la persona": la identidad vive en
    /// <see cref="Asociados.Associate"/> y se enlaza por <see cref="AssociateId"/>. La tabla y
    /// todas sus relaciones (acuerdos, estados de cuenta, pagos, envíos) se conservan intactas
    /// a propósito: mover ese histórico habría sido una migración destructiva.
    /// </para>
    ///
    /// <para>
    /// Sigue sin ser un usuario del sistema: el acceso, si existe, es del asociado.
    /// </para>
    /// </summary>
    public class TenantInvestor : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        /// <summary>
        /// Asociado dueño de esta participación. Nullable solo por compatibilidad con filas
        /// anteriores al módulo de Asociados; la migración enlaza todas las existentes y el
        /// servicio nunca crea un perfil suelto.
        /// </summary>
        [BindNever]
        public int? AssociateId { get; set; }

        public Asociados.Associate? Associate { get; set; }

        [Required(ErrorMessage = "Indicá el nombre del inversionista.")]
        [MaxLength(150)]
        [Display(Name = "Nombre")]
        public string Nombre { get; set; } = string.Empty;

        [Required(ErrorMessage = "Indicá el correo del inversionista.")]
        [MaxLength(256)]
        [Display(Name = "Correo electrónico")]
        public string Email { get; set; } = string.Empty;

        [MaxLength(30)]
        [Display(Name = "Teléfono")]
        public string? Telefono { get; set; }

        [Display(Name = "Activo")]
        public bool Activo { get; set; } = true;

        /// <summary>Notas internas del negocio. NUNCA se incluyen en el correo al inversionista.</summary>
        [MaxLength(1000)]
        [Display(Name = "Notas internas")]
        public string? NotasInternas { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? CreatedByUserId { get; set; }

        [MaxLength(450)]
        public string? UpdatedByUserId { get; set; }

        public ICollection<InvestorAgreement> Acuerdos { get; set; } = new List<InvestorAgreement>();

        public ICollection<InvestorStatement> Estados { get; set; } = new List<InvestorStatement>();
    }
}
