namespace LuxuryApp.Models.Calendar
{
    /// <summary>
    /// Proyección de una solicitud de reserva online PENDIENTE para el calendario.
    ///
    /// <para>
    /// Es un read model, no una cita: el calendario la pinta como bloque propio y nunca se crea
    /// una <c>Cita</c> provisional para conseguirlo. La cita real sólo existe cuando el negocio
    /// confirma la solicitud.
    /// </para>
    /// </summary>
    public sealed class CalendarPendingBookingResponse
    {
        /// <summary>Id de la BookingRequest (no de una cita).</summary>
        public int Id { get; init; }

        /// <summary>Funcionario cuya agenda ocupa la solicitud (el reservado por el servidor).</summary>
        public int FuncionarioId { get; init; }

        public string FuncionarioNombre { get; init; } = string.Empty;

        public string NombreCliente { get; init; } = string.Empty;

        public string? TelefonoCliente { get; init; }

        public string? ServicioNombre { get; init; }

        /// <summary>Inicio en hora local del negocio, igual criterio que <c>Cita.FechaHoraCita</c>.</summary>
        public DateTime FechaHoraInicio { get; init; }

        public int DuracionMinutos { get; init; }

        /// <summary>True si el cliente pidió "cualquier profesional" y el servidor eligió uno.</summary>
        public bool SolicitoCualquierFuncionario { get; init; }

        public bool AceptaWhatsApp { get; init; }

        public string? NotasCliente { get; init; }

        public DateTime CreatedAtUtc { get; init; }
    }
}
