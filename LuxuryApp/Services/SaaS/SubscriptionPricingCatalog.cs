using LuxuryApp.Models.SaaS;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Services.SaaS
{
    /// <summary>
    /// Opcion de precio resuelta para una combinacion exacta de funcionarios + ciclo.
    /// Es la unidad que el backend resuelve server-side; el cliente nunca define el monto.
    /// </summary>
    public sealed record PricingOption
    {
        public required string Code { get; init; }
        public required int WorkerCount { get; init; }
        public required BillingCycle BillingCycle { get; init; }

        /// <summary>Monto que TiloPay cobra hoy (mensual = mensual; anual = total anual adelantado).</summary>
        public required decimal ChargeAmount { get; init; }

        /// <summary>Equivalente mensual para mostrar ("equivale a X/mes"). Solo display.</summary>
        public required decimal MonthlyEquivalentAmount { get; init; }

        public required string Currency { get; init; }
        public required int TilopayRecurringPlanId { get; init; }
        public required string CheckoutUrl { get; init; }
        public int? MaxFuncionarios { get; init; }
        public bool IsActive { get; init; } = true;
        public bool IsPublic { get; init; } = true;
        public int SortOrder { get; init; }
    }

    /// <summary>
    /// Resultado de resolver una combinacion. O bien hay una opcion comprable (IsAvailable),
    /// o hay un error de configuracion visible (Error) y la combinacion NO se puede comprar.
    /// Nunca se inventan montos faltantes.
    /// </summary>
    public sealed record PricingResolution
    {
        public string? RequestedCode { get; init; }
        public int WorkerCount { get; init; }
        public BillingCycle BillingCycle { get; init; }
        public PricingOption? Option { get; init; }
        public string? Error { get; init; }

        public bool IsAvailable => Option is not null && string.IsNullOrEmpty(Error);

        public static PricingResolution Ok(PricingOption option) => new()
        {
            RequestedCode = option.Code,
            WorkerCount = option.WorkerCount,
            BillingCycle = option.BillingCycle,
            Option = option
        };

        public static PricingResolution Fail(
            string? requestedCode,
            int workerCount,
            BillingCycle cycle,
            string error) => new()
        {
            RequestedCode = requestedCode,
            WorkerCount = workerCount,
            BillingCycle = cycle,
            Error = error
        };
    }

    public interface ISubscriptionPricingCatalog
    {
        /// <summary>Resuelve la opcion exacta para (funcionarios, ciclo). Server-side, autoritativo.</summary>
        PricingResolution Resolve(int workerCount, BillingCycle cycle);

        /// <summary>Resuelve por codigo interno (LC_M_NN / LC_A_NN).</summary>
        PricingResolution ResolveByCode(string? code);

        /// <summary>Las 22 combinaciones (1..11 x Mensual/Anual) con su estado de disponibilidad.</summary>
        IReadOnlyList<PricingResolution> EnumerateAll();

        /// <summary>Solo opciones publicas y comprables, para render de la calculadora.</summary>
        IReadOnlyList<PricingOption> ListPublicAvailable();
    }

    public sealed class SubscriptionPricingCatalog : ISubscriptionPricingCatalog
    {
        private readonly TilopayRepeatOptions _repeatOptions;

        public SubscriptionPricingCatalog(IOptions<TilopayRepeatOptions> repeatOptions)
        {
            _repeatOptions = repeatOptions.Value;
        }

        public PricingResolution Resolve(int workerCount, BillingCycle cycle)
        {
            if (workerCount < PlanCodes.CalculatorMinWorkers || workerCount > PlanCodes.CalculatorMaxWorkers)
            {
                return PricingResolution.Fail(
                    null,
                    workerCount,
                    cycle,
                    $"Cantidad de funcionarios fuera de rango ({PlanCodes.CalculatorMinWorkers}-{PlanCodes.CalculatorMaxWorkers}).");
            }

            var code = PlanCodes.BuildCalculatorCode(workerCount, cycle);
            if (string.IsNullOrEmpty(code))
            {
                return PricingResolution.Fail(null, workerCount, cycle, "No fue posible derivar el codigo del plan.");
            }

            var option = _repeatOptions.Calculator
                .FirstOrDefault(plan => string.Equals(plan.Code, code, StringComparison.OrdinalIgnoreCase));

            if (option is null)
            {
                return PricingResolution.Fail(
                    code,
                    workerCount,
                    cycle,
                    $"Plan {code} no configurado en TilopayRepeat:Calculator.");
            }

            return BuildResolution(code, workerCount, cycle, option);
        }

        public PricingResolution ResolveByCode(string? code)
        {
            if (!PlanCodes.IsCalculatorPlanCode(code))
            {
                return PricingResolution.Fail(code, 0, BillingCycle.Monthly, "El codigo no pertenece a la calculadora.");
            }

            var cycle = code!.StartsWith(PlanCodes.CalculatorAnnualPrefix, StringComparison.OrdinalIgnoreCase)
                ? BillingCycle.Annual
                : BillingCycle.Monthly;

            var prefixLength = PlanCodes.CalculatorAnnualPrefix.Length; // mismo largo para ambos prefijos
            var workerSuffix = code.Length > prefixLength ? code[prefixLength..] : string.Empty;
            if (!int.TryParse(workerSuffix, out var workerCount))
            {
                return PricingResolution.Fail(code, 0, cycle, $"Codigo de calculadora invalido: {code}.");
            }

            return Resolve(workerCount, cycle);
        }

        public IReadOnlyList<PricingResolution> EnumerateAll()
        {
            var resolutions = new List<PricingResolution>();
            foreach (var cycle in new[] { BillingCycle.Monthly, BillingCycle.Annual })
            {
                for (var workers = PlanCodes.CalculatorMinWorkers; workers <= PlanCodes.CalculatorMaxWorkers; workers++)
                {
                    resolutions.Add(Resolve(workers, cycle));
                }
            }

            return resolutions;
        }

        public IReadOnlyList<PricingOption> ListPublicAvailable() =>
            EnumerateAll()
                .Where(resolution => resolution.IsAvailable && resolution.Option!.IsPublic)
                .Select(resolution => resolution.Option!)
                .OrderBy(option => option.BillingCycle)
                .ThenBy(option => option.WorkerCount)
                .ToList();

        private static PricingResolution BuildResolution(
            string code,
            int workerCount,
            BillingCycle cycle,
            TilopayRepeatPlanOption option)
        {
            // Validacion "configurada o error visible": sin estos datos no se puede cobrar.
            if (option.TilopayPlanId <= 0)
            {
                return PricingResolution.Fail(code, workerCount, cycle, $"Plan {code} sin TilopayPlanId valido.");
            }

            if (option.MonthlyPrice <= 0)
            {
                return PricingResolution.Fail(code, workerCount, cycle, $"Plan {code} sin monto configurado.");
            }

            if (string.IsNullOrWhiteSpace(option.CheckoutUrl))
            {
                return PricingResolution.Fail(code, workerCount, cycle, $"Plan {code} sin CheckoutUrl configurado.");
            }

            var currency = string.IsNullOrWhiteSpace(option.Currency) ? "CRC" : option.Currency.ToUpperInvariant();

            // Equivalente mensual: preferir el configurado; si falta, derivar (anual => /12).
            var monthlyEquivalent = option.MonthlyEquivalentAmount is > 0
                ? option.MonthlyEquivalentAmount.Value
                : cycle == BillingCycle.Annual
                    ? decimal.Round(option.MonthlyPrice / 12m, 2)
                    : option.MonthlyPrice;

            return PricingResolution.Ok(new PricingOption
            {
                Code = option.Code,
                WorkerCount = workerCount,
                BillingCycle = cycle,
                ChargeAmount = option.MonthlyPrice,
                MonthlyEquivalentAmount = monthlyEquivalent,
                Currency = currency,
                TilopayRecurringPlanId = option.TilopayPlanId,
                CheckoutUrl = option.CheckoutUrl,
                MaxFuncionarios = option.MaxFuncionarios ?? workerCount,
                IsActive = true,
                IsPublic = option.IsPublic,
                SortOrder = (cycle == BillingCycle.Annual ? 100 : 0) + workerCount
            });
        }
    }
}
