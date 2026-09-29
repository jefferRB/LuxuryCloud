using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.Funcionarios
{
    public class LiquidacionSemanalDetalle : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        [Required]
        public int LiquidacionSemanalId { get; set; }
        public LiquidacionSemanal? LiquidacionSemanal { get; set; }

        [Required]
        public int FuncionarioId { get; set; }
        public Funcionario? Funcionario { get; set; }

        [Required]
        public decimal MontoServicios { get; set; }

        [Required]
        public decimal MontoProductos { get; set; }

        [Required]
        public decimal Impuestos { get; set; }

        [Required]
        public decimal MontoNeto { get; set; }

        [Required]
        public decimal MontoPagado { get; set; }

        [Required]
        public decimal Pendiente { get; set; }
    }
}
