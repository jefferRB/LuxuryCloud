using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Calendar
{
    public class MoveCitaVM
    {
        [Required]
        public DateTime FechaHoraCita { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Debe seleccionar un funcionario valido.")]
        public int? FuncionarioId { get; set; }
    }
}
