using System.Text.Json;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.SaaS;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Platform
{
    public sealed class PlatformCommercialSnapshotService : IPlatformCommercialSnapshotService
    {
        private static readonly EstadoSuscripcion[] TerminalStates =
        [
            EstadoSuscripcion.Cancelada,
            EstadoSuscripcion.Vencida,
            EstadoSuscripcion.Suspendida
        ];

        private readonly ApplicationDbContext _context;
        private readonly ITenantCommercialAccessResolver _accessResolver;
        private readonly IPlatformMetricsService _metricsService;
        private readonly IPlatformWhatsAppStatusService _whatsAppStatusService;
        private readonly IPlatformHealthService _healthService;
        private readonly ILogger<PlatformCommercialSnapshotService> _logger;

        public PlatformCommercialSnapshotService(
            ApplicationDbContext context,
            ITenantCommercialAccessResolver accessResolver,
            IPlatformMetricsService metricsService,
            IPlatformWhatsAppStatusService whatsAppStatusService,
            IPlatformHealthService healthService,
            ILogger<PlatformCommercialSnapshotService> logger)
        {
            _context = context;
            _accessResolver = accessResolver;
            _metricsService = metricsService;
            _whatsAppStatusService = whatsAppStatusService;
            _healthService = healthService;
            _logger = logger;
        }

        public async Task<PlatformCommercialSnapshot> CaptureAsync(
            int periodYear,
            int periodMonth,
            string triggerType,
            string? actorEmail,
            CancellationToken cancellationToken = default)
        {
            if (periodMonth is < 1 or > 12)
            {
                throw new ArgumentOutOfRangeException(nameof(periodMonth));
            }

            // Los timestamps de churn son UTC y FechaCreacion del tenant es hora local del
            // servidor; se usan los mismos límites de mes para ambos (desfase máximo de horas,
            // irrelevante en un agregado mensual).
            var periodStart = new DateTime(periodYear, periodMonth, 1);
            var periodEnd = periodStart.AddMonths(1);
            var nowUtc = DateTime.UtcNow;

            // Fuente comercial: suscripciones con su plan, cross-tenant y sin tracking.
            var subscriptions = await _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(subscription => subscription.Plan)
                .ToListAsync(cancellationToken);

            var realSubscriptions = subscriptions
                .Where(subscription => subscription.Plan?.EsPlanValidacion != true)
                .ToList();

            var activeSubscriptions = realSubscriptions
                .Where(subscription => subscription.Estado == EstadoSuscripcion.Activa)
                .ToList();

            var mrrTotal = activeSubscriptions.Sum(NormalizedMonthlyAmount);
            var morosaMrr = realSubscriptions
                .Where(subscription => subscription.Estado == EstadoSuscripcion.Morosa)
                .Sum(NormalizedMonthlyAmount);

            var churned = realSubscriptions
                .Where(subscription =>
                    TerminalStates.Contains(subscription.Estado) &&
                    TerminalTransitionDate(subscription) is { } transition &&
                    transition >= periodStart && transition < periodEnd)
                .ToList();

            var grants = await _context.TenantCommercialAccessGrants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(grant => grant.Activo && grant.FechaFinUtc > nowUtc)
                .ToListAsync(cancellationToken);

            var tenants = await _context.Tenants
                .AsNoTracking()
                .Select(tenant => new { tenant.Id, tenant.FechaCreacion, tenant.Activo })
                .ToListAsync(cancellationToken);

            var healthCounts = await CountTenantHealthAsync(
                tenants.Where(tenant => tenant.Activo).Select(tenant => tenant.Id).ToList(),
                nowUtc,
                cancellationToken);

            var detail = new
            {
                PorPlan = activeSubscriptions
                    .GroupBy(subscription => subscription.Plan?.Codigo ?? subscription.CodigoPlan ?? "SIN_CODIGO")
                    .Select(group => new
                    {
                        Codigo = group.Key,
                        Count = group.Count(),
                        Mrr = group.Sum(NormalizedMonthlyAmount)
                    })
                    .OrderByDescending(item => item.Mrr)
                    .ToArray(),
                PorEstado = realSubscriptions
                    .GroupBy(subscription => subscription.Estado.ToString())
                    .Select(group => new { Estado = group.Key, Count = group.Count() })
                    .ToArray(),
                MorosaMrr = morosaMrr,
                SuscripcionesValidacion = subscriptions.Count - realSubscriptions.Count
            };

            var snapshot = await _context.PlatformCommercialSnapshots
                .FirstOrDefaultAsync(
                    existing => existing.PeriodYear == periodYear && existing.PeriodMonth == periodMonth,
                    cancellationToken);

            if (snapshot is null)
            {
                snapshot = new PlatformCommercialSnapshot();
                _context.PlatformCommercialSnapshots.Add(snapshot);
            }

            snapshot.PeriodYear = periodYear;
            snapshot.PeriodMonth = periodMonth;
            snapshot.CapturedAtUtc = nowUtc;
            snapshot.TriggerType = triggerType;
            snapshot.TriggeredByEmail = actorEmail;
            snapshot.MrrTotal = mrrTotal;
            snapshot.ArrTotal = Math.Round(mrrTotal * 12m, 2, MidpointRounding.ToEven);
            snapshot.ActiveSubscriptions = activeSubscriptions.Count;
            snapshot.MonthlyCycleCount = activeSubscriptions.Count(subscription => Cycle(subscription) == BillingCycle.Monthly);
            snapshot.AnnualCycleCount = activeSubscriptions.Count(subscription => Cycle(subscription) == BillingCycle.Annual);
            snapshot.TenantsTotal = tenants.Count;
            snapshot.TenantsSaludable = healthCounts[TenantHealthState.Saludable];
            snapshot.TenantsAtencion = healthCounts[TenantHealthState.Atencion];
            snapshot.TenantsRiesgo = healthCounts[TenantHealthState.Riesgo];
            snapshot.TenantsSinAcceso = healthCounts[TenantHealthState.SinAcceso];
            snapshot.TrialsActivos = grants.Count;
            snapshot.TrialsPorVencer7d = grants.Count(grant => grant.FechaFinUtc <= nowUtc.AddDays(7));
            snapshot.ChurnedTenants = churned.Select(subscription => subscription.TenantId).Distinct().Count();
            snapshot.ChurnedMrr = churned.Sum(NormalizedMonthlyAmount);
            snapshot.NewTenants = tenants.Count(tenant =>
                tenant.FechaCreacion >= periodStart && tenant.FechaCreacion < periodEnd);
            snapshot.DetailJson = JsonSerializer.Serialize(detail);

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Snapshot comercial {Year}-{Month:00} capturado ({Trigger}). MRR {Mrr}. Suscripciones activas {Active}. Tenants {Tenants}.",
                periodYear,
                periodMonth,
                triggerType,
                snapshot.MrrTotal,
                snapshot.ActiveSubscriptions,
                snapshot.TenantsTotal);

            return snapshot;
        }

        public Task<bool> HasCaptureAsync(
            int periodYear,
            int periodMonth,
            string triggerType,
            CancellationToken cancellationToken = default) =>
            _context.PlatformCommercialSnapshots
                .AsNoTracking()
                .AnyAsync(
                    snapshot => snapshot.PeriodYear == periodYear &&
                        snapshot.PeriodMonth == periodMonth &&
                        snapshot.TriggerType == triggerType,
                    cancellationToken);

        public async Task<IReadOnlyList<PlatformCommercialSnapshot>> GetHistoryAsync(
            int take = 24,
            CancellationToken cancellationToken = default) =>
            await _context.PlatformCommercialSnapshots
                .AsNoTracking()
                .OrderByDescending(snapshot => snapshot.PeriodYear)
                .ThenByDescending(snapshot => snapshot.PeriodMonth)
                .Take(Math.Clamp(take, 1, 120))
                .ToListAsync(cancellationToken);

        /// <summary>
        /// Monto por ciclo normalizado a mensual: el precio del plan anual es el total anual
        /// adelantado (÷ 12). Redondeo half-even como el motor fiscal. El precio snapshot de
        /// la suscripción tiene prioridad sobre el del plan cuando existe.
        /// </summary>
        private static decimal NormalizedMonthlyAmount(Suscripcion subscription)
        {
            var amountPerCycle = subscription.PrecioMensual ?? subscription.Plan?.PrecioMensual ?? 0m;
            var monthly = Cycle(subscription) == BillingCycle.Annual
                ? amountPerCycle / 12m
                : amountPerCycle;

            return Math.Round(monthly, 2, MidpointRounding.ToEven);
        }

        private static BillingCycle Cycle(Suscripcion subscription) =>
            subscription.Plan?.BillingCycle ?? BillingCycle.Monthly;

        /// <summary>Mejor timestamp disponible de la transición a estado terminal.</summary>
        private static DateTime? TerminalTransitionDate(Suscripcion subscription) =>
            subscription.FechaCancelacionUtc
                ?? subscription.FechaFin
                ?? subscription.FechaUltimaActualizacionUtc;

        /// <summary>
        /// Cuenta tenants por estado de salud reutilizando ComputeHealth (los umbrales viven
        /// SOLO ahí, AD-3) alimentado con los servicios batch existentes. O(N) una vez al mes.
        /// </summary>
        private async Task<Dictionary<TenantHealthState, int>> CountTenantHealthAsync(
            IReadOnlyList<Guid> tenantIds,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var counts = Enum.GetValues<TenantHealthState>().ToDictionary(state => state, _ => 0);
            if (tenantIds.Count == 0)
            {
                return counts;
            }

            var usageByTenant = await _metricsService.GetTenantUsageBatchAsync(tenantIds, cancellationToken);
            var whatsAppByTenant = await _whatsAppStatusService.GetBatchStatusAsync(tenantIds, cancellationToken);

            var tenantsWithPendingCheckout = (await _context.PagosSuscripcion
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(payment =>
                        payment.Estado == EstadoPagoProveedor.Pendiente ||
                        payment.Estado == EstadoPagoProveedor.ManualReview)
                    .Select(payment => payment.TenantId)
                    .Distinct()
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            foreach (var tenantId in tenantIds)
            {
                var access = await _accessResolver.ResolveAsync(tenantId, cancellationToken: cancellationToken);
                usageByTenant.TryGetValue(tenantId, out var usage);
                whatsAppByTenant.TryGetValue(tenantId, out var whatsApp);

                var health = _healthService.ComputeHealth(
                    access.CanAccessApp,
                    usage ?? new PlatformTenantUsageViewModel(),
                    whatsApp?.SettingsEnabled == true,
                    whatsApp?.LastErrorCode is not null,
                    tenantsWithPendingCheckout.Contains(tenantId),
                    access.AccessEndsUtc.HasValue && access.AccessEndsUtc.Value <= nowUtc.AddDays(7));

                counts[health.State]++;
            }

            return counts;
        }
    }
}
