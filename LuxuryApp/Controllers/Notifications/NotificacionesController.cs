using LuxuryApp.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Notifications
{
    /// <summary>
    /// Endpoints del Centro de Notificaciones (burbuja flotante). Todo es tenant-scoped por el
    /// global query filter: nunca devuelve datos de otro tenant. Solo el dueño del negocio.
    /// </summary>
    [Authorize(Roles = "Administrador")]
    [Route("[controller]")]
    public sealed class NotificacionesController : Controller
    {
        private readonly INotificationService _notificationService;

        public NotificacionesController(INotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        [HttpGet("Resumen")]
        public async Task<IActionResult> Resumen(CancellationToken cancellationToken)
        {
            var summary = await _notificationService.GetSummaryAsync(15, cancellationToken);

            return Json(new
            {
                unreadCount = summary.UnreadCount,
                notifications = summary.Notifications.Select(n => new
                {
                    id = n.Id,
                    type = n.Type,
                    icon = n.Icon,
                    title = n.Title,
                    message = n.Message,
                    createdAtLabel = n.CreatedAtLabel,
                    actionUrl = n.ActionUrl,
                    isRead = n.IsRead
                })
            });
        }

        [HttpPost("MarcarLeidas")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarcarLeidas(CancellationToken cancellationToken)
        {
            var marcadas = await _notificationService.MarkAllAsReadAsync(cancellationToken);
            return Json(new { success = true, marcadas });
        }

        [HttpPost("MarcarLeida/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarcarLeida(int id, CancellationToken cancellationToken)
        {
            var ok = await _notificationService.MarkAsReadAsync(id, cancellationToken);
            return Json(new { success = ok });
        }
    }
}
