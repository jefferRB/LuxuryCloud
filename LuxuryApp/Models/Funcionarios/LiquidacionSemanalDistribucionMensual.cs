using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.Funcionarios
{
    public class LiquidacionSemanalDistribucionMensual : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        [Required]
        public int LiquidacionSemanalId { get; set; }
        public LiquidacionSemanal? LiquidacionSemanal { get; set; }

        [Required]
        public int Anio { get; set; }

        [Required]
        public int Mes { get; set; }

        [Required]
        public decimal MontoAsignado { get; set; }

        [Required]
        public int DiasAplicados { get; set; }
    }
}
