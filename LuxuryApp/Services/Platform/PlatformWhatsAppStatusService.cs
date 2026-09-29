using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Platform
{
    public sealed class PlatformWhatsAppStatusService : IPlatformWhatsAppStatusService
    {
        private readonly ApplicationDbContext _context;

        public PlatformWhatsAppStatusService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<Dictionary<Guid, PlatformWhatsAppAddonState>> GetBatchStatusAsync(
            IReadOnlyList<Guid> tenantIds,
            CancellationToken cancellationToken = default)
        {
            if (tenantIds.Count == 0)
                return new Dictionary<Guid, PlatformWhatsAppAddonState>();

            var nowUtc = DateTime.UtcNow;
            var todayUtc = nowUtc.Date;
            var tomorrowUtc = todayUtc.AddDays(1);
            var cutoffErrors = nowUtc.AddDays(-30);

            // Query 1: configuración por tenant
            var allSettings = await _context.TenantWhatsAppSettings
                .IgnoreQueryFilters().AsNoTracking()
                .Where(s => tenantIds.Contains(s.TenantId))
                .Select(s => new
                {
                    s.TenantId,
                    s.IsEnabled,
                    s.SendConfirmationOnCreate,
                    s.SendReminderThreeHoursBefore,
                    s.DailyMessageLimit,
                    s.TimeZoneId,
                    s.Notes
                })
                .ToListAsync(cancellationToken);
            var settingsByTenant = allSettings.ToDictionary(s => s.TenantId);

            // Query 2: uso outbound de hoy agrupado por tenant
            var usageByTenant = await _context.WhatsAppMessageLogs
                .IgnoreQueryFilters().AsNoTracking()
                .Where(m => tenantIds.Contains(m.TenantId)
                            && m.Direction == WhatsAppMessageDirections.Outbound
                            && m.CreatedAtUtc >= todayUtc
                            && m.CreatedAtUtc < tomorrowUtc)
                .GroupBy(m => m.TenantId)
                .Select(g => new { TenantId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TenantId, x => x.Count, cancellationToken);

            // Query 3: último error outbound (30 días) por tenant
            var rawErrors = await _context.WhatsAppMessageLogs
                .IgnoreQueryFilters().AsNoTracking()
                .Where(m => tenantIds.Contains(m.TenantId)
                            && m.Direction == WhatsAppMessageDirections.Outbound
                            && m.ErrorCode != null
                            && m.CreatedAtUtc >= cutoffErrors)
                .OrderByDescending(m => m.CreatedAtUtc)
                .Select(m => new { m.TenantId, m.ErrorCode, m.ErrorMessage, m.CreatedAtUtc })
                .ToListAsync(cancellationToken);
            var lastErrorByTenant = rawErrors
                .GroupBy(m => m.TenantId)
                .ToDictionary(g => g.Key, g => g.First());

            // Query 4: addon por tenant — ordenado igual que GetActiveAddonAsync en el servicio real
            var allAddons = await _context.TenantSubscriptionAddons
                .IgnoreQueryFilters().AsNoTracking()
                .Where(a => tenantIds.Contains(a.TenantId))
                .OrderByDescending(a => a.UpdatedAtUtc)
                .ThenByDescending(a => a.CreatedAtUtc)
                .Select(a => new
                {
                    a.TenantId,
                    a.AddonCode,
                    a.Estado,
                    a.FechaFin,
                    a.FechaFinGraciaUtc,
                    a.TilopayRecurringPlanId,
                    a.ProviderSubscriptionId,
                    a.MonthlyMessageLimit,
                    a.BillingSource,
                    a.IsManualGrantIndefinite,
                    a.ManualGrantExpiresAtUtc,
                    a.RevokedAtUtc
                })
                .ToListAsync(cancellationToken);
            // Índice único por tenant, pero GroupBy+First garantiza el más reciente si hubiera duplicados
            var addonByTenant = allAddons
                .GroupBy(a => a.TenantId)
                .ToDictionary(g => g.Key, g => g.First());

            var result = new Dictionary<Guid, PlatformWhatsAppAddonState>(tenantIds.Count);
            foreach (var tenantId in tenantIds)
            {
                settingsByTenant.TryGetValue(tenantId, out var settings);
                usageByTenant.TryGetValue(tenantId, out var todayUsage);
                lastErrorByTenant.TryGetValue(tenantId, out var lastError);
                addonByTenant.TryGetValue(tenantId, out var addon);

                var entitlement = addon is null
                    ? null
                    : WhatsAppAddonEntitlementRules.Classify(
                        addon.BillingSource, addon.Estado, addon.FechaFin, addon.FechaFinGraciaUtc,
                        addon.ProviderSubscriptionId, addon.TilopayRecurringPlanId,
                        addon.IsManualGrantIndefinite, addon.ManualGrantExpiresAtUtc, addon.RevokedAtUtc,
                        addon.MonthlyMessageLimit, nowUtc);
                var addonActive = entitlement?.IsEffective == true;

                result[tenantId] = new PlatformWhatsAppAddonState
                {
                    // Cuando no hay fila de settings pero el addon está activo → habilitado por defecto
                    // (equivalente a TenantWhatsAppSettingsSnapshot.CreateEnabledDefaultsForAddon)
                    SettingsEnabled = settings is not null ? settings.IsEnabled : addonActive,
                    SendConfirmationOnCreate = settings?.SendConfirmationOnCreate ?? true,
                    SendReminderThreeHoursBefore = settings?.SendReminderThreeHoursBefore ?? true,
                    DailyMessageLimit = settings?.DailyMessageLimit ?? TenantWhatsAppSettings.DefaultDailyMessageLimit,
                    TodayUsage = todayUsage,
                    TimeZoneId = settings?.TimeZoneId ?? TenantWhatsAppSettings.DefaultTimeZoneId,
                    Notes = settings?.Notes,
                    AddonActive = addonActive,
                    AddonCode = addonActive ? addon!.AddonCode : null,
                    AddonIsManual = entitlement?.IsManualGrant == true,
                    AddonFechaFin = addonActive ? addon!.FechaFin : null,
                    AddonMonthlyLimit = addonActive ? (int?)addon!.MonthlyMessageLimit : null,
                    AddonSource = entitlement?.Source ?? WhatsAppAddonBillingSource.ProviderRecurring,
                    AddonManualIndefinite = entitlement?.IsIndefinite == true,
                    AddonManualExpiresAtUtc = entitlement?.IsManualGrant == true ? entitlement.ExpiresAtUtc : null,
                    AddonManualExpired = entitlement?.IsManualGrantExpired == true,
                    AddonProviderRisk = entitlement?.IsProviderRisk == true,
                    AddonExists = addon is not null,
                    LastErrorCode = lastError?.ErrorCode,
                    LastErrorMessage = lastError?.ErrorMessage,
                    LastErrorAtUtc = lastError?.CreatedAtUtc
                };
            }
            return result;
        }

        public async Task<PlatformWhatsAppAddonState> GetSingleStatusAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var nowUtc = DateTime.UtcNow;
            var todayUtc = nowUtc.Date;
            var cutoff30d = nowUtc.AddDays(-30);

            var settings = await _context.TenantWhatsAppSettings
                .IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.TenantId == tenantId)
                .Select(s => new
                {
                    s.IsEnabled,
                    s.SendConfirmationOnCreate,
                    s.SendReminderThreeHoursBefore,
                    s.DailyMessageLimit,
                    s.TimeZoneId,
                    s.Notes
                })
                .FirstOrDefaultAsync(cancellationToken);

            var todayUsage = await _context.WhatsAppMessageLogs
                .IgnoreQueryFilters().AsNoTracking()
                .CountAsync(m => m.TenantId == tenantId
                                 && m.Direction == WhatsAppMessageDirections.Outbound
                                 && m.CreatedAtUtc >= todayUtc, cancellationToken);

            var monthly30d = await _context.WhatsAppMessageLogs
                .IgnoreQueryFilters().AsNoTracking()
                .CountAsync(m => m.TenantId == tenantId
                                 && m.Direction == WhatsAppMessageDirections.Outbound
                                 && m.CreatedAtUtc >= cutoff30d, cancellationToken);

            var lastError = await _context.WhatsAppMessageLogs
                .IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TenantId == tenantId
                            && m.Direction == WhatsAppMessageDirections.Outbound
                            && m.ErrorCode != null
                            && m.CreatedAtUtc >= cutoff30d)
                .OrderByDescending(m => m.CreatedAtUtc)
                .Select(m => new { m.ErrorCode, m.ErrorMessage, m.CreatedAtUtc })
                .FirstOrDefaultAsync(cancellationToken);

            var lastSent = await _context.WhatsAppMessageLogs
                .IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.Direction == WhatsAppMessageDirections.Outbound)
                .OrderByDescending(m => m.CreatedAtUtc)
                .Select(m => (DateTime?)m.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var addon = await _context.TenantSubscriptionAddons
                .IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.TenantId == tenantId)
                .OrderByDescending(a => a.UpdatedAtUtc)
                .ThenByDescending(a => a.CreatedAtUtc)
                .Select(a => new
                {
                    a.AddonCode,
                    a.Estado,
                    a.FechaFin,
                    a.FechaFinGraciaUtc,
                    a.TilopayRecurringPlanId,
                    a.ProviderSubscriptionId,
                    a.MonthlyMessageLimit,
                    a.BillingSource,
                    a.IsManualGrantIndefinite,
                    a.ManualGrantExpiresAtUtc,
                    a.RevokedAtUtc
                })
                .FirstOrDefaultAsync(cancellationToken);

            var entitlement = addon is null
                ? null
                : WhatsAppAddonEntitlementRules.Classify(
                    addon.BillingSource, addon.Estado, addon.FechaFin, addon.FechaFinGraciaUtc,
                    addon.ProviderSubscriptionId, addon.TilopayRecurringPlanId,
                    addon.IsManualGrantIndefinite, addon.ManualGrantExpiresAtUtc, addon.RevokedAtUtc,
                    addon.MonthlyMessageLimit, nowUtc);
            var addonActive = entitlement?.IsEffective == true;

            return new PlatformWhatsAppAddonState
            {
                SettingsEnabled = settings is not null ? settings.IsEnabled : addonActive,
                SendConfirmationOnCreate = settings?.SendConfirmationOnCreate ?? true,
                SendReminderThreeHoursBefore = settings?.SendReminderThreeHoursBefore ?? true,
                DailyMessageLimit = settings?.DailyMessageLimit ?? TenantWhatsAppSettings.DefaultDailyMessageLimit,
                TodayUsage = todayUsage,
                MonthlyUsage30d = monthly30d,
                TimeZoneId = settings?.TimeZoneId ?? TenantWhatsAppSettings.DefaultTimeZoneId,
                Notes = settings?.Notes,
                AddonActive = addonActive,
                AddonCode = addonActive ? addon!.AddonCode : null,
                AddonIsManual = entitlement?.IsManualGrant == true,
                AddonFechaFin = addonActive ? addon!.FechaFin : null,
                AddonMonthlyLimit = addonActive ? (int?)addon!.MonthlyMessageLimit : null,
                AddonSource = entitlement?.Source ?? WhatsAppAddonBillingSource.ProviderRecurring,
                AddonManualIndefinite = entitlement?.IsIndefinite == true,
                AddonManualExpiresAtUtc = entitlement?.IsManualGrant == true ? entitlement.ExpiresAtUtc : null,
                AddonManualExpired = entitlement?.IsManualGrantExpired == true,
                AddonProviderRisk = entitlement?.IsProviderRisk == true,
                AddonExists = addon is not null,
                LastErrorCode = lastError?.ErrorCode,
                LastErrorMessage = lastError?.ErrorMessage,
                LastErrorAtUtc = lastError?.CreatedAtUtc,
                LastMessageSentUtc = lastSent
            };
        }
    }
}
