using LuxuryApp.Models.Calendar;

namespace LuxuryApp.Services.Calendar
{
    public interface ICalendarCommandService
    {
        Task<CalendarAppointmentResponse> CreateAsync(CalendarUpsertRequest request, CancellationToken cancellationToken = default);

        Task<CalendarAppointmentResponse> UpdateAsync(int id, CalendarUpsertRequest request, CancellationToken cancellationToken = default);

        Task MoveAsync(int id, CalendarMoveRequest request, CancellationToken cancellationToken = default);

        Task ResizeDurationAsync(int id, int duracionMinutos, CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancela (elimina) una entrada de agenda.
        /// </summary>
        /// <param name="motivoCancelacion">
        /// Motivo que escribió quien cancela. Solo se usa para el aviso de WhatsApp de las citas
        /// que provienen de una reserva online; si viene vacío se usa un texto neutro por defecto.
        /// </param>
        Task DeleteAsync(int id, string? motivoCancelacion = null, CancellationToken cancellationToken = default);

        Task ProcessVisitsAsync(CancellationToken cancellationToken = default);
    }
}
