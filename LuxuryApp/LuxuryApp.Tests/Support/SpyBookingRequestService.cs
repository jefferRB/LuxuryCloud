using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.Reservas;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Espía de <see cref="IBookingRequestService"/>. Permite comprobar que el calendario NO
    /// reimplementa confirmar/rechazar, sino que delega en el mismo servicio de aplicación que
    /// usa la pantalla de solicitudes de reserva.
    /// </summary>
    internal sealed class SpyBookingRequestService : IBookingRequestService
    {
        public int ConfirmCount { get; private set; }
        public int RejectCount { get; private set; }

        public int? LastConfirmedId { get; private set; }
        public int? LastRejectedId { get; private set; }
        public int? LastFuncionarioOverride { get; private set; }
        public string? LastReason { get; private set; }
        public string? LastUserId { get; private set; }
        public DateOnly? LastPendingCalendarDate { get; private set; }
        public BookingClienteChoice? LastClienteChoice { get; private set; }
        public int PreviewCount { get; private set; }
        public int? LastPreviewId { get; private set; }

        public BookingClientePreview? PreviewResult { get; set; }

        public BookingActionResult ConfirmResult { get; set; } =
            BookingActionResult.Ok("Reserva aprobada y cita creada.", citaId: 55, whatsAppStatus: "sent");

        public BookingActionResult RejectResult { get; set; } =
            BookingActionResult.Ok("Solicitud rechazada.");

        public IReadOnlyList<CalendarPendingBookingResponse> PendingForCalendar { get; set; } =
            Array.Empty<CalendarPendingBookingResponse>();

        public Task<BookingRequestsPageViewModel> BuildPageAsync(
            string? estado,
            string? rango,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BookingRequestsPageViewModel());

        public Task<BookingClientePreview?> PreviewClienteAsync(
            int requestId,
            CancellationToken cancellationToken = default)
        {
            PreviewCount++;
            LastPreviewId = requestId;
            return Task.FromResult(PreviewResult);
        }

        public Task<BookingActionResult> ConfirmAsync(
            int requestId,
            int? funcionarioIdOverride,
            string? userId,
            BookingClienteChoice? clienteChoice = null,
            CancellationToken cancellationToken = default)
        {
            ConfirmCount++;
            LastConfirmedId = requestId;
            LastFuncionarioOverride = funcionarioIdOverride;
            LastUserId = userId;
            LastClienteChoice = clienteChoice;
            return Task.FromResult(ConfirmResult);
        }

        public Task<BookingActionResult> RejectAsync(
            int requestId,
            string? reason,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            RejectCount++;
            LastRejectedId = requestId;
            LastReason = reason;
            LastUserId = userId;
            return Task.FromResult(RejectResult);
        }

        public Task<IReadOnlyList<CalendarPendingBookingResponse>> GetPendingForCalendarAsync(
            DateOnly fecha,
            CancellationToken cancellationToken = default)
        {
            LastPendingCalendarDate = fecha;
            return Task.FromResult(PendingForCalendar);
        }
    }
}
