using System;
using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Calendar
{
    public class CitaCreateVM
    {
        [StringLength(100)]
        public string? NombreCliente { get; set; }

        [StringLength(20)]
        public string? TelefonoCliente { get; set; }

        public int? ClienteId { get; set; }

        // Intención del formulario: "registrá también al cliente". Es solo una intención — el
        // backend vuelve a resolver la identidad por teléfono antes de crear nada, así que marcar
        // esta casilla nunca puede producir un duplicado de un cliente que ya existe.
        public bool RegistrarCliente { get; set; }

        public int? ServicioId { get; set; }

        // Servicio personalizado (no pertenece al catálogo). Cuando es true se usa
        // ServicioNombrePersonalizado + DuracionMinutos en lugar de ServicioId.
        public bool EsServicioPersonalizado { get; set; }

        [StringLength(100)]
        public string? ServicioNombrePersonalizado { get; set; }

        [Required]
        public DateTime FechaHoraCita { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Debe seleccionar un funcionario válido.")]
        public int FuncionarioId { get; set; }

        // Colaboradores adicionales de un DESCANSO. El backend valida cada id contra el tenant
        // actual y lo ignora por completo cuando el tipo es CITA.
        public List<int> FuncionarioIds { get; set; } = new();

        [Required]
        [StringLength(20)]
        public string Tipo { get; set; } = "CITA";

        // Duración en minutos para descansos (5-180) y para servicios personalizados (5-480).
        // El rango específico se valida del lado del servidor según el tipo.
        [Range(5, 480, ErrorMessage = "La duración debe estar entre 5 y 480 minutos.")]
        public int? DuracionMinutos { get; set; }

        public bool WhatsAppConsentAtCreation { get; set; }

        [StringLength(80)]
        public string? WhatsAppConsentSource { get; set; }

        public DateTime? WhatsAppConsentCapturedAtUtc { get; set; }

        // Cliente existente que acaba de autorizar WhatsApp desde el formulario de la cita.
        // Campo específico (anti-overposting): nunca representa el valor persistido del cliente,
        // solo la intención de otorgar el consentimiento al guardar. Desmarcado = sin cambios.
        public bool AutorizarWhatsAppAlGuardar { get; set; }

        public bool Duplicar { get; set; }

        public List<string> FechasDuplicadas { get; set; } = new();

    }
}
