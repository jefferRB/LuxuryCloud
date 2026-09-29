using LuxuryApp.Models.SaaS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Estado (SEGURO, enmascarado) del hosted link de un plan/add-on gestionado. NUNCA lleva la URL
    /// completa ni el token: solo si está presente (<see cref="HasCheckoutUrl"/>) y un descriptor
    /// enmascarado (host + longitud). Los links de TiloPay llevan el token en el path, así que jamás
    /// se imprime el path.
    /// </summary>
    public sealed record ManagedPlanCheckoutStatus(
        string SectionKey,
        string Code,
        int TilopayPlanId,
        bool IsAddon,
        bool HasCheckoutUrl,
        string CheckoutUrlDescriptor);

    /// <summary>
    /// Inspecciona la configuración EFECTIVA de checkout (appsettings + appsettings.{Env} + variables
    /// de entorno, ya resuelta por <see cref="IOptions{T}"/>): reporta <c>HasCheckoutUrl</c> por plan.
    /// Pensado para no concluir "vacío" mirando solo un appsettings cuando una env var
    /// (p.ej. desde /etc/luxury/luxury.env) lo sobreescribe en producción.
    /// </summary>
    public interface IManagedPlanCheckoutInspector
    {
        IReadOnlyList<ManagedPlanCheckoutStatus> Inspect();
        IReadOnlyList<ManagedPlanCheckoutStatus> InspectAddons();
    }

    public sealed class ManagedPlanCheckoutInspector : IManagedPlanCheckoutInspector
    {
        private readonly TilopayRepeatOptions _options;

        public ManagedPlanCheckoutInspector(IOptions<TilopayRepeatOptions> options) => _options = options.Value;

        public IReadOnlyList<ManagedPlanCheckoutStatus> Inspect() =>
            _options.GetAllPlans()
                .Where(registration => !registration.Plan.IsValidation)
                .Select(registration => new ManagedPlanCheckoutStatus(
                    registration.SectionKey,
                    registration.Plan.Code,
                    registration.Plan.TilopayPlanId,
                    registration.Plan.IsAddon,
                    !string.IsNullOrWhiteSpace(registration.Plan.CheckoutUrl),
                    DescribeMasked(registration.Plan.CheckoutUrl)))
                .ToList();

        public IReadOnlyList<ManagedPlanCheckoutStatus> InspectAddons() =>
            Inspect().Where(status => status.IsAddon).ToList();

        /// <summary>
        /// Descriptor SEGURO: host + longitud. Nunca el path (que lleva el token del hosted link).
        /// </summary>
        private static string DescribeMasked(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "(vacío)";
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return $"{uri.Host} ({url.Length} chars)";
            }

            return $"(no-uri) ({url.Length} chars)";
        }
    }

    /// <summary>
    /// Log de arranque: deja constancia de si cada add-on de WhatsApp tiene CheckoutUrl EFECTIVO
    /// (tras aplicar env vars). Sin la URL completa. Warning si falta: no se puede vender ese add-on
    /// por checkout recurrente hasta cargar el hosted link real.
    /// </summary>
    public sealed class ManagedPlanCheckoutStartupLogger : IHostedService
    {
        private readonly IManagedPlanCheckoutInspector _inspector;
        private readonly ILogger<ManagedPlanCheckoutStartupLogger> _logger;

        public ManagedPlanCheckoutStartupLogger(
            IManagedPlanCheckoutInspector inspector,
            ILogger<ManagedPlanCheckoutStartupLogger> logger)
        {
            _inspector = inspector;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var addon in _inspector.InspectAddons())
            {
                if (addon.HasCheckoutUrl)
                {
                    _logger.LogInformation(
                        "Config checkout add-on WhatsApp. Code {Code}. TilopayPlanId {TilopayPlanId}. HasCheckoutUrl {HasCheckoutUrl}. Link {Descriptor}.",
                        addon.Code, addon.TilopayPlanId, true, addon.CheckoutUrlDescriptor);
                }
                else
                {
                    _logger.LogWarning(
                        "Add-on WhatsApp SIN CheckoutUrl efectivo: no se puede vender por checkout recurrente hasta cargar el hosted link real. Code {Code}. TilopayPlanId {TilopayPlanId}. HasCheckoutUrl {HasCheckoutUrl}.",
                        addon.Code, addon.TilopayPlanId, false);
                }
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
