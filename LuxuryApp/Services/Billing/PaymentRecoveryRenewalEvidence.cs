using LuxuryApp.Services.Tilopay;

namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Regla ÚNICA (función pura) para decidir si el PROVEEDOR demuestra que una recuperación de pago
    /// terminó, es decir, que empezó un NUEVO período pagado después del cobro rechazado.
    ///
    /// "Active" en TiloPay NO es evidencia de pago: después de un rechazo el suscriptor sigue Active
    /// mientras TiloPay reintenta, con el MISMO expire del período que no se pudo renovar (p. ej.
    /// fallo un día X y minutos después Active + expire = ese mismo día X). Lo único que prueba la renovación
    /// es que el expire AVANZÓ claramente más allá del momento del fallo.
    ///
    /// Ancla = momento del PRIMER fallo del ciclo (<c>SubscriptionPaymentIncident.FailureDetectedAtUtc</c>):
    /// TiloPay intenta la renovación al terminar el período pagado, así que el fallo marca el fin de
    /// ese período. No se usa <c>Suscripcion.FechaFin</c> porque la sincronización de expire puede
    /// moverla ANTES de que corra la sanación y el avance quedaría invisible.
    ///
    /// Margen <see cref="MinimumPeriodAdvance"/>: el expire viene como fecha sin hora y se convierte a
    /// fin del día Costa Rica (≈ +30 h respecto del fallo en el peor caso). Un ciclo real avanza al
    /// menos ~28 días. 3 días separa con holgura el ruido de zona horaria de una renovación real.
    ///
    /// La otra evidencia válida (un <c>PagoSuscripcion</c> Confirmado posterior al fallo) se consulta
    /// en BD por quien llama; esta clase no toca datos.
    /// </summary>
    public static class PaymentRecoveryRenewalEvidence
    {
        /// <summary>Avance mínimo del expire del proveedor sobre el ancla del fallo para considerarlo un período nuevo.</summary>
        public static readonly TimeSpan MinimumPeriodAdvance = TimeSpan.FromDays(3);

        /// <summary>
        /// True solo si el suscriptor está Active Y su expire (ya normalizado a UTC, fin del día Costa
        /// Rica) supera el ancla del fallo por más de <see cref="MinimumPeriodAdvance"/>. Status
        /// desconocido, pausado o inactivo ⇒ false (fail-closed: nunca se inventa una recuperación).
        /// </summary>
        public static bool ProviderShowsNewPaidPeriod(
            string? providerStatus,
            DateTime? providerExpiresAtUtc,
            DateTime failureAnchorUtc)
        {
            if (!ProviderSubscriberStatusRules.IsProviderSubscriberActive(providerStatus) ||
                providerExpiresAtUtc is not { } expiresAtUtc)
            {
                return false;
            }

            return expiresAtUtc > failureAnchorUtc + MinimumPeriodAdvance;
        }
    }
}
