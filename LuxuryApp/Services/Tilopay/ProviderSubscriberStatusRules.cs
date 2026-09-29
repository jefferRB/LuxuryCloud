namespace LuxuryApp.Services.Tilopay
{
    /// <summary>Qué significa el status de un suscriptor para el dinero: ¿puede cobrar o no?</summary>
    public enum ProviderSubscriberState
    {
        /// <summary>Cobrable: crear otro suscriptor para el mismo email/plan sería doble cobro.</summary>
        Active,

        /// <summary>Confirmado NO cobrable (eliminado/cancelado/inactivo): volver a ese plan es seguro.</summary>
        Inactive,

        /// <summary>
        /// Pausado en el proveedor (status 3, confirmado por soporte TiloPay). NO es cobrable
        /// ahora, pero tampoco está confirmado que no vuelva a cobrar al reactivarse, así que para
        /// decidir si un plan quedó "libre" NUNCA cuenta como baja: <see cref="MayStillCharge"/>
        /// devuelve true. Es un estado propio (no Unknown) para que la reactivación pueda
        /// distinguir "pausado, reactivable" de "status que no entendemos".
        /// </summary>
        Paused,

        /// <summary>No sabemos. NUNCA se asume libre: se manda a revisión manual.</summary>
        Unknown
    }

    /// <summary>
    /// Única fuente de verdad para interpretar el status de un suscriptor de TiloPay Repeat.
    ///
    /// Existe porque la lista de strings estaba duplicada en tres lugares y ninguno conocía todos
    /// los valores reales del proveedor. Caso de producción (2026-07-15): TiloPay devuelve
    /// <c>"Delete"</c> (singular) y el filtro solo reconocía <c>"deleted"</c>, así que un suscriptor
    /// ya eliminado se trató como activo y bloqueó un cambio de plan legítimo hacia ese plan.
    ///
    /// Regla de oro: ante un status que no reconocemos, NO se asume que el plan está libre. Crear
    /// un suscriptor nuevo sobre uno que quizá cobra es un doble cobro real; mandar el caso a
    /// soporte solo cuesta una revisión. Por eso <see cref="ProviderSubscriberState.Unknown"/>
    /// existe y no colapsa a Inactive.
    ///
    /// Al agregar un valor nuevo, agregarlo aquí y en ningún otro lado.
    /// </summary>
    public static class ProviderSubscriberStatusRules
    {
        /// <summary>Cobrable. TiloPay usa 1 y variantes de texto según el endpoint.</summary>
        private static readonly HashSet<string> ActiveStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "1",
            "active",
            "activo",
            "activa"
        };

        /// <summary>
        /// Confirmado no cobrable. Incluye "delete" (singular) además de "deleted": es el valor
        /// real que devolvió getSuscriptorRepeat en producción y el que originó el bug.
        /// </summary>
        private static readonly HashSet<string> InactiveStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "4",
            "delete",
            "deleted",
            "eliminado",
            "eliminada",
            "remove",
            "removed",
            "cancel",
            "cancelled",
            "canceled",
            "cancelado",
            "cancelada",
            "inactive",
            "inactivo",
            "inactiva"
        };

        /// <summary>
        /// Pausado (status 3, confirmado por soporte TiloPay). Estado propio, NO Inactive: un
        /// pausado puede volver a cobrar al reactivarse, así que nunca deja el plan "libre"
        /// (<see cref="MayStillCharge"/> sigue siendo true para pausados).
        /// </summary>
        private static readonly HashSet<string> PausedStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "3",
            "paused",
            "pausado",
            "pausada",
            "pause",
            // Valor real que devuelve getSuscriptorRepeat cuando el comercio pausó el suscriptor
            // (observado en la API del proveedor). Se incluyen las variantes con y sin
            // espacios; la comparación ya es case-insensitive + trim del string completo.
            "pause by commerce",
            "paused by commerce",
            "pausebycommerce",
            "pausedbycommerce"
        };

        public static ProviderSubscriberState Classify(string? status)
        {
            var normalized = status?.Trim();

            if (string.IsNullOrEmpty(normalized))
            {
                return ProviderSubscriberState.Unknown;
            }

            if (ActiveStatuses.Contains(normalized))
            {
                return ProviderSubscriberState.Active;
            }

            if (PausedStatuses.Contains(normalized))
            {
                return ProviderSubscriberState.Paused;
            }

            return InactiveStatuses.Contains(normalized)
                ? ProviderSubscriberState.Inactive
                : ProviderSubscriberState.Unknown;
        }

        /// <summary>True SOLO con evidencia explícita de que el suscriptor cobra.</summary>
        public static bool IsProviderSubscriberActive(string? status) =>
            Classify(status) == ProviderSubscriberState.Active;

        /// <summary>True SOLO con evidencia explícita de que el suscriptor ya no cobra.</summary>
        public static bool IsProviderSubscriberInactive(string? status) =>
            Classify(status) == ProviderSubscriberState.Inactive;

        /// <summary>True SOLO con evidencia explícita de que el suscriptor está pausado (status 3).</summary>
        public static bool IsProviderSubscriberPaused(string? status) =>
            Classify(status) == ProviderSubscriberState.Paused;

        /// <summary>
        /// True si NO podemos descartar que siga cobrando (activo o desconocido). Es la pregunta
        /// que importa para verificar una baja: sin prueba de que no cobra, la baja no está hecha.
        /// </summary>
        public static bool MayStillCharge(string? status) =>
            Classify(status) != ProviderSubscriberState.Inactive;

        /// <summary>
        /// Status seguro para auditoría/log: solo se emite si es un valor conocido del proveedor.
        /// Uno desconocido podría traer texto arbitrario del API, así que se reporta clasificado
        /// y recortado en vez de crudo.
        /// </summary>
        public static string Sanitize(string? status)
        {
            var normalized = status?.Trim();

            if (string.IsNullOrEmpty(normalized))
            {
                return "(sin status)";
            }

            return Classify(normalized) == ProviderSubscriberState.Unknown
                ? $"(desconocido:{(normalized.Length <= 20 ? normalized : normalized[..20])})"
                : normalized;
        }
    }
}
