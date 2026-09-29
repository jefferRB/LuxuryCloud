using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.WhatsApp;

namespace LuxuryApp.Models.Calendar
{
    public class Cita : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }
        public int Id { get; set; }

        [MaxLength(100)]
        public string? NombreCliente { get; set; }

        [MaxLength(20)]
        public string? TelefonoCliente { get; set; }

        public int? ClienteId { get; set; }

        public ClientesModel? Cliente { get; set; }

        public int? ServicioId { get; set; }
        public Servicio? Servicio { get; set; }

        // Servicio personalizado: cuando la cita no usa el catálogo (ServicioId == null)
        // y el usuario escribió un nombre libre. La duración se guarda en DuracionMinutos.
        [MaxLength(100)]
        public string? ServicioNombrePersonalizado { get; set; }

        [Required]
        public DateTime FechaHoraCita { get; set; }

        public string Tipo { get; set; } = "CITA";
        public int? DuracionMinutos { get; set; }


        public bool ConfirmacionEnviada { get; set; }
        public bool Recordatorio24hEnviado { get; set; }
        public bool Recordatorio3hEnviado { get; set; }
        public bool VisitaProcesada { get; set; } = false;

        [MaxLength(30)]
        public string EstadoConfirmacionWhatsApp { get; set; } = WhatsAppConfirmationStates.Pendiente;

        public DateTime? ConfirmacionWhatsAppEnviadaUtc { get; set; }

        public DateTime? RecordatorioWhatsAppTresHorasEnviadoUtc { get; set; }

        public DateTime? ConfirmadaPorWhatsAppUtc { get; set; }

        public DateTime? CanceladaPorWhatsAppUtc { get; set; }

        [MaxLength(128)]
        public string? UltimoMetaMessageId { get; set; }

        public DateTime? UltimaRespuestaWhatsAppUtc { get; set; }

        public bool WhatsAppConsentAtCreation { get; set; }

        [MaxLength(80)]
        public string? WhatsAppConsentSource { get; set; }

        public DateTime? WhatsAppConsentCapturedAtUtc { get; set; }

        // FK
        public int FuncionarioId { get; set; }

        // Navigation property
        public Funcionario? Funcionario { get; set; }

        // para almuerzos y breaks




    }
}
