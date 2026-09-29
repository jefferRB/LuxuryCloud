using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.DataBase
{
    public sealed class ServicioRealizadoViewModel
    {
        public int ClienteId { get; set; }
        public string NumeroTelefono { get; set; } = string.Empty;
        public string NombreCliente { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? DescripcionServicios { get; set; }

        public DateTime FechaUltimaVisita { get; set; }
        public int TotalVisitas { get; set; }
    }
}
