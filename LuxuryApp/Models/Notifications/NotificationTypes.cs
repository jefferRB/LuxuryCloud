namespace LuxuryApp.Models.Notifications
{
    /// <summary>
    /// Tipos de notificación del Centro de Notificaciones. Se guardan como string legible en BD
    /// para que las consultas y el panel sean fáciles de interpretar y extender en el futuro.
    /// </summary>
    public static class NotificationTypes
    {
        /// <summary>Llegó una nueva solicitud de reserva desde el link público.</summary>
        public const string BookingRequestReceived = "BookingRequestReceived";

        /// <summary>Una cita fue cancelada por el cliente respondiendo el WhatsApp de confirmación.</summary>
        public const string AppointmentCancelledViaWhatsApp = "AppointmentCancelledViaWhatsApp";
    }

    /// <summary>
    /// Tipo de entidad que originó la notificación. Junto con <c>EntityId</c> y <c>Type</c>
    /// forma la llave lógica que evita duplicados ante reintentos.
    /// </summary>
    public static class NotificationEntityTypes
    {
        public const string BookingRequest = "BookingRequest";
        public const string Cita = "Cita";
    }

    /// <summary>Origen que generó la notificación (auditoría ligera).</summary>
    public static class NotificationSources
    {
        public const string System = "System";
        public const string PublicBooking = "PublicBooking";
        public const string WhatsApp = "WhatsApp";
    }
}
