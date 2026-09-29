namespace LuxuryApp.Models.SaaS
{
    public static class PlanCodes
    {
        public const string Basic = "BASIC";
        public const string Pro = "PRO";
        public const string Business = "BUSINESS";
        public const string WhatsApp400 = "WA400";
        public const string WhatsApp800 = "WA800";
        public const string WhatsApp1200 = "WA1200";
        public const string TestRecurring = "TEST_RECURRING";

        // Plan de prueba controlada en producción (plan recurrente de monto mínimo).
        // Es un plan publico normal (NO validacion) usado para verificar el flujo recurrente real.
        public const string TestProdBasic100 = "TEST_PROD_BASIC_100";

        public static readonly string[] BasePlans =
        [
            Basic,
            Pro,
            Business
        ];

        public static readonly string[] WhatsAppAddons =
        [
            WhatsApp400,
            WhatsApp800,
            WhatsApp1200
        ];

        // ---------------------------------------------------------------------
        // Calculadora dinamica de suscripcion: 1..11 funcionarios x Mensual/Anual.
        // El codigo se deriva siempre de (funcionarios, ciclo) con Build(...), de modo
        // que el backend resuelve una unica opcion y nunca confia en el cliente.
        // Mensual => LC_M_NN, Anual => LC_A_NN (NN con dos digitos, 01..11).
        // ---------------------------------------------------------------------
        public const string CalculatorMonthlyPrefix = "LC_M_";
        public const string CalculatorAnnualPrefix = "LC_A_";

        public const int CalculatorMinWorkers = 1;
        public const int CalculatorMaxWorkers = 11;

        /// <summary>
        /// Construye el codigo interno canonico de la calculadora para una combinacion
        /// exacta de funcionarios y ciclo. Devuelve null si los funcionarios estan fuera
        /// de [CalculatorMinWorkers, CalculatorMaxWorkers].
        /// </summary>
        public static string? BuildCalculatorCode(int workerCount, BillingCycle cycle)
        {
            if (workerCount < CalculatorMinWorkers || workerCount > CalculatorMaxWorkers)
            {
                return null;
            }

            var prefix = cycle == BillingCycle.Annual
                ? CalculatorAnnualPrefix
                : CalculatorMonthlyPrefix;

            return $"{prefix}{workerCount:D2}";
        }

        public static bool IsCalculatorPlanCode(string? code) =>
            !string.IsNullOrWhiteSpace(code) &&
            (code.StartsWith(CalculatorMonthlyPrefix, StringComparison.OrdinalIgnoreCase) ||
             code.StartsWith(CalculatorAnnualPrefix, StringComparison.OrdinalIgnoreCase));

        /// <summary>Todos los codigos de la calculadora en orden (mensuales 01..11, luego anuales).</summary>
        public static IEnumerable<string> EnumerateCalculatorCodes()
        {
            for (var workers = CalculatorMinWorkers; workers <= CalculatorMaxWorkers; workers++)
            {
                yield return BuildCalculatorCode(workers, BillingCycle.Monthly)!;
            }

            for (var workers = CalculatorMinWorkers; workers <= CalculatorMaxWorkers; workers++)
            {
                yield return BuildCalculatorCode(workers, BillingCycle.Annual)!;
            }
        }
    }
}
