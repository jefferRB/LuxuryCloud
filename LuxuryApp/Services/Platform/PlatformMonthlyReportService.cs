using LuxuryApp.Models.Platform;
using LuxuryApp.Models.Reports;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Reports;
using LuxuryApp.Services.Tenant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Platform
{
    public sealed class PlatformMonthlyReportService : IPlatformMonthlyReportService
    {
        private readonly ApplicationDbContext _context;
        private readonly IMonthlyReportRecipientResolver _recipientResolver;
        private readonly TenantExecutionService _tenantExecutionService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IOptionsMonitor<MonthlyReportSchedulerOptions> _schedulerOptions;

        public PlatformMonthlyReportService(
            ApplicationDbContext context,
            IMonthlyReportRecipientResolver recipientResolver,
            TenantExecutionService tenantExecutionService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IOptionsMonitor<MonthlyReportSchedulerOptions> schedulerOptions)
        {
            _context = context;
            _recipientResolver = recipientResolver;
            _tenantExecutionService = tenantExecutionService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _schedulerOptions = schedulerOptions;
        }

        public async Task<PlatformMonthlyReportOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            var tenants = await _context.Tenants
                .AsNoTracking()
                .OrderBy(t => t.Nombre)
                .Select(t => new { t.Id, t.Nombre, t.Activo })
                .ToListAsync(cancellationToken);

            // Cross-tenant deliberado (SuperAdmin): las settings son ITenantEntity, se ignora el RLS.
            var settingsByTenant = await _context.TenantMonthlyReportSettings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToDictionaryAsync(s => s.TenantId, cancellationToken);

            var rows = new List<PlatformMonthlyReportRow>(tenants.Count);

            foreach (var tenant in tenants)
            {
                settingsByTenant.TryGetValue(tenant.Id, out var settings);

                var lastLog = await _context.TenantMonthlyReportEmailLogs
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(l => l.TenantId == tenant.Id)
                    .OrderByDescending(l => l.CreatedAt)
                    .ThenByDescending(l => l.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                var resolution = settings is null
                    ? MonthlyReportRecipientResolution.Empty
                    : await _recipientResolver.ResolveAsync(tenant.Id, settings, cancellationToken);

                rows.Add(new PlatformMonthlyReportRow
                {
                    TenantId = tenant.Id,
                    BusinessName = string.IsNullOrWhiteSpace(tenant.Nombre) ? "(sin nombre)" : tenant.Nombre,
                    TenantActivo = tenant.Activo,
                    HasSettings = settings is not null,
                    IsEnabled = settings?.IsEnabled ?? false,
                    SendDayOfMonth = settings?.SendDayOfMonth ?? 1,
                    SendHour = settings?.SendHour ?? 8,
                    RecipientCount = resolution.Included.Count,
                    ExcludedCount = resolution.Excluded.Count,
                    LastSendAt = lastLog?.CreatedAt,
                    LastStatus = lastLog?.Status,
                    LastWasTest = lastLog?.IsTest,
                    LastError = lastLog?.ErrorMessage,
                    LastAutomaticRunAt = settings?.LastAutomaticRunAt,
                    LastAutomaticSentAt = settings?.LastAutomaticSentAt,
                    LastAutomaticError = settings?.LastAutomaticError
                });
            }

            var today = _businessDateTimeProvider.Today();
            var monthStart = new DateTime(today.Year, today.Month, 1);
            var monthEnd = monthStart.AddMonths(1);
            var previous = monthStart.AddMonths(-1);

            // KPIs del mes calendario actual (por fecha del intento).
            var enviadosEsteMes = await _context.TenantMonthlyReportEmailLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(
                    l => !l.IsTest &&
                         l.Status == MonthlyReportEmailStatus.Sent &&
                         l.CreatedAt >= monthStart && l.CreatedAt < monthEnd,
                    cancellationToken);

            var fallidosEsteMes = await _context.TenantMonthlyReportEmailLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(
                    l => l.Status == MonthlyReportEmailStatus.Failed &&
                         l.CreatedAt >= monthStart && l.CreatedAt < monthEnd,
                    cancellationToken);

            return new PlatformMonthlyReportOverview
            {
                Rows = rows,
                SchedulerEnabled = _schedulerOptions.CurrentValue.SchedulerEnabled,
                TenantsConReporteActivo = rows.Count(r => r.IsEnabled),
                EnviadosEsteMes = enviadosEsteMes,
                FallidosEsteMes = fallidosEsteMes,
                DefaultYear = previous.Year,
                DefaultMonth = previous.Month
            };
        }

        public async Task<PlatformMonthlyReportDetailViewModel?> GetTenantDetailAsync(
            Guid tenantId,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            var tenant = await _context.Tenants
                .AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.Id, t.Nombre, t.Activo })
                .FirstOrDefaultAsync(cancellationToken);

            if (tenant is null)
            {
                return null;
            }

            var settings = await _context.TenantMonthlyReportSettings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            var effectiveSettings = settings ?? new TenantMonthlyReportSettings { TenantId = tenantId };

            var logs = await _context.TenantMonthlyReportEmailLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(l => l.TenantId == tenantId)
                .OrderByDescending(l => l.CreatedAt)
                .ThenByDescending(l => l.Id)
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync(cancellationToken);

            var recipients = await _recipientResolver.ResolveAsync(tenantId, effectiveSettings, cancellationToken);

            var previous = new DateTime(_businessDateTimeProvider.Today().Year, _businessDateTimeProvider.Today().Month, 1)
                .AddMonths(-1);

            return new PlatformMonthlyReportDetailViewModel
            {
                TenantId = tenantId,
                BusinessName = string.IsNullOrWhiteSpace(tenant.Nombre) ? "(sin nombre)" : tenant.Nombre,
                TenantActivo = tenant.Activo,
                HasSettings = settings is not null,
                SchedulerEnabled = _schedulerOptions.CurrentValue.SchedulerEnabled,
                Settings = ToForm(effectiveSettings),
                Recipients = recipients,
                Logs = logs,
                LastAutomaticRunAt = effectiveSettings.LastAutomaticRunAt,
                LastAutomaticSentAt = effectiveSettings.LastAutomaticSentAt,
                LastAutomaticError = effectiveSettings.LastAutomaticError,
                DefaultYear = previous.Year,
                DefaultMonth = previous.Month
            };
        }

        public async Task<PlatformSaveSettingsResult> SaveSettingsAsync(
            Guid tenantId,
            PlatformMonthlyReportSettingsForm form,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var tenantExists = await _context.Tenants
                .AsNoTracking()
                .AnyAsync(t => t.Id == tenantId, cancellationToken);

            if (!tenantExists)
            {
                return PlatformSaveSettingsResult.NotFound;
            }

            var now = _businessDateTimeProvider.Now();

            // El guardado corre en el scope del tenant destino para que los guards de RLS
            // acepten la escritura cross-tenant sin exponer datos de otros tenants.
            await _tenantExecutionService.RunForTenantAsync(
                tenantId,
                async (serviceProvider, _, ct) =>
                {
                    var context = serviceProvider.GetRequiredService<ApplicationDbContext>();

                    var settings = await context.TenantMonthlyReportSettings
                        .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

                    if (settings is null)
                    {
                        settings = new TenantMonthlyReportSettings { TenantId = tenantId, CreatedAt = now };
                        context.TenantMonthlyReportSettings.Add(settings);
                    }

                    settings.IsEnabled = form.IsEnabled;
                    settings.SendToAllAdmins = form.SendToAllAdmins;
                    // SendToOwnerEmail se mantiene sincronizado con SendToAllAdmins (compat. Fase 1).
                    settings.SendToOwnerEmail = form.SendToAllAdmins;
                    settings.RequireConfirmedEmail = form.RequireConfirmedEmail;
                    settings.IncludeManualRecipients = form.IncludeManualRecipients;
                    settings.AdditionalRecipients = NormalizeRecipients(form.AdditionalRecipients);
                    settings.IncludeFinancialData = form.IncludeFinancialData;
                    settings.IncludeOperationalData = form.IncludeOperationalData;
                    settings.IncludeMonthOverMonth = form.IncludeMonthOverMonth;
                    settings.IncludeRecommendations = form.IncludeRecommendations;
                    settings.SendDayOfMonth = Math.Clamp(form.SendDayOfMonth, 1, 28);
                    settings.SendHour = Math.Clamp(form.SendHour, 0, 23);
                    settings.UpdatedAt = now;

                    await context.SaveChangesAsync(ct);
                },
                cancellationToken);

            return new PlatformSaveSettingsResult(true, true);
        }

        private static PlatformMonthlyReportSettingsForm ToForm(TenantMonthlyReportSettings s) => new()
        {
            IsEnabled = s.IsEnabled,
            SendToAllAdmins = s.SendToAllAdmins,
            RequireConfirmedEmail = s.RequireConfirmedEmail,
            IncludeManualRecipients = s.IncludeManualRecipients,
            AdditionalRecipients = s.AdditionalRecipients,
            IncludeFinancialData = s.IncludeFinancialData,
            IncludeOperationalData = s.IncludeOperationalData,
            IncludeMonthOverMonth = s.IncludeMonthOverMonth,
            IncludeRecommendations = s.IncludeRecommendations,
            SendDayOfMonth = s.SendDayOfMonth,
            SendHour = s.SendHour
        };

        /// <summary>Deduplica y normaliza los correos manuales; descarta los inválidos silenciosamente.</summary>
        private static string? NormalizeRecipients(string? raw)
        {
            var validos = new List<string>();
            foreach (var candidate in MonthlyBusinessReportService.ParseAdditionalRecipients(raw))
            {
                var normalized = MonthlyBusinessReportService.TryNormalizeEmail(candidate);
                if (normalized is not null && !validos.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    validos.Add(normalized);
                }
            }

            return validos.Count == 0 ? null : string.Join(",", validos);
        }

        public Task<MonthlyReportSendResult> SendTestAsync(
            Guid tenantId,
            int year,
            int month,
            string recipientEmail,
            CancellationToken cancellationToken = default) =>
            RunInTenantScopeAsync(
                tenantId,
                (service, ct) => service.SendTestAsync(tenantId, year, month, recipientEmail, MonthlyReportTriggers.Platform, ct),
                cancellationToken);

        public Task<MonthlyReportSendResult> SendRealAsync(
            Guid tenantId,
            int year,
            int month,
            CancellationToken cancellationToken = default) =>
            RunInTenantScopeAsync(
                tenantId,
                (service, ct) => service.SendMonthlyReportAsync(tenantId, year, month, MonthlyReportTriggers.Platform, ct),
                cancellationToken);

        /// <summary>
        /// Ejecuta una acción del servicio de reportes dentro del scope del tenant destino, para
        /// que el guard de "tenant actual" y el RLS operen correctamente en un contexto cross-tenant.
        /// </summary>
        private async Task<MonthlyReportSendResult> RunInTenantScopeAsync(
            Guid tenantId,
            Func<IMonthlyBusinessReportService, CancellationToken, Task<MonthlyReportSendResult>> action,
            CancellationToken cancellationToken)
        {
            MonthlyReportSendResult? captured = null;

            await _tenantExecutionService.RunForTenantAsync(
                tenantId,
                async (serviceProvider, _, ct) =>
                {
                    var service = serviceProvider.GetRequiredService<IMonthlyBusinessReportService>();
                    captured = await action(service, ct);
                },
                cancellationToken);

            return captured ?? MonthlyReportSendResult.Failed("No se pudo ejecutar la acción en el contexto del tenant.");
        }
    }
}
