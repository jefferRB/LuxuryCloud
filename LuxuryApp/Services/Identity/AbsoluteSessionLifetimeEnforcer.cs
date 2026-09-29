using System.Globalization;

namespace LuxuryApp.Services.Identity
{
    /// <summary>
    /// Aplica el límite absoluto de vida de una sesión persistente. La expiración
    /// deslizante (sliding) renueva la cookie mientras el usuario sigue activo, pero por
    /// sí sola podría mantenerla viva indefinidamente. Este servicio impone un tope duro
    /// de 90 días contados desde la autenticación original.
    ///
    /// La marca inmutable se guarda dentro de <c>AuthenticationProperties.Items</c> del
    /// ticket cifrado y firmado por Data Protection (no en un claim regenerado por la
    /// fábrica de claims ni en un valor enviado por el cliente). Al vivir en las
    /// propiedades del ticket sobrevive a: renovación por sliding, regeneración del
    /// principal por el validador de security stamp y <c>RefreshSignInAsync</c>. Un ticket
    /// manipulado no descifra, por lo que el valor nunca puede falsificarse.
    /// </summary>
    public sealed class AbsoluteSessionLifetimeEnforcer
    {
        /// <summary>
        /// Clave dentro de <c>AuthenticationProperties.Items</c> que guarda la fecha UTC
        /// (formato "O", round-trip) de la autenticación original.
        /// </summary>
        public const string SessionStartedItemKey = "auth_session_started_utc";

        /// <summary>Tope duro desde la autenticación original, sin importar la actividad.</summary>
        public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(90);

        // Tolerancia para relojes ligeramente adelantados entre nodos; una marca más allá
        // de este margen en el futuro se considera corrupta y se rechaza.
        private static readonly TimeSpan FutureSkewTolerance = TimeSpan.FromMinutes(5);

        private readonly TimeProvider _clock;

        public AbsoluteSessionLifetimeEnforcer(TimeProvider clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public enum Decision
        {
            /// <summary>La marca existe, es válida y está dentro del tope de 90 días.</summary>
            WithinLimit,

            /// <summary>Superó los 90 días, es una marca corrupta o con fecha futura: cerrar sesión.</summary>
            Expired,

            /// <summary>
            /// No hay marca todavía: inicializarla. Dos escenarios distintos, ambos seguros:
            /// (1) sesión nueva (login con contraseña, 2FA o registro): la marca se fija en el
            /// primer request autenticado posterior a completar el login, prácticamente en el
            /// momento de autenticación. (2) cookie legacy emitida ANTES del despliegue y sin la
            /// marca: el tope absoluto arranca en el primer request posterior al despliegue,
            /// porque esas cookies no contienen una fecha de inicio original confiable y NO se
            /// reconstruye ninguna fecha histórica inexistente.
            /// </summary>
            NeedsInitialization
        }

        /// <summary>
        /// Evalúa la marca almacenada en las propiedades del ticket. No muta la colección.
        /// </summary>
        public Decision Evaluate(IDictionary<string, string?>? ticketItems)
        {
            if (ticketItems is null ||
                !ticketItems.TryGetValue(SessionStartedItemKey, out var raw) ||
                string.IsNullOrWhiteSpace(raw))
            {
                return Decision.NeedsInitialization;
            }

            if (!DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var started))
            {
                // Presente pero ilegible: no puede provenir de un cliente (el ticket va
                // firmado), así que es un estado anómalo. Comportamiento seguro: rechazar.
                return Decision.Expired;
            }

            var now = _clock.GetUtcNow();
            var startedUtc = started.ToUniversalTime();

            if (startedUtc - now > FutureSkewTolerance)
            {
                return Decision.Expired;
            }

            if (now - startedUtc > AbsoluteLifetime)
            {
                return Decision.Expired;
            }

            return Decision.WithinLimit;
        }

        /// <summary>
        /// Fecha UTC actual serializada para sembrar la marca en un ticket nuevo o al
        /// backfillear una cookie que aún no la tenía.
        /// </summary>
        public string CreateStartMarker() =>
            _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    }
}
