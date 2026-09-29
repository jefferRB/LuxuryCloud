using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.SaaS
{
    public sealed class TenantCommercialAccessResolver : ITenantCommercialAccessResolver
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

        private readonly ApplicationDbContext _context;
        private readonly IMemoryCache _cache;
        private readonly ITenantCommercialAccessCache _accessCache;
        private readonly SuscripcionService _suscripcionService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public TenantCommercialAccessResolver(
            ApplicationDbContext context,
            IMemoryCache cache,
            ITenantCommercialAccessCache accessCache,
            SuscripcionService suscripcionService,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _cache = cache;
            _accessCache = accessCache;
            _suscripcionService = suscripcionService;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task<TenantCommercialAccessResult> ResolveAsync(
            Guid tenantId,
            AppUsuario? user = null,
            CancellationToken cancellationToken = default)
        {
            if (tenantId == Guid.Empty)
            {
                return Denied(
                    tenantId,
                    TenantCommercialAccessMode.RequiresSubscription,
                    "No fue posible resolver el tenant.");
            }

            if (user?.IsPlatformSuperAdmin == true)
            {
                var superAdminPlan = await ResolvePlanAsync(tenantId, cancellationToken);
                return new TenantCommercialAccessResult
                {
                    CanAccessApp = true,
                    RequiresBilling = false,
                    IsPlatformSuperAdmin = true,
                    TenantId = tenantId,
                    EffectivePlanId = superAdminPlan?.Id,
                    EffectivePlanName = superAdminPlan?.Nombre,
                    EffectivePlanCode = superAdminPlan?.Codigo,
                    EffectivePlanKind = PlanCatalogRules.Classify(superAdminPlan),
                    EffectiveEmployeeLimit = superAdminPlan?.MaxFuncionarios,
                    BillingSource = TenantAccessBillingSource.Manual,
                    CommercialAccessMode = superAdminPlan is null
                        ? TenantCommercialAccessMode.RequiresSubscription
                        : TenantCommercialAccessMode.Internal,
                    AccessSource = TenantCommercialAccessSource.PlatformSuperAdmin,
                    Reason = "Acceso interno de plataforma.",
                    HasCommercialHistory = true
                };
            }

            var cacheKey = _accessCache.BuildTenantKey(tenantId);
            if (_cache.TryGetValue(cacheKey, out TenantCommercialAccessResult? cached) && cached is not null)
            {
                return cached;
            }

            var result = await ResolveCoreAsync(tenantId, cancellationToken);
            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }

        private async Task<TenantCommercialAccessResult> ResolveCoreAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            var tenant = await _context.Tenants
                .AsNoTracking()
                .Include(t => t.ForcedPlan)
                .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

            if (tenant is null)
            {
                return Denied(
                    tenantId,
                    TenantCommercialAccessMode.RequiresSubscription,
                    "El tenant no existe.");
            }

            if (!tenant.Activo)
            {
                return Denied(
                    tenantId,
                    tenant.CommercialAccessMode,
                    "El tenant se encuentra suspendido o inactivo.");
            }

            if (tenant.CommercialAccessMode == TenantCommercialAccessMode.PendingVerification)
            {
                return Denied(
                    tenantId,
                    tenant.CommercialAccessMode,
                    "Registro pendiente de confirmacion de correo.",
                    hasCommercialHistory: false,
                    forcedPlanId: tenant.ForcedPlanId);
            }

            if (tenant.CommercialAccessMode == TenantCommercialAccessMode.Exempt ||
                tenant.CommercialAccessMode == TenantCommercialAccessMode.Internal)
            {
                var forcedPlan = tenant.ForcedPlan;
                if (forcedPlan is null || !forcedPlan.Activo)
                {
                    return Denied(
                        tenantId,
                        tenant.CommercialAccessMode,
                        "El tenant tiene acceso comercial especial, pero no tiene un plan forzado valido.",
                        forcedPlanId: tenant.ForcedPlanId,
                        warnings: forcedPlan is null
                            ? new[] { "Modo comercial especial sin plan forzado: el tenant queda sin limite ni acceso." }
                            : new[] { $"El plan forzado '{forcedPlan.Nombre}' esta INACTIVO en el catalogo." });
                }

                // El plan forzado define el limite efectivo. Se clasifica para detectar la
                // configuracion invalida de haber forzado un add-on de WhatsApp como plan base
                // (sin MaxFuncionarios), que la validacion server-side ya bloquea al guardar.
                var forcedKind = PlanCatalogRules.Classify(forcedPlan);
                var forcedWarnings = new List<string>();

                if (!PlanCatalogRules.IsBasePlan(forcedKind))
                {
                    forcedWarnings.Add(
                        $"El plan forzado '{forcedPlan.Nombre}' no es un plan base valido " +
                        $"({PlanCatalogRules.DescribeKind(forcedKind)}). El limite de funcionarios no es confiable.");
                }
                else if (forcedKind == PlanCatalogKind.LegacyBase || forcedKind == PlanCatalogKind.Validation)
                {
                    forcedWarnings.Add(
                        $"El plan forzado es {PlanCatalogRules.DescribeKind(forcedKind).ToLowerInvariant()} " +
                        $"('{forcedPlan.Nombre}'). Migrar a un plan de la calculadora (LC_M_/LC_A_).");
                }

                return new TenantCommercialAccessResult
                {
                    CanAccessApp = true,
                    RequiresBilling = false,
                    TenantId = tenantId,
                    EffectivePlanId = forcedPlan.Id,
                    EffectivePlanName = forcedPlan.Nombre,
                    EffectivePlanCode = forcedPlan.Codigo,
                    EffectivePlanKind = forcedKind,
                    // Un add-on forzado no aporta limite de funcionarios: se deja null y se advierte,
                    // en vez de heredar el LimiteMensajesMensual como si fuera un cupo de personal.
                    EffectiveEmployeeLimit = PlanCatalogRules.IsBasePlan(forcedKind)
                        ? forcedPlan.MaxFuncionarios
                        : null,
                    IsForcedByPlatform = true,
                    ForcedPlanId = tenant.ForcedPlanId,
                    BillingSource = TenantAccessBillingSource.Manual,
                    CommercialAccessMode = tenant.CommercialAccessMode,
                    AccessSource = tenant.CommercialAccessMode == TenantCommercialAccessMode.Internal
                        ? TenantCommercialAccessSource.TenantInternal
                        : TenantCommercialAccessSource.TenantExempt,
                    Reason = tenant.CommercialAccessMode == TenantCommercialAccessMode.Internal
                        ? "Tenant interno con acceso operativo."
                        : "Tenant exento con acceso patrocinado.",
                    HasCommercialHistory = true,
                    Warnings = forcedWarnings
                };
            }

            var now = _businessDateTimeProvider.NowOffset().UtcDateTime;
            var grant = await _context.TenantCommercialAccessGrants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(g => g.Plan)
                .Where(g =>
                    g.TenantId == tenantId &&
                    g.Activo &&
                    g.FechaInicioUtc <= now &&
                    g.FechaFinUtc >= now)
                .OrderByDescending(g => g.FechaFinUtc)
                .ThenByDescending(g => g.FechaInicioUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (grant is not null && grant.Plan is not null && grant.Plan.Activo)
            {
                var grantKind = PlanCatalogRules.Classify(grant.Plan);
                return new TenantCommercialAccessResult
                {
                    CanAccessApp = true,
                    RequiresBilling = grant.RequiresBilling,
                    TenantId = tenantId,
                    EffectivePlanId = grant.PlanId,
                    EffectivePlanName = grant.Plan.Nombre,
                    EffectivePlanCode = grant.Plan.Codigo,
                    EffectivePlanKind = grantKind,
                    EffectiveEmployeeLimit = PlanCatalogRules.IsBasePlan(grantKind)
                        ? grant.Plan.MaxFuncionarios
                        : null,
                    ForcedPlanId = tenant.ForcedPlanId,
                    BillingSource = TenantAccessBillingSource.PromotionalGrant,
                    CommercialAccessMode = tenant.CommercialAccessMode,
                    AccessSource = TenantCommercialAccessSource.PromotionalGrant,
                    Reason = "Acceso comercial temporal activo.",
                    AccessEndsUtc = grant.FechaFinUtc,
                    HasCommercialHistory = true
                };
            }

            var suscripcion = await _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(s => s.Plan)
                .Where(s => s.TenantId == tenantId)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .ThenByDescending(s => s.FechaInicio)
                .FirstOrDefaultAsync(cancellationToken);

            if (suscripcion is not null &&
                suscripcion.Plan is not null &&
                suscripcion.Plan.Activo)
            {
                var effectiveStatus = _suscripcionService.GetEffectiveStatus(suscripcion);
                var canAccessApp = _suscripcionService.CanAccessApp(suscripcion);
                var isInGracePeriod = effectiveStatus == EstadoSuscripcion.Morosa;

                // El plan efectivo del camino pagado es el de la suscripcion. El limite prioriza el
                // snapshot de la suscripcion (lo que el cliente realmente compro) y cae al plan.
                var subKind = PlanCatalogRules.Classify(suscripcion.Plan);
                var subLimit = PlanCatalogRules.IsBasePlan(subKind)
                    ? suscripcion.MaxFuncionarios ?? suscripcion.Plan.MaxFuncionarios
                    : null;
                var subWarnings = new List<string>();

                if (!PlanCatalogRules.IsBasePlan(subKind))
                {
                    subWarnings.Add(
                        $"La suscripcion base apunta a '{suscripcion.Plan.Nombre}' que no es un plan base " +
                        $"({PlanCatalogRules.DescribeKind(subKind)}).");
                }

                // Plan forzado configurado pero en modo RequiresSubscription: no aplica. Se avisa
                // para que plataforma no crea que el limite viene del plan que ve en el selector.
                if (tenant.ForcedPlanId.HasValue)
                {
                    subWarnings.Add(
                        "El tenant tiene un plan forzado configurado pero su modo comercial es " +
                        "'Requiere suscripcion': manda la suscripcion pagada, el plan forzado se ignora.");
                }

                var subBillingSource = suscripcion.Proveedor == PaymentProviderType.Tilopay ||
                                       suscripcion.TilopayRecurringPlanId.HasValue
                    ? TenantAccessBillingSource.ProviderRecurring
                    : TenantAccessBillingSource.Legacy;

                if (canAccessApp)
                {
                    return new TenantCommercialAccessResult
                    {
                        CanAccessApp = true,
                        RequiresBilling = true,
                        TenantId = tenantId,
                        EffectivePlanId = suscripcion.PlanId,
                        EffectivePlanName = suscripcion.Plan.Nombre,
                        EffectivePlanCode = suscripcion.CodigoPlan ?? suscripcion.Plan.Codigo,
                        EffectivePlanKind = subKind,
                        EffectiveEmployeeLimit = subLimit,
                        ForcedPlanId = tenant.ForcedPlanId,
                        BillingSource = subBillingSource,
                        ProviderSubscriptionId = suscripcion.ProviderSubscriptionId,
                        Warnings = subWarnings,
                        CommercialAccessMode = tenant.CommercialAccessMode,
                        AccessSource = effectiveStatus == EstadoSuscripcion.Trial
                            ? TenantCommercialAccessSource.SubscriptionTrial
                            : TenantCommercialAccessSource.SubscriptionActive,
                        Reason = effectiveStatus == EstadoSuscripcion.Trial
                            ? "Acceso permitido por trial vigente."
                            : isInGracePeriod
                                ? "Suscripcion en periodo de gracia por cobro pendiente."
                                : "Acceso permitido por suscripcion activa.",
                        AccessEndsUtc = effectiveStatus == EstadoSuscripcion.Trial
                            ? suscripcion.FechaTrialFin
                            : isInGracePeriod
                                ? suscripcion.FechaFinGraciaUtc
                                : suscripcion.FechaFin,
                        HasCommercialHistory = true,
                        SubscriptionStatus = effectiveStatus,
                        CurrentPeriodEndUtc = suscripcion.FechaFin,
                        NextBillingDateUtc = suscripcion.FechaProximoCobroUtc,
                        GracePeriodEndsUtc = suscripcion.FechaFinGraciaUtc,
                        IsInGracePeriod = isInGracePeriod
                    };
                }

                return new TenantCommercialAccessResult
                {
                    TenantId = tenantId,
                    CommercialAccessMode = tenant.CommercialAccessMode,
                    AccessSource = TenantCommercialAccessSource.None,
                    CanAccessApp = false,
                    RequiresBilling = true,
                    EffectivePlanId = suscripcion.PlanId,
                    EffectivePlanName = suscripcion.Plan.Nombre,
                    EffectivePlanCode = suscripcion.CodigoPlan ?? suscripcion.Plan.Codigo,
                    EffectivePlanKind = subKind,
                    EffectiveEmployeeLimit = subLimit,
                    ForcedPlanId = tenant.ForcedPlanId,
                    BillingSource = subBillingSource,
                    ProviderSubscriptionId = suscripcion.ProviderSubscriptionId,
                    Warnings = subWarnings,
                    Reason = effectiveStatus == EstadoSuscripcion.Suspendida
                        ? "La suscripcion vencio y ya supero el periodo de gracia."
                        : "El tenant no tiene acceso comercial activo.",
                    HasCommercialHistory = true,
                    SubscriptionStatus = effectiveStatus,
                    CurrentPeriodEndUtc = suscripcion.FechaFin,
                    NextBillingDateUtc = suscripcion.FechaProximoCobroUtc,
                    GracePeriodEndsUtc = suscripcion.FechaFinGraciaUtc,
                    IsInGracePeriod = effectiveStatus == EstadoSuscripcion.Morosa
                };
            }

            var hasCommercialHistory = suscripcion is not null ||
                await _context.TenantCommercialAccessGrants
                    .IgnoreQueryFilters()
                    .AnyAsync(grantHistory => grantHistory.TenantId == tenantId, cancellationToken);

            return Denied(
                tenantId,
                tenant.CommercialAccessMode,
                "El tenant no tiene acceso comercial activo.",
                hasCommercialHistory,
                forcedPlanId: tenant.ForcedPlanId);
        }

        private async Task<Plan?> ResolvePlanAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            var tenant = await _context.Tenants
                .AsNoTracking()
                .Include(t => t.ForcedPlan)
                .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

            if (tenant?.ForcedPlan is not null && tenant.ForcedPlan.Activo)
            {
                return tenant.ForcedPlan;
            }

            return await _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.TenantId == tenantId)
                .Include(s => s.Plan)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .Select(s => s.Plan)
                .FirstOrDefaultAsync(cancellationToken);
        }

        private static TenantCommercialAccessResult Denied(
            Guid tenantId,
            TenantCommercialAccessMode commercialAccessMode,
            string reason,
            bool hasCommercialHistory = false,
            Guid? forcedPlanId = null,
            IReadOnlyList<string>? warnings = null) =>
            new()
            {
                TenantId = tenantId,
                CommercialAccessMode = commercialAccessMode,
                AccessSource = TenantCommercialAccessSource.None,
                CanAccessApp = false,
                RequiresBilling = commercialAccessMode == TenantCommercialAccessMode.RequiresSubscription,
                Reason = reason,
                HasCommercialHistory = hasCommercialHistory,
                ForcedPlanId = forcedPlanId,
                BillingSource = TenantAccessBillingSource.None,
                EffectivePlanKind = PlanCatalogKind.Unknown,
                Warnings = warnings ?? Array.Empty<string>()
            };
    }
}
