using LuxuryApp.Services.Clientes;

namespace LuxuryApp.Services.Reservas
{
    /// <summary>
    /// Qué pidió el administrador que se haga con el Cliente al confirmar una reserva. Es la
    /// intención de la pantalla; la identidad la vuelve a resolver el servidor.
    /// </summary>
    public enum BookingClienteDecision
    {
        /// <summary>Sin decisión explícita: vincula solo si el teléfono identifica a un único cliente.</summary>
        Automatico = 0,

        /// <summary>"Confirmar y registrar": si no existe, se crea el cliente y se vincula.</summary>
        Registrar = 1,

        /// <summary>"Vincular cliente": se asocia al cliente elegido entre las coincidencias.</summary>
        Vincular = 2,

        /// <summary>"Confirmar sin registrar/vincular": la cita queda con los datos de la reserva.</summary>
        SinVincular = 3
    }

    /// <summary>Decisión + cliente elegido (solo para <see cref="BookingClienteDecision.Vincular"/>).</summary>
    public sealed record BookingClienteChoice(BookingClienteDecision Decision, int? ClienteId = null)
    {
        public static BookingClienteChoice Automatico { get; } = new(BookingClienteDecision.Automatico);

        /// <summary>
        /// Traduce el valor recibido del formulario. Cualquier cosa desconocida cae en
        /// <see cref="BookingClienteDecision.Automatico"/> (allowlist, nunca se confía en el texto).
        /// </summary>
        public static BookingClienteChoice Parse(string? decision, int? clienteId) => decision?.Trim().ToLowerInvariant() switch
        {
            "registrar" => new BookingClienteChoice(BookingClienteDecision.Registrar),
            "vincular" => new BookingClienteChoice(BookingClienteDecision.Vincular, clienteId),
            "sinvincular" => new BookingClienteChoice(BookingClienteDecision.SinVincular),
            _ => Automatico
        };
    }

    /// <summary>
    /// Lo que la pantalla necesita saber ANTES de confirmar para decidir si pregunta algo.
    /// Se calcula en el servidor: la página pública de reservas nunca puede consultar esto.
    /// </summary>
    public sealed record BookingClientePreview(
        int RequestId,
        string NombreCliente,
        string TelefonoCliente,
        ClienteIdentityStatus Status,
        IReadOnlyList<ClienteIdentityMatch> Matches)
    {
        /// <summary>
        /// True cuando se puede confirmar de una sin preguntar nada: el cliente ya está registrado
        /// con ese mismo nombre, o no hay teléfono con el que identificar a nadie.
        /// </summary>
        public bool PuedeConfirmarDirecto =>
            Status is ClienteIdentityStatus.ExistingExactMatch or ClienteIdentityStatus.InsufficientData;
    }
}
