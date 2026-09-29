using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Payments
{
    public class PaymentProviderWebhookData
    {
        public PaymentProviderType ProviderType { get; set; }
        public string EventId { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
        public string? ProviderOrderNumber { get; set; }
        public string StatusCode { get; set; } = string.Empty;
        public string StatusDescription { get; set; } = string.Empty;
        public string? ProviderCheckoutId { get; set; }
        public string? ProviderTransactionId { get; set; }
        public int? RecurringPlanId { get; set; }
        public string? PlanCode { get; set; }
        public string? ProviderSubscriberId { get; set; }
        public string? CustomerEmail { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }

        // ── Captura real del dinero (ver RecurringPaymentSettlementRules) ──────────────────
        // "Aprobada" NO implica "capturada": TiloPay puede autorizar un monto de verificacion y
        // reversarlo. Estos campos son NULL cuando el proveedor no manda la señal; en ese caso el
        // veredicto es Settled y la defensa queda en el monto exacto + el sondeo del proveedor.

        /// <summary>True/false SOLO si el proveedor lo dice explicitamente. Null = sin señal.</summary>
        public bool? IsCaptured { get; set; }

        /// <summary>Total realmente debitado, si el proveedor lo envia. 0 con Amount &gt; 0 = sin cobro.</summary>
        public decimal? CapturedAmount { get; set; }

        /// <summary>Estado de captura crudo del proveedor (para clasificar y auditar).</summary>
        public string? CaptureStatusRaw { get; set; }
        public bool IsRecurring { get; set; }
        public string? RecurringModality { get; set; }
        public string? RecurringFrequency { get; set; }
        public string? CouponCode { get; set; }
        public bool? HasFreeTrial { get; set; }
        public DateTime? NextBillingDateUtc { get; set; }
        public DateTime? ExpirationDateUtc { get; set; }
        public string? AuthorizationCode { get; set; }
        public string? CardBrand { get; set; }
        public string? CardLast4 { get; set; }
        public string? OrderHash { get; set; }
        public string RawPayload { get; set; } = string.Empty;
    }
}
