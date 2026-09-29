using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;

namespace LuxuryApp.Models.DataBase
{
    public class ClienteServicioRealizado : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int ClienteId { get; set; }

        public int? FuncionarioId { get; set; }

        public int? ServicioId { get; set; }

        public int? CobroId { get; set; }

        public int? CitaId { get; set; }

        [Required]
        public DateTime FechaHora { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Monto { get; set; }

        [StringLength(500)]
        public string? Notas { get; set; }

        [Required]
        [StringLength(30)]
        public string Origen { get; set; } = OrigenServicioRealizado.Manual;

        public DateTime CreadoEn { get; set; }

        [ForeignKey(nameof(ClienteId))]
        public ClientesModel? Cliente { get; set; }

        [ForeignKey(nameof(FuncionarioId))]
        public Funcionario? Funcionario { get; set; }

        [ForeignKey(nameof(ServicioId))]
        public Servicio? Servicio { get; set; }

        [ForeignKey(nameof(CobroId))]
        public Cobro? Cobro { get; set; }

        [ForeignKey(nameof(CitaId))]
        public Cita? Cita { get; set; }
    }

    public static class OrigenServicioRealizado
    {
        public const string Cobro = "Cobro";
        public const string Calendario = "Calendario";
        public const string Manual = "Manual";
    }
}
