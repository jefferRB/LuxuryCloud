using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.Funcionarios
{
    /// <summary>
    /// Override persistido de un permiso del portal para un funcionario concreto.
    /// Si no existe fila para un permiso, aplica el valor por defecto
    /// (<see cref="FuncionarioPortalPermissions.Defaults"/>).
    /// </summary>
    public sealed class FuncionarioPortalPermiso : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int FuncionarioId { get; set; }

        [Required]
        [MaxLength(60)]
        public string Permiso { get; set; } = string.Empty;

        public bool Permitido { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        public Funcionario? Funcionario { get; set; }
    }
}
