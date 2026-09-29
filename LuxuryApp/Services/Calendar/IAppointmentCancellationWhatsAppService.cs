using LuxuryApp.Services.WhatsApp;

namespace LuxuryApp.Services.Calendar
{
    /// <summary>
    /// Aviso de cancelacion al cliente (<c>luxurycloud_cancelacion_cita</c>) para citas que
    /// nacieron de una reserva online.
    ///
    /// <para>
    /// Se parte en dos fases a proposito, porque cancelar una cita la ELIMINA de la agenda:
    /// <list type="number">
    ///   <item><see cref="PrepareAsync"/> corre ANTES del borrado, mientras todavia existe el
    ///   vinculo <c>BookingRequest.ConvertedCitaId</c> (que el borrado pone en NULL) y los datos
    ///   de la cita. Decide si corresponde avisar y reserva la fila del log.</item>
    ///   <item><see cref="SendAsync"/> corre DESPUES de que la cancelacion quedo confirmada en
    ///   base de datos. Si Meta falla, la cita sigue cancelada: el envio nunca revierte nada.</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface IAppointmentCancellationWhatsAppService
    {
        /// <summary>
        /// Devuelve el aviso listo para enviar, o <c>null</c> cuando no corresponde (cita que no
        /// vino de una reserva online, sin consentimiento, sin telefono, tenant sin WhatsApp,
        /// limite alcanzado, o aviso ya registrado). Debe llamarse dentro de la misma transaccion
        /// que elimina la cita: si la cancelacion se revierte, la reserva del log tambien.
        /// </summary>
        Task<PreparedAppointmentCancellation?> PrepareAsync(
            int citaId,
            string? motivoCancelacion,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Envia el aviso ya preparado y cierra su fila del log (Sent/Failed). No lanza: cualquier
        /// error queda registrado, nunca propagado a la cancelacion.
        /// </summary>
        Task SendAsync(
            PreparedAppointmentCancellation prepared,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Solo lectura: que va a pasar si se cancela esta cita. La agenda lo usa para avisarle al
        /// negocio ANTES de cancelar, con la MISMA regla que aplica <see cref="PrepareAsync"/> (una
        /// sola definicion de "¿se le avisa al cliente?").
        /// </summary>
        Task<AppointmentCancellationNoticePreview> PreviewAsync(
            int citaId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Aviso previo para la pantalla de cancelacion.
    /// </summary>
    /// <param name="TelefonoCliente">Telefono del CLIENTE, para contactarlo a mano. Nunca el del negocio.</param>
    /// <param name="Mensaje">Texto para el modal, ANTES de cancelar.</param>
    /// <param name="MensajeContactoManual">
    /// Aviso corto para DESPUES de cancelar cuando no hubo autorizacion. Vacio si sí se notifica.
    /// </param>
    public sealed record AppointmentCancellationNoticePreview(
        bool NotificaraPorWhatsApp,
        string? TelefonoCliente,
        string Mensaje,
        string MensajeContactoManual)
    {
        /// <summary>Cita que no vino de una reserva online (o descanso): no hay aviso que anunciar.</summary>
        public static readonly AppointmentCancellationNoticePreview NoAplica =
            new(
                NotificaraPorWhatsApp: false,
                TelefonoCliente: null,
                Mensaje: string.Empty,
                MensajeContactoManual: string.Empty);
    }

    /// <summary>
    /// Instantanea de todo lo necesario para enviar el aviso, tomada mientras la cita aun existia.
    /// </summary>
    /// <param name="MessageLogId">Fila de <c>WhatsAppMessageLogs</c> ya reservada en estado Pending.</param>
    /// <param name="TenantId">Tenant dueño de la cita. Se deriva de la entidad, nunca del cliente.</param>
    /// <param name="CitaId">Id de la cita cancelada. Solo para trazas: la fila ya no existe.</param>
    /// <param name="BookingRequestId">Reserva online que origino la cita.</param>
    public sealed record PreparedAppointmentCancellation(
        long MessageLogId,
        Guid TenantId,
        int CitaId,
        int BookingRequestId,
        string RecipientPhoneE164,
        WhatsAppCancellationTemplateParameters Parameters);
}
