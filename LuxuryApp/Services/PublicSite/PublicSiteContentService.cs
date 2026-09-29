using LuxuryApp.Models.Marketing;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.Saas;
using LuxuryApp.Services.SaaS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.PublicSite
{
    public sealed class PublicSiteContentService : IPublicSiteContentService
    {
        // Clave estable y claramente nombrada. El sufijo de versión permite invalidar de
        // forma controlada si cambia la forma proyectada del snapshot.
        internal const string AvailablePlansCacheKey = "public-site:available-plans:v1";

        // Catálogo comercial (calculador LC_M_/LC_A_) proyectado para la landing. Clave distinta
        // porque es un propósito distinto al de las cards de planes legacy.
        internal const string CommercialPricingPreviewCacheKey = "public-site:commercial-pricing-preview:v1";

        // Los planes públicos cambian con muy poca frecuencia; 10 minutos absorben los picos
        // de tráfico anónimo sin servir precios desactualizados por mucho tiempo.
        private static readonly TimeSpan AvailablePlansCacheTtl = TimeSpan.FromMinutes(10);

        private readonly ApplicationDbContext _context;
        private readonly IMemoryCache _cache;
        private readonly ISubscriptionPricingCatalog _pricingCatalog;
        private readonly OpcionesPago _paymentOptions;
        private readonly OpcionesTilopay _tilopayOptions;
        private readonly TilopayRepeatOptions _tilopayRepeatOptions;

        public PublicSiteContentService(
            ApplicationDbContext context,
            IMemoryCache cache,
            ISubscriptionPricingCatalog pricingCatalog,
            IOptions<OpcionesPago> paymentOptions,
            IOptions<OpcionesTilopay> tilopayOptions,
            IOptions<TilopayRepeatOptions> tilopayRepeatOptions)
        {
            _context = context;
            _cache = cache;
            _pricingCatalog = pricingCatalog;
            _paymentOptions = paymentOptions.Value;
            _tilopayOptions = tilopayOptions.Value;
            _tilopayRepeatOptions = tilopayRepeatOptions.Value;
        }

        public IReadOnlyCollection<MarketingMetricViewModel> GetHeroMetrics() =>
        [
            new() { Value = "1", Label = "panel para citas, caja, clientes y equipo" },
            new() { Value = "7", Label = "modulos conectados para operar sin hojas sueltas" },
            new() { Value = "24/7", Label = "visibilidad comercial para decidir con mas control" }
        ];

        public IReadOnlyCollection<MarketingModuleViewModel> GetModules() =>
        [
            new()
            {
                Id = "ganancias",
                Eyebrow = "Ganancias claras",
                Title = "Gestiona la rentabilidad diaria sin esperar al cierre del mes",
                Summary = "Visualiza cuanto entra por servicios, productos y equipo desde un solo frente operativo.",
                Solution = "LuxuryCloud ordena la caja diaria y la convierte en una lectura simple para tomar decisiones rapidas.",
                Icon = "bi-graph-up-arrow",
                MockupTitle = "Resumen de ganancias",
                PrimaryMetric = "+18%",
                PrimaryLabel = "margen semanal",
                SecondaryMetric = "CRC 486k",
                SecondaryLabel = "ingreso del dia",
                TertiaryMetric = "12",
                TertiaryLabel = "servicios cobrados",
                Tags = [ "servicios", "productos", "equipo" ]
            },
            new()
            {
                Id = "finanzas",
                Eyebrow = "Ingresos y egresos",
                Title = "Controla entradas y salidas con una vista financiera que si ayuda a operar",
                Summary = "Evita perder dinero por gastos invisibles y detecta variaciones antes de que afecten la liquidez.",
                Solution = "Cada movimiento queda clasificado para que el negocio vea flujo, categorias y rentabilidad real.",
                Icon = "bi-cash-stack",
                MockupTitle = "Flujo del negocio",
                PrimaryMetric = "92%",
                PrimaryLabel = "egresos categorizados",
                SecondaryMetric = "6",
                SecondaryLabel = "alertas activas",
                TertiaryMetric = "CRC 1.2M",
                TertiaryLabel = "flujo semanal",
                Tags = [ "caja", "gastos", "margen" ],
                ReverseLayout = true
            },
            new()
            {
                Id = "calendario",
                Eyebrow = "Agenda centralizada",
                Title = "Organiza citas y calendario con una agenda preparada para un negocio de servicios",
                Summary = "Reduce huecos, cruces y olvidos con una vista pensada para la operacion diaria del salon o barberia.",
                Solution = "La agenda conecta equipo, horarios y clientes para mover el dia con menos friccion.",
                Icon = "bi-calendar2-week",
                MockupTitle = "Agenda del dia",
                PrimaryMetric = "34",
                PrimaryLabel = "citas previstas",
                SecondaryMetric = "4",
                SecondaryLabel = "espacios libres",
                TertiaryMetric = "3",
                TertiaryLabel = "reagendadas",
                Tags = [ "agenda", "horarios", "recordatorios" ]
            },
            new()
            {
                Id = "funcionarios",
                Eyebrow = "Equipo alineado",
                Title = "Administra funcionarios, roles y productividad desde la misma plataforma",
                Summary = "Consulta actividad, pagos y carga operativa del equipo sin depender de reportes manuales.",
                Solution = "El modulo de funcionarios mantiene la operacion visible para ordenar turnos, rendimiento y seguimiento.",
                Icon = "bi-people-fill",
                MockupTitle = "Rendimiento del equipo",
                PrimaryMetric = "9",
                PrimaryLabel = "funcionarios activos",
                SecondaryMetric = "87%",
                SecondaryLabel = "ocupacion promedio",
                TertiaryMetric = "CRC 74k",
                TertiaryLabel = "pago semanal",
                Tags = [ "equipo", "ocupacion", "pagos" ],
                ReverseLayout = true
            },
            new()
            {
                Id = "clientes",
                Eyebrow = "Clientes fidelizados",
                Title = "Gestiona clientes con contexto real para vender mejor y atender mejor",
                Summary = "Consolida historial, frecuencia y contacto para que cada visita tenga seguimiento comercial.",
                Solution = "LuxuryCloud vuelve accionable la informacion del cliente para mejorar retencion y experiencia.",
                Icon = "bi-person-hearts",
                MockupTitle = "Relacion con clientes",
                PrimaryMetric = "68%",
                PrimaryLabel = "clientes recurrentes",
                SecondaryMetric = "146",
                SecondaryLabel = "perfiles activos",
                TertiaryMetric = "21",
                TertiaryLabel = "cumpleanos del mes",
                Tags = [ "historial", "retencion", "contacto" ]
            },
            new()
            {
                Id = "inventario",
                Eyebrow = "Inventario bajo control",
                Title = "Controla productos e inventario sin perder visibilidad entre ventas y reposicion",
                Summary = "Sabe que se vende, que rota y que debes reponer antes de afectar el servicio al cliente.",
                Solution = "El inventario deja de ser reactivo y pasa a ser parte del control operativo del negocio.",
                Icon = "bi-box2-heart",
                MockupTitle = "Inventario comercial",
                PrimaryMetric = "18",
                PrimaryLabel = "productos criticos",
                SecondaryMetric = "126",
                SecondaryLabel = "items en stock",
                TertiaryMetric = "11",
                TertiaryLabel = "mas vendidos",
                Tags = [ "stock", "rotacion", "ventas" ],
                ReverseLayout = true
            },
            new()
            {
                Id = "dashboard",
                Eyebrow = "Vision ejecutiva",
                Title = "Visualiza dashboard financiero e informacion del negocio con lectura inmediata",
                Summary = "Une operacion, agenda y finanzas en una misma capa visual para decidir con contexto completo.",
                Solution = "Ideal para quienes quieren dirigir el negocio con menos intuicion y mas evidencia diaria.",
                Icon = "bi-speedometer2",
                MockupTitle = "Centro de control",
                PrimaryMetric = "7",
                PrimaryLabel = "indicadores clave",
                SecondaryMetric = "3",
                SecondaryLabel = "modulos conectados",
                TertiaryMetric = "1",
                TertiaryLabel = "vista ejecutiva",
                Tags = [ "dashboard", "KPIs", "decision" ]
            }
        ];

        public async Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetPlanCardsAsync(
        CancellationToken cancellationToken = default)
        {
            var plans = await LoadAvailablePlansAsync(cancellationToken);
            return plans
                .Where(IsPublicBasePlan)
                .OrderBy(plan => plan.PrecioMensual)
                .Select(MapPlanCard)
                .ToArray();
        }

        /// <summary>
        /// Catálogo comercial compacto para la landing. Fuente de verdad ÚNICA: el mismo
        /// <see cref="ISubscriptionPricingCatalog"/> que alimenta el calculador de /Billing/Planes
        /// (planes LC_M_/LC_A_). El "Desde ₡X" es el plan mensual de UN integrante (LC_M_01),
        /// nunca un Min() sobre todos los planes. Excluye por construcción legacy/TEST/internos/add-ons.
        /// </summary>
        public async Task<CommercialPricingPreview> GetCommercialPricingPreviewAsync(
            CancellationToken cancellationToken = default)
        {
            if (_cache.TryGetValue(CommercialPricingPreviewCacheKey, out CommercialPricingPreview? cached) &&
                cached is not null)
            {
                return cached;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // El precio de entrada es SIEMPRE el plan mensual de un integrante (LC_M_01),
            // resuelto explícitamente por el catálogo. Si no está disponible, no hay preview.
            var starting = _pricingCatalog.Resolve(PlanCodes.CalculatorMinWorkers, BillingCycle.Monthly);
            if (!starting.IsAvailable || starting.Option is null)
            {
                // No se cachea el estado degradado: se reintenta en el siguiente request.
                return CommercialPricingPreview.Unavailable();
            }

            var options = _pricingCatalog.ListPublicAvailable();
            var tiers = options
                .Select(option => new CommercialPricingTier
                {
                    Workers = option.WorkerCount,
                    Cycle = option.BillingCycle == BillingCycle.Annual ? "Annual" : "Monthly",
                    ChargeAmount = option.ChargeAmount,
                    MonthlyEquivalentAmount = option.MonthlyEquivalentAmount
                })
                .ToArray();

            // El complemento de WhatsApp viene del catálogo de ADD-ONS (separado de los planes
            // base). Si falla o no hay add-ons, se omite el precio; nunca contamina el "Desde ₡X".
            var whatsAppFromCharge = await TryGetWhatsAppFromChargeAsync(cancellationToken);

            var preview = new CommercialPricingPreview
            {
                IsAvailable = true,
                Currency = starting.Option.Currency,
                MinWorkers = PlanCodes.CalculatorMinWorkers,
                MaxWorkers = PlanCodes.CalculatorMaxWorkers,
                StartingMonthlyCharge = starting.Option.ChargeAmount,
                HasMonthly = options.Any(option => option.BillingCycle == BillingCycle.Monthly),
                HasAnnual = options.Any(option => option.BillingCycle == BillingCycle.Annual),
                WhatsAppFromCharge = whatsAppFromCharge,
                Tiers = tiers
            };

            // Solo se cachea un preview completo y exitoso.
            _cache.Set(
                CommercialPricingPreviewCacheKey,
                preview,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = AvailablePlansCacheTtl
                });

            return preview;
        }

        private async Task<decimal?> TryGetWhatsAppFromChargeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var addonCards = await GetWhatsAppAddonCardsAsync(cancellationToken);
                var addonCharges = addonCards
                    .Where(card => card.IsAddon && card.MonthlyPrice > 0)
                    .Select(card => card.MonthlyPrice)
                    .ToArray();

                return addonCharges.Length > 0 ? addonCharges.Min() : null;
            }
            catch (OperationCanceledException)
            {
                // La cancelación del cliente sí debe propagarse (semántica correcta).
                throw;
            }
            catch
            {
                // El complemento de WhatsApp es opcional: si el catálogo de add-ons falla,
                // la landing sigue mostrando el precio base sin el "desde" de WhatsApp.
                return null;
            }
        }

        public async Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetWhatsAppAddonCardsAsync(
            CancellationToken cancellationToken = default)
        {
            var plans = await LoadAvailablePlansAsync(cancellationToken);
            return plans
                .Where(IsPublicAddonPlan)
                .OrderBy(plan => plan.PrecioMensual)
                .Select(MapPlanCard)
                .ToArray();
        }

        public async Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetInternalPlanCardsAsync(
            CancellationToken cancellationToken = default)
        {
            if (!_paymentOptions.EnableValidationPlans ||
                !_tilopayRepeatOptions.EnableTestRecurringPlan)
            {
                return Array.Empty<MarketingPlanCardViewModel>();
            }

            var plans = await LoadAvailablePlansAsync(cancellationToken);
            return plans
                .Where(IsInternalPlan)
                .OrderBy(plan => plan.PrecioMensual)
                .Select(MapPlanCard)
                .ToArray();
        }

        public Task<Plan?> FindAvailablePlanAsync(Guid planId, CancellationToken cancellationToken = default) =>
            FindAvailablePlanCoreAsync(planId, cancellationToken);

        public async Task<string?> GetPlanNameAsync(Guid? planId, CancellationToken cancellationToken = default)
        {
            if (!planId.HasValue || planId.Value == Guid.Empty)
            {
                return null;
            }

            var plan = await FindAvailablePlanCoreAsync(planId.Value, cancellationToken);
            return plan?.Nombre;
        }

        private IQueryable<Plan> BuildAvailablePlansQuery() =>
            _context.Planes.Where(plan =>
                plan.Activo &&
                (!plan.EsPlanValidacion || _paymentOptions.EnableValidationPlans));

        /// <summary>
        /// Devuelve los planes disponibles como snapshots proyectados y cacheados.
        /// Nunca se cachea una cancelación ni una excepción: ambas rompen el flujo antes de
        /// <see cref="IMemoryCache.Set"/>, por lo que un visitante que abandona no deja
        /// datos inválidos en cache.
        /// </summary>
        private async Task<IReadOnlyCollection<PublicPlanSnapshot>> LoadAvailablePlansAsync(
            CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(AvailablePlansCacheKey, out IReadOnlyCollection<PublicPlanSnapshot>? cachedPlans) &&
                cachedPlans is not null)
            {
                return cachedPlans;
            }

            // Si el request ya fue abortado no consultamos la base de datos ni contaminamos
            // la cache; respetamos la cancelación lanzando la excepción estándar.
            cancellationToken.ThrowIfCancellationRequested();

            // Proyección directa a los campos que necesitan las cards. No se usa Include:
            // el join a Feature se resuelve dentro del Select y no arrastra entidades EF.
            var plans = await BuildAvailablePlansQuery()
                .AsNoTracking()
                .OrderBy(plan => plan.PrecioMensual)
                .Select(plan => new PublicPlanSnapshot
                {
                    Id = plan.Id,
                    Codigo = plan.Codigo,
                    Nombre = plan.Nombre,
                    PrecioMensual = plan.PrecioMensual,
                    Moneda = plan.Moneda,
                    MaxFuncionarios = plan.MaxFuncionarios,
                    LimiteMensajesMensual = plan.LimiteMensajesMensual,
                    EsPlanValidacion = plan.EsPlanValidacion,
                    Features = plan.PlanFeatures
                        .Select(planFeature => new PublicPlanFeatureSnapshot(
                            planFeature.Feature != null ? planFeature.Feature.Nombre : null,
                            planFeature.Limite))
                        .ToList()
                })
                .ToListAsync(cancellationToken);

            var snapshot = (IReadOnlyCollection<PublicPlanSnapshot>)plans;

            // Solo llega aquí un resultado completo y exitoso.
            _cache.Set(
                AvailablePlansCacheKey,
                snapshot,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = AvailablePlansCacheTtl
                });

            return snapshot;
        }

        private async Task<Plan?> FindAvailablePlanCoreAsync(Guid planId, CancellationToken cancellationToken)
        {
            if (planId == Guid.Empty)
            {
                return null;
            }

            // La ruta de registro sí necesita la entidad real, no el snapshot, por lo que se
            // consulta el plan puntual (sin cache). Es un flujo frío, no el de la landing.
            var plan = await BuildAvailablePlansQuery()
                .AsNoTracking()
                .Include(candidate => candidate.PlanFeatures)
                    .ThenInclude(planFeature => planFeature.Feature)
                .FirstOrDefaultAsync(candidate => candidate.Id == planId, cancellationToken);

            if (plan is null)
            {
                return null;
            }

            var snapshot = ToSnapshot(plan);
            return IsPublicBasePlan(snapshot) || IsPublicAddonPlan(snapshot) || IsInternalPlan(snapshot)
                ? plan
                : null;
        }

        private static PublicPlanSnapshot ToSnapshot(Plan plan) =>
            new()
            {
                Id = plan.Id,
                Codigo = plan.Codigo,
                Nombre = plan.Nombre,
                PrecioMensual = plan.PrecioMensual,
                Moneda = plan.Moneda,
                MaxFuncionarios = plan.MaxFuncionarios,
                LimiteMensajesMensual = plan.LimiteMensajesMensual,
                EsPlanValidacion = plan.EsPlanValidacion,
                Features = plan.PlanFeatures
                    .Select(planFeature => new PublicPlanFeatureSnapshot(
                        planFeature.Feature?.Nombre,
                        planFeature.Limite))
                    .ToList()
            };

        private MarketingPlanCardViewModel MapPlanCard(PublicPlanSnapshot plan)
        {
            var highlights = plan.Features
                .Select(FormatFeature)
                .Where(highlight => !string.IsNullOrWhiteSpace(highlight))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();

            if (highlights.Length == 0)
            {
                highlights = plan.LimiteMensajesMensual.HasValue
                    ?
                    [
                        $"{plan.LimiteMensajesMensual.Value} mensajes automaticos al mes",
                        "Recordatorios y confirmaciones por WhatsApp",
                        "Consumo controlado por periodo mensual",
                        "Ideal para recordatorios automáticos"
                    ]
                    :
                    [
                        "Agenda comercial del equipo",
                        "Control de ingresos y egresos",
                        plan.MaxFuncionarios.HasValue
                            ? $"Hasta {plan.MaxFuncionarios.Value} funcionarios"
                            : "Funcionarios ilimitados",
                        "Dashboard operativo"
                    ];
            }

            var isAddon = plan.LimiteMensajesMensual.HasValue;
            var isFeatured = string.Equals(plan.Codigo, PlanCodes.Business, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plan.Nombre, "Empresarial", StringComparison.OrdinalIgnoreCase);
            var checkoutAvailability = ResolveCheckoutAvailability(plan);

            return new MarketingPlanCardViewModel
            {
                Id = plan.Id,
                Code = plan.Codigo,
                Name = plan.Nombre,
                BillingLabel = "por mes",
                MonthlyPrice = plan.PrecioMensual,
                CurrencyCode = string.IsNullOrWhiteSpace(plan.Moneda) ? "CRC" : plan.Moneda,
                StaffLabel = isAddon
                    ? $"{plan.LimiteMensajesMensual ?? 0} mensajes al mes"
                    : plan.MaxFuncionarios.HasValue
                        ? $"Hasta {plan.MaxFuncionarios.Value} funcionarios"
                        : "Funcionarios ilimitados",
                MonthlyMessageLimit = plan.LimiteMensajesMensual,
                Summary = plan.EsPlanValidacion
                    ? "Plan controlado para validar el primer cobro real con riesgo financiero minimo."
                    : isAddon
                        ? "Agrega recordatorios y confirmaciones automaticas por WhatsApp sin mezclarlo con el plan base."
                        : isFeatured
                        ? "Pensado para operaciones con mayor ritmo, mas equipo y necesidad de visibilidad completa."
                        : "Una base profesional para ordenar la operacion comercial desde el primer dia.",
                BadgeText = plan.EsPlanValidacion
                    ? "TEST interno"
                    : isAddon
                        ? "Add-on"
                    : isFeatured
                        ? "Mas elegido"
                        : null,
                IsFeatured = isFeatured,
                IsValidationPlan = plan.EsPlanValidacion,
                IsAddon = isAddon,
                CanStartCheckout = checkoutAvailability.CanStartCheckout,
                CheckoutAvailabilityMessage = checkoutAvailability.Message,
                Highlights = highlights
            };
        }

        private bool IsPublicBasePlan(PublicPlanSnapshot plan)
        {
            var code = plan.Codigo?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                return !plan.LimiteMensajesMensual.HasValue && !plan.EsPlanValidacion;
            }

            return code is PlanCodes.Basic or PlanCodes.Pro or PlanCodes.Business or PlanCodes.TestProdBasic100;
        }

        private bool IsPublicAddonPlan(PublicPlanSnapshot plan)
        {
            var code = plan.Codigo?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                return plan.LimiteMensajesMensual.HasValue;
            }

            return code is PlanCodes.WhatsApp400 or PlanCodes.WhatsApp800 or PlanCodes.WhatsApp1200;
        }

        private bool IsInternalPlan(PublicPlanSnapshot plan)
        {
            if (!_paymentOptions.EnableValidationPlans ||
                !_tilopayRepeatOptions.Enabled ||
                !_tilopayRepeatOptions.EnableTestRecurringPlan)
            {
                return false;
            }

            return string.Equals(
                plan.Codigo,
                PlanCodes.TestRecurring,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatFeature(PublicPlanFeatureSnapshot planFeature)
        {
            var featureName = planFeature.Nombre?.Trim();
            if (string.IsNullOrWhiteSpace(featureName))
            {
                return string.Empty;
            }

            return planFeature.Limite.HasValue
                ? $"{featureName} hasta {planFeature.Limite.Value}"
                : featureName;
        }

        private CheckoutAvailability ResolveCheckoutAvailability(PublicPlanSnapshot plan)
        {
            var code = plan.Codigo?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                return CheckoutAvailability.Enabled();
            }

            if (!TilopayRepeatOptions.IsManagedPlanCode(code))
            {
                return CheckoutAvailability.Enabled();
            }

            var repeatPlan = _tilopayRepeatOptions.FindByCode(code);
            if (repeatPlan is null)
            {
                return CheckoutAvailability.Disabled(
                    $"El plan {code} no tiene mapping recurrente configurado.");
            }

            if (!_tilopayRepeatOptions.Enabled)
            {
                return CheckoutAvailability.Disabled(
                    "Tilopay Repeat esta deshabilitado: TilopayRepeat:Enabled=false.");
            }

            if (!_tilopayRepeatOptions.UseHostedLinks)
            {
                return CheckoutAvailability.Disabled(
                    "Tilopay Repeat requiere hosted links: TilopayRepeat:UseHostedLinks=false.");
            }

            if (plan.EsPlanValidacion && !_tilopayRepeatOptions.EnableTestRecurringPlan)
            {
                return CheckoutAvailability.Disabled(
                    "El plan TEST recurrente esta deshabilitado: TilopayRepeat:EnableTestRecurringPlan=false.");
            }

            if (code is PlanCodes.Basic or PlanCodes.Pro or PlanCodes.Business &&
                !_tilopayRepeatOptions.UseRecurringCheckoutForPublicPlans)
            {
                return CheckoutAvailability.Disabled(
                    "Tilopay Repeat esta deshabilitado para planes publicos: TilopayRepeat:UseRecurringCheckoutForPublicPlans=false.");
            }

            if (string.IsNullOrWhiteSpace(_tilopayOptions.WebhookAccessToken))
            {
                return CheckoutAvailability.Disabled(
                    "Falta WebhookAccessToken: Tilopay:WebhookAccessToken.");
            }

            var sectionKey = TilopayRepeatOptions.ResolveSectionKey(code);
            if (string.IsNullOrWhiteSpace(repeatPlan.CheckoutUrl) && !string.IsNullOrWhiteSpace(sectionKey))
            {
                return CheckoutAvailability.Disabled(
                    $"Falta CheckoutUrl para {code}: TilopayRepeat:{sectionKey}:CheckoutUrl.");
            }

            return CheckoutAvailability.Enabled();
        }

        private sealed record CheckoutAvailability(bool CanStartCheckout, string? Message)
        {
            public static CheckoutAvailability Enabled() => new(true, null);

            public static CheckoutAvailability Disabled(string message) => new(false, message);
        }
    }
}
