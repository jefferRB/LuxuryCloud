namespace LuxuryApp.Models.SaaS
{
    public sealed class TilopayRepeatOptions
    {
        public bool Enabled { get; set; }
        public bool UseHostedLinks { get; set; } = true;
        public bool UseRecurringCheckoutForPublicPlans { get; set; }
        public bool EnableTestRecurringPlan { get; set; }
        public int GracePeriodDays { get; set; } = 3;
        public TilopayRepeatPlanOption Basic { get; set; } = new();
        public TilopayRepeatPlanOption Pro { get; set; } = new();
        public TilopayRepeatPlanOption Business { get; set; } = new();
        public TilopayRepeatPlanOption WhatsApp400 { get; set; } = new();
        public TilopayRepeatPlanOption WhatsApp800 { get; set; } = new();
        public TilopayRepeatPlanOption WhatsApp1200 { get; set; } = new();
        public TilopayRepeatPlanOption TestRecurring { get; set; } = new();
        public TilopayRepeatPlanOption TestProdBasic100 { get; set; } = new();

        /// <summary>
        /// Opciones de la calculadora dinamica (1..11 funcionarios x Mensual/Anual).
        /// Coleccion abierta para no tener 22 propiedades fijas. Cada entrada se enlaza por
        /// Code (LC_M_NN / LC_A_NN) a una fila de Planes con el mismo Codigo.
        /// </summary>
        public List<TilopayRepeatPlanOption> Calculator { get; set; } = new();

        public IReadOnlyCollection<TilopayRepeatPlanRegistration> GetAllPlans()
        {
            var registrations = new List<TilopayRepeatPlanRegistration>
            {
                new("Basic", Basic),
                new("Pro", Pro),
                new("Business", Business),
                new("WhatsApp400", WhatsApp400),
                new("WhatsApp800", WhatsApp800),
                new("WhatsApp1200", WhatsApp1200),
                new("TestRecurring", TestRecurring),
                new("TestProdBasic100", TestProdBasic100)
            };

            foreach (var option in Calculator)
            {
                registrations.Add(new TilopayRepeatPlanRegistration(
                    $"Calculator:{option.Code}",
                    option));
            }

            return registrations;
        }

        public TilopayRepeatPlanOption? FindByCode(string? code) =>
            FindRegistrationByCode(code)?.Plan;

        public TilopayRepeatPlanOption? FindByRecurringPlanId(int? recurringPlanId) =>
            FindRegistrationByRecurringPlanId(recurringPlanId)?.Plan;

        public TilopayRepeatPlanRegistration? FindRegistrationByCode(string? code) =>
            GetAllPlans().FirstOrDefault(plan =>
                !string.IsNullOrWhiteSpace(plan.Plan.Code) &&
                string.Equals(plan.Plan.Code, code, StringComparison.OrdinalIgnoreCase));

        public TilopayRepeatPlanRegistration? FindRegistrationByRecurringPlanId(int? recurringPlanId) =>
            recurringPlanId.HasValue
                ? GetAllPlans().FirstOrDefault(plan => plan.Plan.TilopayPlanId == recurringPlanId.Value)
                : null;

        public static bool IsManagedPlanCode(string? code) =>
            code is PlanCodes.Basic or
                PlanCodes.Pro or
                PlanCodes.Business or
                PlanCodes.WhatsApp400 or
                PlanCodes.WhatsApp800 or
                PlanCodes.WhatsApp1200 or
                PlanCodes.TestRecurring or
                PlanCodes.TestProdBasic100 ||
            PlanCodes.IsCalculatorPlanCode(code);

        public static string? ResolveSectionKey(string? code) =>
            code switch
            {
                PlanCodes.Basic => "Basic",
                PlanCodes.Pro => "Pro",
                PlanCodes.Business => "Business",
                PlanCodes.WhatsApp400 => "WhatsApp400",
                PlanCodes.WhatsApp800 => "WhatsApp800",
                PlanCodes.WhatsApp1200 => "WhatsApp1200",
                PlanCodes.TestRecurring => "TestRecurring",
                PlanCodes.TestProdBasic100 => "TestProdBasic100",
                _ => PlanCodes.IsCalculatorPlanCode(code) ? $"Calculator:{code}" : null
            };
    }

    public sealed record TilopayRepeatPlanRegistration(
        string SectionKey,
        TilopayRepeatPlanOption Plan);

    public sealed class TilopayRepeatPlanOption
    {
        public int TilopayPlanId { get; set; }
        public string Code { get; set; } = string.Empty;

        // Monto que TiloPay cobra POR CICLO. Para planes mensuales es el precio mensual;
        // para planes anuales es el total anual adelantado. La validacion de monto exacto
        // del webhook compara contra ExpectedFirstChargeAmount, por lo que esto debe ser
        // siempre el cobro real del ciclo (no el equivalente mensual).
        public decimal MonthlyPrice { get; set; }
        public decimal ExpectedFirstChargeAmount => MonthlyPrice;

        // Ciclo de facturacion de la opcion. Default Monthly preserva los planes existentes.
        public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;

        // Equivalente mensual para mostrar en la UI ("equivale a X/mes"). Solo display.
        // Si no se configura, se deriva (anual => MonthlyPrice/12; mensual => MonthlyPrice).
        public decimal? MonthlyEquivalentAmount { get; set; }

        public string Currency { get; set; } = "CRC";
        public int? MaxFuncionarios { get; set; }
        public int? MonthlyMessageLimit { get; set; }
        public int? DailyMessageLimit { get; set; }
        public string CheckoutUrl { get; set; } = string.Empty;
        public bool IsAddon { get; set; }
        public bool IsValidation { get; set; }
        public bool IsPublic { get; set; } = true;

        // Override por plan: habilita checkout recurrente para este plan publico
        // sin tener que encender el flag global UseRecurringCheckoutForPublicPlans
        // (que afectaria a BASIC/PRO/BUSINESS). Default false preserva comportamiento.
        public bool UsesRecurringCheckout { get; set; }
    }
}
