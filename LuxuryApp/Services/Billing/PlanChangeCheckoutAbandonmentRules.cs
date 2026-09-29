using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Distingue un checkout de cambio de plan ABANDONADO (el cliente abrió el link y nunca pagó)
    /// de uno con DINERO detrás. Es la única pregunta que separa "limpiar ruido" de "borrar la
    /// pista de un cobro real", así que vive en un solo lugar: la usan la expiración automática,
    /// el supersede al abrir un checkout nuevo y el health check.
    ///
    /// Sesgo deliberado hacia NO expirar: dejar un pendiente de más solo ensucia un contador y un
    /// humano lo ve; expirar uno que sí tenía dinero nos deja sin rastro de un cobro real. Ante
    /// cualquier señal —o ante la duda— el caso va a reparación/revisión, nunca a la basura.
    /// </summary>
    public static class PlanChangeCheckoutAbandonmentRules
    {
        /// <summary>
        /// Señales de que el proveedor YA hizo algo con este intento. Cualquiera de ellas veta la
        /// expiración automática. Un pago inexistente no es señal: sin pago no hubo checkout.
        /// </summary>
        public static bool HasMoneySignals(PagoSuscripcion? payment, bool hasProviderEvent = false)
        {
            if (payment is null)
            {
                return false;
            }

            // Estados no terminales donde puede haber dinero cobrado. Fallido/Cancelado/Expirado
            // son cierres SIN dinero vivo, así que no vetan.
            if (payment.Estado is EstadoPagoProveedor.Confirmado or EstadoPagoProveedor.ManualReview)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(payment.ProviderTransactionId) ||
                   !string.IsNullOrWhiteSpace(payment.ProviderSubscriberId) ||
                   payment.FechaConfirmacionUtc.HasValue ||
                   // Un webhook asociado significa que TiloPay tocó este intento: aunque no haya
                   // dejado tx ni suscriptor, no se descarta a ciegas.
                   hasProviderEvent;
        }

        /// <summary>
        /// True si el intento se puede expirar: pago Pendiente, sin ninguna señal de dinero y con
        /// más antigüedad que la ventana. Sin pago asociado también cuenta (checkout que nunca abrió).
        /// </summary>
        public static bool IsAbandonedCheckout(
            PagoSuscripcion? payment,
            DateTime intentCreatedAtUtc,
            DateTime nowUtc,
            int expirationHours,
            bool hasProviderEvent = false)
        {
            if (HasMoneySignals(payment, hasProviderEvent))
            {
                return false;
            }

            if (payment is not null && payment.Estado != EstadoPagoProveedor.Pendiente)
            {
                // Ya cerrado por otra vía (fallido/cancelado/expirado): el intent se limpia igual,
                // pero eso lo decide el llamador; aquí solo importa la ventana.
                return HasExpired(payment.FechaCreacionUtc, intentCreatedAtUtc, nowUtc, expirationHours);
            }

            var referenceUtc = payment?.FechaCreacionUtc ?? intentCreatedAtUtc;
            return HasExpired(referenceUtc, intentCreatedAtUtc, nowUtc, expirationHours);
        }

        /// <summary>
        /// La ventana se mide desde el evento MÁS RECIENTE (creación del pago o del intent): si el
        /// cliente reabrió el checkout hace un minuto, no es un abandono aunque el intent sea viejo.
        /// </summary>
        private static bool HasExpired(
            DateTime paymentCreatedAtUtc,
            DateTime intentCreatedAtUtc,
            DateTime nowUtc,
            int expirationHours)
        {
            var lastActivityUtc = paymentCreatedAtUtc > intentCreatedAtUtc ? paymentCreatedAtUtc : intentCreatedAtUtc;
            return lastActivityUtc <= nowUtc.AddHours(-Math.Max(1, expirationHours));
        }
    }
}
