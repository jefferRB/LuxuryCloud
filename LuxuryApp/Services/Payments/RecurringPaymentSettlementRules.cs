using System.Globalization;

namespace LuxuryApp.Services.Payments
{
    /// <summary>Qué significa el webhook para el DINERO: ¿se cobró de verdad o no?</summary>
    public enum RecurringSettlementVerdict
    {
        /// <summary>No hay evidencia en contra: se trata como cobro real (comportamiento histórico).</summary>
        Settled = 0,

        /// <summary>Autorizada/pre-autorizada SIN captura, o total debitado 0. NO es cobro real.</summary>
        NotCaptured = 1,

        /// <summary>Anulada / reverso / devolución. NO es cobro real.</summary>
        VoidedOrReversed = 2
    }

    public sealed record RecurringSettlementAssessment(RecurringSettlementVerdict Verdict, string? Reason)
    {
        public bool IsSettled => Verdict == RecurringSettlementVerdict.Settled;

        public static RecurringSettlementAssessment Settled { get; } =
            new(RecurringSettlementVerdict.Settled, null);
    }

    /// <summary>
    /// Única fuente de verdad para decidir si un webhook recurrente representa DINERO REALMENTE
    /// COBRADO. Regresión que cubre (downgrade de add-on WA800→WA400): el proveedor puede
    /// generar una transacción de monto bajo "Aprobada no capturada" (orden PFC…-PRE…) y acto
    /// seguido un reverso "Re-PFC…" con total debitado 0, y aun así enviar el webhook como
    /// <c>repeat_payment_success</c> con <c>code=1</c> y <c>response="Transaccion aprobada"</c>.
    /// Un "aprobada" del proveedor NO implica captura.
    ///
    /// Regla de oro (espeja <see cref="Tilopay.ProviderSubscriberStatusRules"/>): solo se rechaza
    /// con EVIDENCIA EXPLÍCITA en contra. Sin señales de captura en el payload el veredicto es
    /// <see cref="RecurringSettlementVerdict.Settled"/>, para no romper los flujos que hoy
    /// funcionan (WA400→WA800 se aprobó sin estos campos). La defensa real contra el caso sin
    /// señales es la validación de monto exacto + el sondeo del proveedor tras el rechazo.
    /// </summary>
    public static class RecurringPaymentSettlementRules
    {
        /// <summary>Valores de captura que confirman que NO se capturó el dinero.</summary>
        private static readonly HashSet<string> NotCapturedStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "0",
            "false",
            "no",
            "n",
            "pending",
            "pendiente",
            "uncaptured",
            "not_captured",
            "notcaptured",
            "no_capturada",
            "nocapturada",
            "authorized",
            "autorizada",
            "autorizado",
            "preauth",
            "pre_auth",
            "preauthorized",
            "preautorizada",
            "preautorizado",
            "preautorizacion",
            "hold"
        };

        /// <summary>Fragmentos que delatan una autorización sin captura en textos libres del proveedor.</summary>
        private static readonly string[] NotCapturedTextMarkers =
        {
            "no capturada",
            "no capturado",
            "not captured",
            "uncaptured",
            "sin captura",
            "aprobada no",
            "preautoriz",
            "pre-autoriz",
            "pre autoriz",
            "preauthoriz",
            "pre-authoriz"
        };

        /// <summary>Fragmentos que delatan una anulación/reverso/devolución.</summary>
        private static readonly string[] VoidTextMarkers =
        {
            "anulada",
            "anulado",
            "anulacion",
            "anulación",
            "void",
            "reversa",
            "reverso",
            "reversal",
            "reversed",
            "refund",
            "reembolso",
            "devolucion",
            "devolución",
            "chargeback",
            "contracargo"
        };

        /// <summary>
        /// Marca de reverso de TiloPay: la orden del reverso es la original con el prefijo "Re-"
        /// (p. ej. "Re-PFC000001-PRE123456" anulando "PFC000001-PRE123456").
        /// </summary>
        private const string ReversalOrderPrefix = "Re-";

