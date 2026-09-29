namespace LuxuryApp.Models.Marketing
{
    /// <summary>
    /// Vista comercial COMPACTA para la landing, derivada del MISMO catálogo del calculador
    /// de <c>/Billing/Planes</c> (ISubscriptionPricingCatalog → planes LC_M_/LC_A_).
    /// Es un DTO inmutable, sin entidades EF, seguro de cachear. Nunca contiene planes
    /// legacy (BASIC/PRO/BUSINESS), TEST, internos ni add-ons de WhatsApp.
    /// </summary>
    public sealed record CommercialPricingPreview
    {
        /// <summary>False cuando el catálogo comercial no pudo resolverse: la landing muestra
        /// el mensaje neutro "Los precios no están disponibles temporalmente." sin inventar montos.</summary>
        public bool IsAvailable { get; init; }

        public string Currency { get; init; } = "CRC";
        public int MinWorkers { get; init; }
        public int MaxWorkers { get; init; }

        /// <summary>Cobro mensual del plan de entrada (1 integrante = LC_M_01). Es el "Desde ₡X".
        /// NO es un Min() sobre todos los planes: es explícitamente el plan mensual de un integrante.</summary>
        public decimal StartingMonthlyCharge { get; init; }

        public bool HasMonthly { get; init; }
        public bool HasAnnual { get; init; }

        /// <summary>Precio "desde" del complemento de WhatsApp (del catálogo de add-ons, separado
        /// de los planes base). Null si no hay add-ons disponibles ⇒ no se muestra precio.</summary>
        public decimal? WhatsAppFromCharge { get; init; }

        public IReadOnlyList<CommercialPricingTier> Tiers { get; init; } = Array.Empty<CommercialPricingTier>();

        public static CommercialPricingPreview Unavailable() => new() { IsAvailable = false };
    }

    /// <summary>
    /// Un punto del calculador: (integrantes, ciclo) con su cobro y equivalente mensual, tal como
    /// los provee el catálogo real. La landing no recalcula montos ni descuentos: solo los muestra.
    /// </summary>
    public sealed record CommercialPricingTier
    {
        public int Workers { get; init; }
        public string Cycle { get; init; } = "Monthly"; // "Monthly" | "Annual"
        public decimal ChargeAmount { get; init; }
        public decimal MonthlyEquivalentAmount { get; init; }
    }
}
