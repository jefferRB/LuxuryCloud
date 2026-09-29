using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Calendar
{
    public class ResizeDurationVM
    {
        [Required]
        [Range(5, 600, ErrorMessage = "La duración debe estar entre 5 y 600 minutos.")]
        public int DuracionMinutos { get; set; }
    }
}
