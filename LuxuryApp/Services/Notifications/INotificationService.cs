using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Notifications;
using LuxuryApp.Models.Reservas;

namespace LuxuryApp.Services.Notifications
{
    /// <summary>
    /// Centro de Notificaciones interno (tenant-scoped). La generación de notificaciones se hace
    /// desde los servicios de dominio existentes; el panel solo consume el resumen y marca leídas.
    /// </summary>
    public interface INotificationService
    {
        /// <summary>Resumen para la burbuja: conteo de no leídas + historial reciente.</summary>
        Task<NotificationSummary> GetSummaryAsync(int limit = 15, CancellationToken cancellationToken = default);

        /// <summary>Marca como leídas todas las notificaciones no leídas del tenant actual. Devuelve cuántas cambió.</summary>
        Task<int> MarkAllAsReadAsync(CancellationToken cancellationToken = default);

        /// <summary>Marca una notificación puntual como leída. Tenant-scoped.</summary>
        Task<bool> MarkAsReadAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Notifica que llegó una nueva solicitud de reserva pública. Idempotente por
        /// (Type + EntityType + EntityId) dentro del tenant.
        /// </summary>
        Task CreateBookingRequestReceivedAsync(BookingRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Notifica que una cita fue cancelada desde el flujo de WhatsApp. Idempotente por
        /// (Type + EntityType + EntityId) dentro del tenant.
        /// </summary>
        Task CreateAppointmentCancelledViaWhatsAppAsync(Cita cita, CancellationToken cancellationToken = default);
    }
}
