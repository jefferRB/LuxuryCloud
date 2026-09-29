using LuxuryApp.Services.Clientes;

namespace LuxuryApp.Models.Calendar
{
    public class CalendarUpsertRequest
    {
        public string? NombreCliente { get; init; }

        public string? TelefonoCliente { get; init; }

        public int? ClienteId { get; init; }

        /// <summary>
        /// Qué hacer con el Cliente al guardar. El backend SIEMPRE vuelve a resolver la identidad
        /// por teléfono dentro de la transacción: lo que viene del navegador es una intención, no
        /// una conclusión. Ver <see cref="ClienteLinkMode"/>.
        /// </summary>
        public ClienteLinkMode ClienteLinkMode { get; init; } = ClienteLinkMode.Automatico;

        public int? ServicioId { get; init; }

        public bool EsServicioPersonalizado { get; init; }

        public string? ServicioNombrePersonalizado { get; init; }

        public DateTime FechaHoraCita { get; init; }

        public int FuncionarioId { get; init; }

        /// <summary>
        /// Colaboradores a los que se aplica un DESCANSO. Un descanso para varias personas se
        /// guarda como una fila por persona (el modelo de siempre), no como una entidad nueva.
        ///
        /// <para>
        /// Solo se honra cuando <see cref="Tipo"/> es <c>DESCANSO</c>: una CITA sigue siendo de UN
        /// funcionario y la normalización la reduce a <see cref="FuncionarioId"/>, de modo que un
        /// formulario que arrastre ids viejos no puede convertir una cita en multi-funcionario.
        /// </para>
        /// </summary>
        public IReadOnlyList<int> FuncionarioIds { get; init; } = Array.Empty<int>();

        public string Tipo { get; init; } = "CITA";

        public int? DuracionMinutos { get; init; }

        public bool WhatsAppConsentAtCreation { get; init; }

        public string? WhatsAppConsentSource { get; init; }

        public DateTime? WhatsAppConsentCapturedAtUtc { get; init; }

        // Autorización de WhatsApp otorgada desde el formulario de la cita para un cliente
        // existente. Se aplica al Cliente dentro de la misma transacción que guarda la cita.
        public bool AutorizarWhatsAppAlGuardar { get; init; }

        // Usuario autenticado que capturó la autorización (auditoría). Se resuelve en el
        // servidor desde los claims; nunca proviene del cuerpo enviado por el navegador.
        public string? WhatsAppConsentCapturedByUserId { get; init; }

        public bool Duplicar { get; init; }

        public IReadOnlyList<string> FechasDuplicadas { get; init; } = Array.Empty<string>();
    }
}
