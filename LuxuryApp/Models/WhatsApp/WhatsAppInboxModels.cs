namespace LuxuryApp.Models.WhatsApp
{
    /// <summary>Respuesta de la bandeja WhatsApp para un día/tenant.</summary>
    public sealed class WhatsAppInboxResponse
    {
        public bool WhatsAppEnabled { get; init; }

        public WhatsAppInboxStats Stats { get; init; } = new();

        public IReadOnlyList<WhatsAppInboxItem> Items { get; init; } = new List<WhatsAppInboxItem>();
    }

    public sealed class WhatsAppInboxStats
    {
        public int Enviados { get; init; }

        public int Confirmados { get; init; }

        public int Pendientes { get; init; }

        public int Fallidos { get; init; }
    }

    /// <summary>
    /// Una cita del día con su estado de seguimiento WhatsApp.
    /// Sirve tanto a la bandeja (tarjetas) como a la agenda del día (tabla).
    /// </summary>
    public sealed class WhatsAppInboxItem
    {
        public int CitaId { get; init; }

        public string NombreCliente { get; init; } = string.Empty;

        public string Iniciales { get; init; } = string.Empty;

        public string? Telefono { get; init; }

        public bool TieneTelefono { get; init; }

        public string ServicioNombre { get; init; } = string.Empty;

        public string FuncionarioNombre { get; init; } = string.Empty;

        public DateTime FechaHoraCita { get; init; }

        public string HoraLocal { get; init; } = string.Empty;

        // Fecha local corta (dd/MM/yyyy) — usada por el seguimiento por rango.
        public string FechaLocal { get; init; } = string.Empty;

        // Agrupador de día para el Centro de confirmaciones: "Hoy" / "Mañana" / "dd MMM".
        public string DiaGrupo { get; init; } = string.Empty;

        // true cuando la cita aún no ha pasado (FechaHoraCita >= ahora del negocio).
        // El panel "Citas de hoy" usa esto para ocultar citas ya vencidas.
        public bool EsFutura { get; init; }

        // Estado de la cita (Confirmada / Pendiente / Cancelada) derivado de EstadoConfirmacionWhatsApp.
        public string EstadoCitaKey { get; init; } = string.Empty;

        public string EstadoCitaLabel { get; init; } = string.Empty;

        // Estado del mensaje WhatsApp (machine key + etiqueta visible + subtexto).
        public string WaStatusKey { get; init; } = string.Empty;

        public string WaStatusLabel { get; init; } = string.Empty;

        public string WaSubText { get; init; } = string.Empty;

        // Marca de "requiere atención" (fallido, sin teléfono o sin autorización en cita activa).
        public bool RequiereAtencion { get; init; }

        // Reglas de acción calculadas en servidor.
        public bool PuedeEnviar { get; init; }

        public bool PuedeReenviar { get; init; }

        public bool TieneEnvioWhatsApp { get; init; }

        public bool TieneFalloWhatsApp { get; init; }
    }

    /// <summary>
    /// Respuesta del "Centro de confirmaciones WhatsApp": seguimiento por rango de fechas
    /// con KPIs agregados. Sólo se sirve a tenants con add-on WhatsApp.
    /// </summary>
    public sealed class WhatsAppFollowUpResponse
    {
        public bool WhatsAppEnabled { get; init; }

        public string RangeKey { get; init; } = string.Empty;

        public string FromLocal { get; init; } = string.Empty;

        public string ToLocal { get; init; } = string.Empty;

        public WhatsAppFollowUpStats Stats { get; init; } = new();

        public IReadOnlyList<WhatsAppInboxItem> Items { get; init; } = new List<WhatsAppInboxItem>();
    }

    public sealed class WhatsAppFollowUpStats
    {
        public int TotalTracking { get; init; }

        public int Confirmed { get; init; }

        public int Pending { get; init; }

        public int Sent { get; init; }

        public int Failed { get; init; }

        public int RequiresAttention { get; init; }

        public decimal ConfirmationRate { get; init; }
    }

    public sealed class WhatsAppChatLogItem
    {
        public DateTime FechaHoraUtc { get; init; }

        public string FechaHoraLocal { get; init; } = string.Empty;

        public string Direccion { get; init; } = string.Empty;

        public string Tipo { get; init; } = string.Empty;

        public string Estado { get; init; } = string.Empty;

        public string? Error { get; init; }

        // MetaMessageId enmascarado (nunca se exponen tokens ni payload sensible).
        public string? ReferenciaMensaje { get; init; }
    }
}
