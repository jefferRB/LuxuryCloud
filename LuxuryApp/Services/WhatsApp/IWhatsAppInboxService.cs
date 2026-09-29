using LuxuryApp.Models.WhatsApp;

namespace LuxuryApp.Services.WhatsApp
{
    public interface IWhatsAppInboxService
    {
        Task<WhatsAppInboxResponse> GetInboxAsync(
            DateTime date,
            int? funcionarioId,
            bool whatsAppEnabled,
            CancellationToken cancellationToken = default);

        Task<bool> FuncionarioExistsForCurrentTenantAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Seguimiento de confirmaciones WhatsApp en un rango de fechas (Centro de confirmaciones).
        /// <paramref name="toExclusive"/> es exclusivo. <paramref name="statusKey"/> filtra por grupo de
        /// estado (confirmados/pendientes/enviados/fallidos/cancelados/atencion); vacío = todos.
        /// </summary>
        Task<WhatsAppFollowUpResponse> GetFollowUpAsync(
            DateTime from,
            DateTime toExclusive,
            int? funcionarioId,
            string? statusKey,
            string rangeKey,
            bool whatsAppEnabled,
            CancellationToken cancellationToken = default);

        /// <summary>Historial de mensajes/logs de una cita (para "Ver chat"). Devuelve null si la cita no pertenece al tenant.</summary>
        Task<IReadOnlyList<WhatsAppChatLogItem>?> GetCitaChatAsync(int citaId, CancellationToken cancellationToken = default);
    }
}
