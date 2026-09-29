using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.DataBase
{
    public class ClientesModel : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string NumeroTelefono { get; set; } = string.Empty;

        public bool AceptaMensajesWhatsApp { get; set; }

        public DateTime? WhatsAppConsentUpdatedAtUtc { get; set; }

        [StringLength(80)]
        public string? WhatsAppConsentSource { get; set; }

        [StringLength(450)]
        public string? WhatsAppConsentCapturedByUserId { get; set; }

        [StringLength(40)]
        public string? WhatsAppConsentTextVersion { get; set; }

        [EmailAddress]
        [StringLength(256)]
        public string? CorreoElectronico { get; set; }

        [Required]
        [StringLength(150)]
        public string Nombre { get; set; } = string.Empty;

        [Range(1, int.MaxValue, ErrorMessage = "La frecuencia de visita debe ser mayor a cero.")]
        public int FrecuenciaVisita { get; set; }

        public DateTime FechaUltimaVisita { get; set; }

        public DateTime? FechaCumpleaños { get; set; }
        public string? DescripcionServiciosRealizados { get; set; }

        public ICollection<ClienteVisitas> Visitas { get; set; } = new List<ClienteVisitas>();

        public ICollection<Cita> Citas { get; set; } = new List<Cita>();

        public ICollection<ClienteServicioRealizado> ServiciosRealizados { get; set; } = new List<ClienteServicioRealizado>();
    }
}
