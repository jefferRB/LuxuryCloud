using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.Funcionarios
{
    public class Puesto : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }
        [Key]
        public int IdPuesto { get; set; }

        [Required]
        [MaxLength(100)]
        public string NombrePuesto { get; set; } = string.Empty;

        [MaxLength(250)]
        public string? Detalle { get; set; }

        public bool Activo { get; set; } = true;

        // Relación
        public ICollection<Funcionario> Funcionarios { get; set; } = new List<Funcionario>();
    }
}
