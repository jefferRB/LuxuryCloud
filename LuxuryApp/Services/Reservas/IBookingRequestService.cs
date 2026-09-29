using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Reservas;

namespace LuxuryApp.Services.Reservas
{
    public sealed class BookingActionResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public int? CitaId { get; init; }

        /// <summary>
        /// Estado del envío de la confirmación por WhatsApp tras aprobar la reserva:
        /// "sent" | "pending" | "skipped" | "failed" | null (no aplica). Solo para la UI.
        /// </summary>
        public string? WhatsAppStatus { get; init; }

        public static BookingActionResult Ok(string message, int? citaId = null, string? whatsAppStatus = null) =>
            new() { Success = true, Message = message, CitaId = citaId, WhatsAppStatus = whatsAppStatus };

        public static BookingActionResult Fail(string message) =>
            new() { Success = false, Message = message };
    }

    public interface IBookingRequestService
    {
        Task<BookingRequestsPageViewModel> BuildPageAsync(
            string? estado,
            string? rango,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Estado del Cliente asociado a una solicitud pendiente, para que la pantalla sepa si
        /// tiene que preguntar algo antes de confirmar. Es una consulta ADMINISTRATIVA: el
        /// formulario público jamás puede averiguar si un teléfono está en la base de Clientes.
        /// </summary>
        Task<BookingClientePreview?> PreviewClienteAsync(
            int requestId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Confirma una solicitud: revalida disponibilidad, crea la cita real reutilizando el
        /// servicio del calendario (que dispara el flujo de WhatsApp existente si aplica) y marca
        /// la solicitud como Confirmed. funcionarioIdOverride permite asignar un funcionario cuando
        /// la solicitud era "cualquiera".
        ///
        /// <para>
        /// <paramref name="clienteChoice"/> es lo que eligió el administrador sobre el Cliente
        /// (registrar / vincular / ninguno). La resolución final la hace el servidor dentro de la
        /// transacción que crea la cita, así que una decisión obsoleta no puede crear duplicados.
        /// </para>
        /// </summary>
        Task<BookingActionResult> ConfirmAsync(
            int requestId,
            int? funcionarioIdOverride,
            string? userId,
            BookingClienteChoice? clienteChoice = null,
            CancellationToken cancellationToken = default);

        Task<BookingActionResult> RejectAsync(
            int requestId,
            string? reason,
            string? userId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Solicitudes PENDIENTES de un día, proyectadas para pintarlas en el calendario. Es un
        /// read model: no crea citas provisionales ni expone la entidad. Sólo devuelve las que
        /// tienen un funcionario reservado, porque son las que ocupan una columna de la agenda.
        /// </summary>
        Task<IReadOnlyList<CalendarPendingBookingResponse>> GetPendingForCalendarAsync(
            DateOnly fecha,
            CancellationToken cancellationToken = default);
    }
}
