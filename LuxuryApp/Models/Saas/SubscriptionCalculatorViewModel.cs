namespace LuxuryApp.Models.SaaS
{
    /// <summary>
    /// Modelo de la calculadora dinamica de suscripcion. Lleva la matriz de opciones
    /// (1..11 x Mensual/Anual) ya resueltas server-side para que el cliente solo elija
    /// cantidad + ciclo y vea precios sin recargar. El backend nunca confia en montos del cliente.
    /// </summary>
    public sealed class SubscriptionCalculatorViewModel
    {
        public IReadOnlyList<SubscriptionCalculatorOption> Options { get; init; } = Array.Empty<SubscriptionCalculatorOption>();
        public string Currency { get; init; } = "CRC";

        public int MinWorkers { get; init; } = 1;
        public int MaxWorkers { get; init; } = 11;
        public int DefaultWorkers { get; init; } = 1;
        public BillingCycle DefaultCycle { get; init; } = BillingCycle.Monthly;

        public int ActiveFuncionarios { get; init; }
        public bool HasActiveSubscription { get; init; }
        public int? CurrentWorkers { get; init; }
        public BillingCycle? CurrentCycle { get; init; }
        public string? CurrentPlanCode { get; init; }

        /// <summary>
        /// Próxima renovación EFECTIVA del plan actual (dd/MM/yyyy, fecha Tica). La calculadora la
        /// muestra cuando el plan elegido coincide con el actual, en vez de calcular "hoy + 1 ciclo":
        /// para el plan vigente hay que mostrar la fecha real, no un estimado.
        /// </summary>
        public string? CurrentPlanRenewalDisplay { get; init; }

        /// <summary>Hay al menos una opcion comprable configurada.</summary>
        public bool IsEnabled => Options.Any(option => option.IsAvailable);

        /// <summary>True si todas las combinaciones de un ciclo estan disponibles (UI puede ofrecer el toggle).</summary>
        public bool AnnualAvailable => Options.Any(option => option.IsAvailable && option.Cycle == "Annual");
        public bool MonthlyAvailable => Options.Any(option => option.IsAvailable && option.Cycle == "Monthly");
    }

    public sealed class SubscriptionCalculatorOption
    {
        public required string Code { get; init; }
        public required int Workers { get; init; }
        public required string Cycle { get; init; } // "Monthly" | "Annual"
        public decimal ChargeAmount { get; init; }
        public decimal MonthlyEquivalentAmount { get; init; }

        /// <summary>Ahorro anual frente a pagar 12 meses al precio mensual (0 en planes mensuales).</summary>
        public decimal AnnualSavings { get; init; }
        public int SavingsPercent { get; init; }

        public bool IsAvailable { get; init; }
        public string? UnavailableReason { get; init; }
    }
}
