using System.Text.Json;

namespace LuxuryApp.Services.WhatsApp
{
    /// <summary>
    /// Responde con un mensaje neutral a quien escriba al numero central de LuxuryCloud.
    ///
    /// <para>
    /// El numero solo emite notificaciones automaticas, asi que la respuesta es siempre la misma y
    /// deliberadamente ciega: no se averigua a que negocio pertenece quien escribe, no se consultan
    /// citas ni clientes, y no se menciona ningun nombre ni telefono de ningun tenant.
    /// </para>
    /// </summary>
    public interface IWhatsAppInboundAutoReplyService
    {
        /// <summary>
        /// Procesa el payload del webhook y responde a los mensajes que realmente escribio una
        /// persona. Nunca lanza: el webhook debe contestar 200 a Meta pase lo que pase.
        /// </summary>
        Task ProcessInboundMessagesAsync(JsonElement payload, CancellationToken cancellationToken = default);
    }
}
