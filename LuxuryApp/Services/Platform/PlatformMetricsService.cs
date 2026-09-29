using LuxuryApp.Models.Platform;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.WhatsApp;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Platform
{
    /// <summary>
    /// Metricas cross-tenant para la consola de plataforma.
    ///
    /// CONTRATO platform-safe: TODAS las queries usan <c>IgnoreQueryFilters()</c> mas un filtro
    /// EXPLICITO por los TenantId solicitados. Nunca dependen de <c>CurrentTenantId</c> (el tenant
    /// del super admin logueado), asi que abrir la ficha de un tenant devuelve los datos de ESE
    /// tenant y no los del usuario de plataforma. Desactivar el query filter aqui es intencional y
    /// es la razon por la que el filtro explicito es obligatorio en cada query: si se agrega una
    /// metrica nueva sin <c>tenantIds.Contains(...)</c>, se filtran datos de todos los tenants.
    /// </summary>
    public sealed class PlatformMetricsService : IPlatformMetricsService
    {
        private readonly ApplicationDbContext _context;

        public PlatformMetricsService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<Dictionary<Guid, PlatformTenantUsageViewModel>> GetTenantUsageBatchAsync(
            IReadOnlyList<Guid> tenantIds,
            CancellationToken cancellationToken = default)
        {
            // Guid.Empty nunca identifica un tenant real y, si se colara al filtro, produciria
            // conteos vacios que se leerian como "el tenant no tiene actividad". Fail-fast.
            if (tenantIds.Any(tenantId => tenantId == Guid.Empty))
            {
                throw new ArgumentException(
                    "Las metricas de plataforma requieren TenantId explicitos; Guid.Empty no es valido.",
                    nameof(tenantIds));
            }

            if (tenantIds.Count == 0)
                return new Dictionary<Guid, PlatformTenantUsageViewModel>();

            var nowUtc = DateTime.UtcNow;
            var cutoff30d = nowUtc.AddDays(-30);
            var cutoff7d = nowUtc.AddDays(-7);

            // Query 1: Citas en los últimos 30d, con sub-conteo de 7d inline
            var citasByTenant = await _context.Citas
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => tenantIds.Contains(c.TenantId) && c.FechaHoraCita >= cutoff30d)
                .GroupBy(c => c.TenantId)
                .Select(g => new
                {
                    TenantId = g.Key,
                    Citas30d = g.Count(),
                    Citas7d = g.Count(c => c.FechaHoraCita >= cutoff7d)
                })
                .ToDictionaryAsync(x => x.TenantId, cancellationToken);

            // Query 2: Cobros en los últimos 30d, con sub-conteo 7d y suma de monto
            var cobrosByTenant = await _context.Cobros
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => tenantIds.Contains(c.TenantId) && c.FechaCobro >= cutoff30d)
                .GroupBy(c => c.TenantId)
                .Select(g => new
                {
                    TenantId = g.Key,
                    Cobros30d = g.Count(),
                    Cobros7d = g.Count(c => c.FechaCobro >= cutoff7d),
                    Monto30d = g.Sum(c => c.Monto)
                })
                .ToDictionaryAsync(x => x.TenantId, cancellationToken);

            // Query 3: Reservas recibidas últimos 30d con desglose por estado
            var bookingsByTenant = await _context.BookingRequests
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => tenantIds.Contains(r.TenantId) && r.CreatedAtUtc >= cutoff30d)
                .GroupBy(r => r.TenantId)
                .Select(g => new
                {
                    TenantId = g.Key,
                    Total30d = g.Count(),
                    Confirmed30d = g.Count(r => r.Estado == BookingRequestStates.Confirmed),
                    Rejected30d = g.Count(r => r.Estado == BookingRequestStates.Rejected)
                })
                .ToDictionaryAsync(x => x.TenantId, cancellationToken);

            // Query 4: Reservas pendientes (sin límite de fecha, pueden ser antiguas)
            var allPendingByTenant = await _context.BookingRequests
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => tenantIds.Contains(r.TenantId) && r.Estado == BookingRequestStates.Pending)
                .GroupBy(r => r.TenantId)
                .Select(g => new { TenantId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TenantId, x => x.Count, cancellationToken);

            // Query 5: WhatsApp outbound últimos 30d
            var waByTenant = await _context.WhatsAppMessageLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(m => tenantIds.Contains(m.TenantId)
                            && m.Direction == WhatsAppMessageDirections.Outbound
                            && m.CreatedAtUtc >= cutoff30d)
                .GroupBy(m => m.TenantId)
                .Select(g => new { TenantId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TenantId, x => x.Count, cancellationToken);

            // Query 6: Última cita por tenant (proxy de actividad de agenda)
            var lastCitaByTenant = await _context.Citas
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => tenantIds.Contains(c.TenantId) && c.FechaHoraCita <= nowUtc.AddDays(1))
                .GroupBy(c => c.TenantId)
                .Select(g => new { TenantId = g.Key, Last = g.Max(c => c.FechaHoraCita) })
                .ToDictionaryAsync(x => x.TenantId, x => (DateTime?)x.Last, cancellationToken);

            // Query 7: Último cobro por tenant
            var lastCobroByTenant = await _context.Cobros
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => tenantIds.Contains(c.TenantId))
                .GroupBy(c => c.TenantId)
                .Select(g => new { TenantId = g.Key, Last = g.Max(c => c.FechaCobro) })
                .ToDictionaryAsync(x => x.TenantId, x => (DateTime?)x.Last, cancellationToken);

            // Query 8: Última reserva por tenant
            var lastBookingByTenant = await _context.BookingRequests
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => tenantIds.Contains(r.TenantId))
                .GroupBy(r => r.TenantId)
                .Select(g => new { TenantId = g.Key, Last = g.Max(r => r.CreatedAtUtc) })
                .ToDictionaryAsync(x => x.TenantId, x => (DateTime?)x.Last, cancellationToken);

            var result = new Dictionary<Guid, PlatformTenantUsageViewModel>(tenantIds.Count);
            foreach (var tenantId in tenantIds)
            {
                citasByTenant.TryGetValue(tenantId, out var citas);
                cobrosByTenant.TryGetValue(tenantId, out var cobros);
                bookingsByTenant.TryGetValue(tenantId, out var bookings);
                allPendingByTenant.TryGetValue(tenantId, out var allPending);
                waByTenant.TryGetValue(tenantId, out var waCount);
                lastCitaByTenant.TryGetValue(tenantId, out var lastCita);
                lastCobroByTenant.TryGetValue(tenantId, out var lastCobro);
                lastBookingByTenant.TryGetValue(tenantId, out var lastBooking);

                result[tenantId] = new PlatformTenantUsageViewModel
                {
                    Citas7d = citas?.Citas7d ?? 0,
                    Citas30d = citas?.Citas30d ?? 0,
                    Cobros7d = cobros?.Cobros7d ?? 0,
                    Cobros30d = cobros?.Cobros30d ?? 0,
                    MontoCobros30d = cobros?.Monto30d ?? 0,
                    BookingRequests30d = bookings?.Total30d ?? 0,
                    BookingRequestsPending = allPending,
                    BookingRequestsConfirmed30d = bookings?.Confirmed30d ?? 0,
                    BookingRequestsRejected30d = bookings?.Rejected30d ?? 0,
                    WhatsAppEnviados30d = waCount,
                    LastActivityUtc = MaxDate(lastCita, lastCobro, lastBooking)
                };
            }

            return result;
        }

        public async Task<PlatformTenantUsageViewModel> GetTenantUsageAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var batch = await GetTenantUsageBatchAsync([tenantId], cancellationToken);
            return batch.TryGetValue(tenantId, out var vm) ? vm : new PlatformTenantUsageViewModel();
        }

        private static DateTime? MaxDate(params DateTime?[] dates)
        {
            DateTime? max = null;
            foreach (var d in dates)
            {
                if (d.HasValue && (max is null || d.Value > max.Value))
                    max = d;
            }
            return max;
        }
    }
}
