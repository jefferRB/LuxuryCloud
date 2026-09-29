using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.Funcionarios
{
    public class PagoFuncionario : ITenantEntity
    {

        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }
        [Key]
        public int IdPago { get; set; }
        public int FuncionarioId { get; set; }
        public decimal MontoPagado { get; set; }
        public DateTime FechaPago { get; set; }
        public DateTime InicioSemana { get; set; }
        public DateTime FinSemana { get; set; }
        public string? Observacion { get; set; }
        [ForeignKey("FuncionarioId")]
        public Funcionario? Funcionario { get; set; }
    }
}