        public static RecurringSettlementAssessment Evaluate(PaymentProviderWebhookData webhook)
        {
            ArgumentNullException.ThrowIfNull(webhook);

            // ── 1. Anulación / reverso ─────────────────────────────────────────────────────
            if (IsReversalOrderNumber(webhook.ProviderOrderNumber))
            {
                return new RecurringSettlementAssessment(
                    RecurringSettlementVerdict.VoidedOrReversed,
                    $"El numero de orden del proveedor empieza con \"{ReversalOrderPrefix}\": es el reverso de una transaccion previa, no un cobro.");
            }

            if (ContainsAny(webhook.StatusDescription, VoidTextMarkers) ||
                ContainsAny(webhook.EventType, VoidTextMarkers) ||
                ContainsAny(webhook.CaptureStatusRaw, VoidTextMarkers))
            {
                return new RecurringSettlementAssessment(
                    RecurringSettlementVerdict.VoidedOrReversed,
                    "El proveedor reporta la transaccion como anulada/reversada: no se trata como pago confirmado.");
            }

            // ── 2. Autorizada sin captura ──────────────────────────────────────────────────
            if (webhook.IsCaptured == false)
            {
                return new RecurringSettlementAssessment(
                    RecurringSettlementVerdict.NotCaptured,
                    "El proveedor reporta la transaccion como NO capturada: hay autorizacion pero no cobro.");
            }

            if (IsNotCapturedStatus(webhook.CaptureStatusRaw))
            {
                return new RecurringSettlementAssessment(
                    RecurringSettlementVerdict.NotCaptured,
                    $"Estado de captura del proveedor \"{Sanitize(webhook.CaptureStatusRaw)}\": autorizada sin captura.");
            }

            if (ContainsAny(webhook.StatusDescription, NotCapturedTextMarkers))
            {
                return new RecurringSettlementAssessment(
                    RecurringSettlementVerdict.NotCaptured,
                    "La descripcion del proveedor indica una autorizacion sin captura (p. ej. \"Aprobada no capturada\").");
            }

            // Total debitado 0 con monto autorizado > 0 = autorizacion retenida, no cobro.
            if (webhook.CapturedAmount is { } captured &&
                captured <= 0m &&
                webhook.Amount is { } authorized &&
                authorized > 0m)
            {
                return new RecurringSettlementAssessment(
                    RecurringSettlementVerdict.NotCaptured,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "El proveedor autorizo {0:0.00} pero el total debitado es {1:0.00}: no hubo cobro real.",
                        authorized,
                        captured));
            }

            return RecurringSettlementAssessment.Settled;
        }

        /// <summary>
        /// Señal informativa (NO bloqueante): la orden trae la marca de pre-autorizacion de TiloPay.
        /// Sola no rechaza nada —aparece tambien en cobros reales—, pero enriquece el diagnostico
        /// cuando el pago ya se rechazo por monto o por captura.
        /// </summary>
        public static bool LooksLikePreAuthorizationOrder(string? providerOrderNumber) =>
            !string.IsNullOrWhiteSpace(providerOrderNumber) &&
            providerOrderNumber.Contains("-PRE", StringComparison.OrdinalIgnoreCase);

        public static bool IsReversalOrderNumber(string? providerOrderNumber) =>
            !string.IsNullOrWhiteSpace(providerOrderNumber) &&
            providerOrderNumber.TrimStart().StartsWith(ReversalOrderPrefix, StringComparison.OrdinalIgnoreCase);

        private static bool IsNotCapturedStatus(string? captureStatus)
        {
            var normalized = captureStatus?.Trim();
            return !string.IsNullOrEmpty(normalized) && NotCapturedStatuses.Contains(normalized);
        }

        private static bool ContainsAny(string? value, string[] markers)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var marker in markers)
            {
                if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Sanitize(string? value)
        {
            var normalized = value?.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                return "(vacio)";
            }

            return normalized.Length <= 30 ? normalized : normalized[..30];
        }
    }
}
