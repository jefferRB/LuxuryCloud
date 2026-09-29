using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.WhatsApp;

namespace LuxuryApp.Services.WhatsApp
{
    public sealed record WhatsAppConsentDecision(
        bool CanSend,
        string Source,
        bool HasClienteId,
        string Message);

    /// <summary>
    /// Regla unica de consentimiento para escribirle a un cliente por WhatsApp. Es una funcion
    /// pura: quien la usa se encarga de leer el flag del cliente registrado (una sola consulta) y
    /// esta clase decide. Asi confirmaciones, recordatorios y cancelaciones comparten exactamente
    /// el mismo criterio en vez de tener cada una su propia version.
    /// </summary>
    public static class WhatsAppConsentPolicy
    {
        /// <param name="clienteAceptaMensajesWhatsApp">
        /// Valor de <c>ClientesModel.AceptaMensajesWhatsApp</c> cuando la cita esta ligada a un
        /// cliente registrado; <c>null</c> cuando no hay cliente registrado (o ya no existe).
        /// </param>
        public static WhatsAppConsentDecision Evaluate(Cita cita, bool? clienteAceptaMensajesWhatsApp)
        {
            ArgumentNullException.ThrowIfNull(cita);

            if (cita.ClienteId.HasValue)
            {
                return clienteAceptaMensajesWhatsApp == true
                    ? new WhatsAppConsentDecision(
                        CanSend: true,
                        Source: WhatsAppConsentSources.ClienteRegistrado,
                        HasClienteId: true,
                        Message: string.Empty)
                    : new WhatsAppConsentDecision(
                        CanSend: false,
                        Source: WhatsAppConsentSources.ClienteRegistrado,
                        HasClienteId: true,
                        Message: "El cliente no autorizó mensajes de WhatsApp.");
            }

            var source = ResolveSource(cita);
            return cita.WhatsAppConsentAtCreation
                ? new WhatsAppConsentDecision(
                    CanSend: true,
                    Source: source,
                    HasClienteId: false,
                    Message: string.Empty)
                : new WhatsAppConsentDecision(
                    CanSend: false,
                    Source: source,
                    HasClienteId: false,
                    Message: "El cliente no autorizó mensajes de WhatsApp.");
        }

        public static string ResolveSource(Cita cita)
        {
            ArgumentNullException.ThrowIfNull(cita);

            if (cita.ClienteId.HasValue)
            {
                return WhatsAppConsentSources.ClienteRegistrado;
            }

            if (!string.IsNullOrWhiteSpace(cita.WhatsAppConsentSource))
            {
                return cita.WhatsAppConsentSource!;
            }

            return cita.WhatsAppConsentAtCreation
                ? WhatsAppConsentSources.CitaManual
                : WhatsAppConsentSources.SinConsentimiento;
        }
    }
}
