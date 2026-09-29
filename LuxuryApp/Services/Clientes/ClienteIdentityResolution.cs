namespace LuxuryApp.Services.Clientes
{
    /// <summary>
    /// Resultado semántico de intentar identificar a un cliente. NO contiene decisiones de UI:
    /// la capa de aplicación (calendario, reservas) decide qué hacer con cada estado y la vista
    /// solo lo presenta.
    /// </summary>
    public enum ClienteIdentityStatus
    {
        /// <summary>No hay teléfono utilizable: el nombre por sí solo NO identifica a nadie.</summary>
        InsufficientData = 0,

        /// <summary>El teléfono no corresponde a ningún cliente del negocio.</summary>
        NotFound = 1,

        /// <summary>Un único cliente con ese teléfono y el mismo nombre.</summary>
        ExistingExactMatch = 2,

        /// <summary>Un único cliente con ese teléfono, pero el nombre escrito difiere.</summary>
        ExistingPhoneMatchWithDifferentName = 3,

        /// <summary>Dos o más clientes comparten ese teléfono: hay que elegir, no adivinar.</summary>
        AmbiguousPhoneMatch = 4
    }

    /// <summary>Datos canónicos del cliente encontrado (los persistidos, no los escritos en el formulario).</summary>
    public sealed record ClienteIdentityMatch(
        int ClienteId,
        string Nombre,
        string NumeroTelefono,
        string? CorreoElectronico,
        bool AceptaMensajesWhatsApp);

    public sealed record ClienteIdentityResolution(
        ClienteIdentityStatus Status,
        IReadOnlyList<ClienteIdentityMatch> Matches)
    {
        public static ClienteIdentityResolution InsufficientData { get; } =
            new(ClienteIdentityStatus.InsufficientData, Array.Empty<ClienteIdentityMatch>());

        public static ClienteIdentityResolution NotFound { get; } =
            new(ClienteIdentityStatus.NotFound, Array.Empty<ClienteIdentityMatch>());

        /// <summary>
        /// Cliente único al que se puede vincular sin preguntar nada. Es <c>null</c> cuando hay
        /// ambigüedad: <b>nunca</b> se elige arbitrariamente entre duplicados históricos.
        /// </summary>
        public ClienteIdentityMatch? SingleMatch =>
            Status is ClienteIdentityStatus.ExistingExactMatch or ClienteIdentityStatus.ExistingPhoneMatchWithDifferentName
                ? Matches[0]
                : null;

        public bool IsAmbiguous => Status == ClienteIdentityStatus.AmbiguousPhoneMatch;
    }

    /// <summary>Qué debe pasar con el Cliente al guardar la cita. El backend siempre re-resuelve.</summary>
    public enum ClienteLinkMode
    {
        /// <summary>Vincula si el teléfono identifica a un único cliente. Nunca crea.</summary>
        Automatico = 0,

        /// <summary>Vincula si ya existe; si no existe, crea el cliente y lo vincula.</summary>
        Registrar = 1,

        /// <summary>Ni vincula ni crea: la cita queda con el nombre/teléfono escritos.</summary>
        SinVincular = 2
    }

    /// <summary>Datos para dar de alta un cliente desde el flujo de una cita.</summary>
    public sealed record ClienteRegistrationRequest(
        string Nombre,
        string Telefono,
        bool AceptaMensajesWhatsApp,
        string? ConsentSource,
        DateTime? ConsentCapturedAtUtc,
        string? ConsentCapturedByUserId);
}
