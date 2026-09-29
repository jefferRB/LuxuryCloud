using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.Inversionistas;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Asociados
{
    /// <summary>
    /// Persona relacionada con el negocio que NO es un funcionario operativo: inversionista,
    /// socio, marketing, contabilidad, asesor…
    ///
    /// <para>
    /// Separación de responsabilidades (a propósito, esta entidad NO crece con banderas):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Quién es</b> → esta entidad.</item>
    ///   <item><b>Qué relación tiene</b> → <see cref="AssociateTypeAssignment"/>.</item>
    ///   <item><b>Si entra al sistema</b> → <see cref="AppUsuarioId"/> + la cuenta Identity.</item>
    ///   <item><b>Qué puede hacer</b> → <see cref="AssociatePermission"/>.</item>
    ///   <item><b>Su participación financiera</b> → <see cref="Inversionistas.TenantInvestor"/>
    ///   + <see cref="Inversionistas.InvestorAgreement"/> (historial de porcentajes).</item>
    /// </list>
    ///
    /// <para>
    /// Un asociado NO es un <c>Funcionario</c>: el funcionario tiene agenda, producción, cobros y
    /// liquidaciones. Los dos dominios se mantienen separados; solo comparten la infraestructura
    /// de identidad e invitaciones.
    /// </para>
    /// </summary>
    public class Associate : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        [Required(ErrorMessage = "Indicá el nombre del asociado.")]
        [MaxLength(150)]
        [Display(Name = "Nombre")]
        public string Nombre { get; set; } = string.Empty;

        [MaxLength(256)]
        [Display(Name = "Correo electrónico")]
        public string? Email { get; set; }

        [MaxLength(30)]
        [Display(Name = "Teléfono")]
        public string? Telefono { get; set; }

        /// <summary>Cargo o rol descriptivo dentro del negocio (texto libre, no autoriza nada).</summary>
        [MaxLength(120)]
        [Display(Name = "Puesto")]
        public string? Puesto { get; set; }

        /// <summary>
        /// Baja lógica. Desactivar NUNCA borra participación, estados de cuenta ni auditoría:
        /// solo saca al asociado de la operación diaria.
        /// </summary>
        [Display(Name = "Activo")]
        public bool Activo { get; set; } = true;

        /// <summary>Notas internas del negocio. Nunca salen en correos al asociado.</summary>
        [MaxLength(1000)]
        [Display(Name = "Notas internas")]
        public string? NotasInternas { get; set; }

        /// <summary>
        /// Cuenta Identity del asociado cuando tiene acceso al sistema. Null = no tiene
        /// credenciales y no puede iniciar sesión (caso normal de un inversionista).
        /// </summary>
        [BindNever]
        [MaxLength(450)]
        public string? AppUsuarioId { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? CreatedByUserId { get; set; }

        [MaxLength(450)]
        public string? UpdatedByUserId { get; set; }

        public ICollection<AssociateTypeAssignment> Tipos { get; set; } = new List<AssociateTypeAssignment>();

        public ICollection<AssociatePermission> Permisos { get; set; } = new List<AssociatePermission>();

        /// <summary>
        /// Perfil de inversionista del asociado. Existe solo cuando el asociado participa de la
        /// ganancia; es la entidad que ya usan los acuerdos y estados de cuenta históricos.
        /// </summary>
        public TenantInvestor? PerfilInversionista { get; set; }

        public bool TieneAcceso => !string.IsNullOrWhiteSpace(AppUsuarioId);
    }

    /// <summary>
    /// Clasificación de negocio de un asociado. Una persona puede tener varias
    /// (ej. Inversionista + Socio), por eso es una tabla y no una columna.
    /// </summary>
    public class AssociateTypeAssignment : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int AssociateId { get; set; }

        public Associate? Associate { get; set; }

        public AssociateType Tipo { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Concesión explícita de un permiso a un asociado. La ausencia de fila significa
    /// DENEGADO: un asociado nunca hereda permisos por su tipo ni por defaults.
    /// </summary>
    public class AssociatePermission : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int AssociateId { get; set; }

        public Associate? Associate { get; set; }

        [Required]
        [MaxLength(80)]
        public string Permiso { get; set; } = string.Empty;

        public bool Permitido { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
