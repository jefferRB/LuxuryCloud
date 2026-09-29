namespace LuxuryApp.Models.Reservas
{
    /// <summary>
    /// Estados de una solicitud de reserva online. Se guardan como string legible en BD
    /// para que las consultas y el panel privado sean fáciles de interpretar.
    /// </summary>
    public static class BookingRequestStates
    {
        public const string Pending = "Pending";
        public const string Confirmed = "Confirmed";
        public const string Rejected = "Rejected";
        public const string Expired = "Expired";
        public const string CancelledByClient = "CancelledByClient";

        public static readonly IReadOnlyCollection<string> All =
        [
            Pending,
            Confirmed,
            Rejected,
            Expired,
            CancelledByClient
        ];

        public static bool IsKnown(string? estado) =>
            !string.IsNullOrWhiteSpace(estado) && All.Contains(estado);
    }

    /// <summary>
    /// Valores por defecto del rechazo de una solicitud.
    /// </summary>
    public static class BookingRejectionDefaults
    {
        /// <summary>
        /// Motivo con el que la pantalla precarga el campo y con el que el backend completa si el
        /// request llega sin motivo utilizable. Viaja al cliente en {{6}} de
        /// <c>luxurycloud_cancelacion_cita</c>, así que nunca puede quedar vacío.
        /// </summary>
        public const string MotivoPorDefecto = "El colaborador no está disponible.";

        /// <summary>Límite de <see cref="BookingRequest.RejectedReason"/>.</summary>
        public const int MotivoMaxLength = 300;

        /// <summary>
        /// Deja el motivo listo para persistir y para enviar: recorta, limita y cae al valor por
        /// defecto. No se confía en el <c>required</c> del HTML: la garantía vive acá.
        /// </summary>
        public static string NormalizarMotivo(string? motivo)
        {
            if (string.IsNullOrWhiteSpace(motivo))
            {
                return MotivoPorDefecto;
            }

            var limpio = motivo.Trim();
            return limpio.Length <= MotivoMaxLength ? limpio : limpio[..MotivoMaxLength];
        }
    }

    /// <summary>
    /// Origen de la solicitud. En Fase 1 solo existe el link público, pero se deja
    /// preparado para futuros canales (WhatsApp, recepción, etc.).
    /// </summary>
    public static class BookingRequestOrigins
    {
        public const string PublicLink = "PublicLink";
    }

    /// <summary>
    /// Modos de operación de las reservas online. En Fase 1 solo aprobación manual:
    /// el cliente solicita y el negocio confirma o rechaza desde la plataforma privada.
    /// </summary>
    public static class PublicBookingModes
    {
        public const string ManualApproval = "ManualApproval";
    }
}
