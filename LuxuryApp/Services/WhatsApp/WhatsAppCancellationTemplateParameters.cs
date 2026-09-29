namespace LuxuryApp.Services.WhatsApp
{
    /// <summary>
    /// Parametros del BODY de <c>luxurycloud_cancelacion_cita</c>, con nombre en vez de posicion.
    ///
    /// <para>
    /// El template de Meta es posicional ({{1}}…{{8}}) y no permite repetir la misma variable en
    /// dos lugares: por eso el nombre del negocio viaja dos veces, en {{2}} y en {{7}}. Construir
    /// el arreglo a mano en el punto de envio es justo donde se cuelan los errores de orden, asi
    /// que el orden vive en un solo lugar: <see cref="ToOrderedBodyParameters"/>.
    /// </para>
    /// </summary>
    public sealed record WhatsAppCancellationTemplateParameters
    {
        /// <summary>{{1}} Nombre del cliente.</summary>
        public required string CustomerName { get; init; }

        /// <summary>{{2}} Nombre del negocio.</summary>
        public required string BusinessName { get; init; }

        /// <summary>{{3}} Nombre del servicio.</summary>
        public required string ServiceName { get; init; }

        /// <summary>{{4}} Fecha de la cita (hora local del negocio).</summary>
        public required string AppointmentDate { get; init; }

        /// <summary>{{5}} Hora de la cita (hora local del negocio).</summary>
        public required string AppointmentTime { get; init; }

        /// <summary>{{6}} Motivo de la cancelacion.</summary>
        public required string CancellationReason { get; init; }

        /// <summary>
        /// {{7}} Nombre del negocio otra vez. Deliberadamente igual a <see cref="BusinessName"/>:
        /// Meta no admite reutilizar {{2}} en dos posiciones del cuerpo.
        /// </summary>
        public required string BusinessNameRepeated { get; init; }

        /// <summary>
        /// {{8}} Telefono publico/habitual del negocio. NUNCA el numero central de automatizaciones
        /// de LuxuryCloud: el mensaje invita al cliente a llamar al negocio, no a este numero.
        /// </summary>
        public required string BusinessPhone { get; init; }

        /// <summary>Parametros en el orden exacto que espera el template. Unico lugar que define el orden.</summary>
        public IReadOnlyList<string> ToOrderedBodyParameters() =>
        [
            CustomerName,
            BusinessName,
            ServiceName,
            AppointmentDate,
            AppointmentTime,
            CancellationReason,
            BusinessNameRepeated,
            BusinessPhone
        ];
    }
}
