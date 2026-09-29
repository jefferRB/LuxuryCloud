namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Fecha EFECTIVA de fin de período / próxima renovación de una suscripción, conciliando lo
    /// que LuxuryCloud calculó localmente con lo que TiloPay realmente va a cobrar.
    ///
    /// Principio: LuxuryCloud manda sobre plan, cupo y acceso; TiloPay es la fuente de verdad de
    /// CUÁNDO cobra de nuevo. Si el proveedor tiene una fecha POSTERIOR (p.ej. reactivó un
    /// suscriptor y extendió el expire), no hay que marcar moroso antes de esa fecha.
    ///
    /// La regla es un simple MÁXIMO, y esa forma implementa las dos mitades del requisito de una
    /// sola vez: si el proveedor va por delante, extiende; si el proveedor va por DETRÁS, el
    /// máximo se queda con la fecha local, así que NUNCA acorta el acceso. Acortar por una fecha
    /// del proveedor podría quitar servicio ya pagado, y eso jamás se hace automáticamente.
    /// </summary>
    public static class SubscriptionEffectiveDates
    {
        /// <summary>
        /// Fin de período efectivo = el más TARDÍO entre el local y el del proveedor. El del
        /// proveedor solo cuenta si está presente (solo se persiste cuando el suscriptor está
        /// Active), así que basta su presencia — nunca se guarda para un suscriptor inactivo.
        /// </summary>
        public static DateTime? GetEffectiveEndUtc(DateTime? localEndUtc, DateTime? providerExpiresAtUtc)
        {
            if (providerExpiresAtUtc is null)
            {
                return localEndUtc;
            }

            if (localEndUtc is null)
            {
                return providerExpiresAtUtc;
            }

            return providerExpiresAtUtc.Value > localEndUtc.Value
                ? providerExpiresAtUtc.Value
                : localEndUtc.Value;
        }

        /// <summary>True si el proveedor va por DELANTE del local más allá de una tolerancia (extiende el acceso).</summary>
        public static bool ProviderIsAhead(DateTime? localEndUtc, DateTime? providerExpiresAtUtc, TimeSpan tolerance)
        {
            if (providerExpiresAtUtc is null || localEndUtc is null)
            {
                return false;
            }

            return providerExpiresAtUtc.Value - localEndUtc.Value > tolerance;
        }

        /// <summary>True si el proveedor va por DETRÁS del local más allá de una tolerancia (posible corte injusto: solo alerta).</summary>
        public static bool ProviderIsEarlier(DateTime? localEndUtc, DateTime? providerExpiresAtUtc, TimeSpan tolerance)
        {
            if (providerExpiresAtUtc is null || localEndUtc is null)
            {
                return false;
            }

            return localEndUtc.Value - providerExpiresAtUtc.Value > tolerance;
        }
    }
}
