using System.Diagnostics;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.Platform.MissionControl;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Services.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Platform
{
    public sealed class PlatformMissionControlService : IPlatformMissionControlService
    {
        private const string CacheKey = "platform:mission-control:snapshot";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);

        // Umbrales de señal. Los de webhooks replican BillingHealth (48/96 h);
        // unificar en el registro de señales de la Ola 3 (arquitectura AD-3).
        private static readonly TimeSpan DbLatencyWarn = TimeSpan.FromMilliseconds(500);
        private const double DiskWarnPercent = 85;
        private const double DiskCriticalPercent = 95;
        private static readonly TimeSpan FastWorkerWarn = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FastWorkerCritical = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan WebhookFreshnessWarn = TimeSpan.FromHours(48);
        private static readonly TimeSpan WebhookFreshnessCritical = TimeSpan.FromHours(96);
        private const int WhatsAppErrorsCriticalThreshold = 10;

        private readonly ApplicationDbContext _context;
        private readonly IWorkerHeartbeatService _heartbeatService;
        private readonly IMemoryCache _cache;
        private readonly IOptionsMonitor<BillingReconciliationOptions> _reconciliationOptions;
        private readonly IOptionsMonitor<MonthlyReportSchedulerOptions> _monthlyReportOptions;
        private readonly IOptionsMonitor<InvestorStatementSchedulerOptions> _investorStatementOptions;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ILogger<PlatformMissionControlService> _logger;

        public PlatformMissionControlService(
            ApplicationDbContext context,
            IWorkerHeartbeatService heartbeatService,
            IMemoryCache cache,
            IOptionsMonitor<BillingReconciliationOptions> reconciliationOptions,
            IOptionsMonitor<MonthlyReportSchedulerOptions> monthlyReportOptions,
            IOptionsMonitor<InvestorStatementSchedulerOptions> investorStatementOptions,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ILogger<PlatformMissionControlService> logger)
        {
            _context = context;
            _heartbeatService = heartbeatService;
            _cache = cache;
            _reconciliationOptions = reconciliationOptions;
            _monthlyReportOptions = monthlyReportOptions;
            _investorStatementOptions = investorStatementOptions;
            _businessDateTimeProvider = businessDateTimeProvider;
            _logger = logger;
        }

        public async Task<MissionControlSnapshotViewModel> GetSnapshotAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (!forceRefresh &&
                _cache.TryGetValue(CacheKey, out MissionControlSnapshotViewModel? cached) &&
                cached is not null)
            {
                return cached;
            }

            var snapshot = await BuildSnapshotAsync(cancellationToken);
            _cache.Set(CacheKey, snapshot, CacheDuration);
            return snapshot;
        }

        private async Task<MissionControlSnapshotViewModel> BuildSnapshotAsync(CancellationToken ct)
        {
            var nowUtc = DateTime.UtcNow;

            // EF Core DbContext no es thread-safe: todo secuencial. Cada señal aislada
            // en try/catch para que una medición rota no tumbe el Mission Control.
            var signals = new List<MissionControlSignalViewModel>
            {
                await ComputeSafeAsync("db", "Base de datos", () => ComputeDatabaseSignalAsync(ct)),
                ComputeSafe("disk", "Disco", ComputeDiskSignal)
            };

            signals.AddRange(await ComputeSafeAsync(
                "workers", "Workers", () => ComputeWorkerSignalsAsync(nowUtc, ct)));

            // Datos de webhooks compartidos entre la señal y la cola (una sola pasada de queries).
            var webhookData = await ComputeSafeDataAsync(
                () => LoadWebhookDataAsync(nowUtc, ct), "webhooks");
            signals.Add(webhookData is null
                ? UnknownSignal("webhooks", "Webhooks Tilopay", "No fue posible medir la señal.")
                : BuildWebhookSignal(webhookData, nowUtc));

            var whatsAppData = await ComputeSafeDataAsync(
                () => LoadWhatsAppDataAsync(nowUtc, ct), "whatsapp");
            signals.Add(whatsAppData is null
                ? UnknownSignal("whatsapp", "WhatsApp Meta", "No fue posible medir la señal.")
                : BuildWhatsAppSignal(whatsAppData, nowUtc));

            var queues = await ComputeSafeDataAsync(
                () => BuildQueuesAsync(webhookData, whatsAppData, nowUtc, ct), "queues")
                ?? [];

            var pulse = await ComputeSafeDataAsync(() => BuildPulseAsync(ct), "pulse")
                ?? new MissionControlPulseViewModel();

            return new MissionControlSnapshotViewModel
            {
                GeneratedAtUtc = nowUtc,
                Signals = signals,
                Queues = queues,
                Pulse = pulse
            };
        }

        // ------------------------------------------------------------------
        // Señales
        // ------------------------------------------------------------------

        private async Task<MissionControlSignalViewModel> ComputeDatabaseSignalAsync(CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            await _context.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            stopwatch.Stop();

            var latency = stopwatch.Elapsed;
            var state = latency > DbLatencyWarn ? SignalState.Warning : SignalState.Ok;

            return new MissionControlSignalViewModel
            {
                Key = "db",
                Label = "Base de datos",
                State = state,
                Evidence = $"Latencia {latency.TotalMilliseconds:0} ms (umbral {DbLatencyWarn.TotalMilliseconds:0} ms)",
                MeasuredAtUtc = DateTime.UtcNow
            };
        }

        private MissionControlSignalViewModel ComputeDiskSignal()
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory);
            if (string.IsNullOrEmpty(root))
            {
                return UnknownSignal("disk", "Disco", "No fue posible resolver el volumen de la aplicación.");
            }

            var drive = new DriveInfo(root);
            var usedPercent = drive.TotalSize == 0
                ? 0
                : (1 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100;

            var state = usedPercent >= DiskCriticalPercent
                ? SignalState.Critical
                : usedPercent >= DiskWarnPercent
                    ? SignalState.Warning
                    : SignalState.Ok;

            var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;

            return new MissionControlSignalViewModel
            {
                Key = "disk",
                Label = "Disco",
                State = state,
                Evidence = $"{usedPercent:0}% usado · {freeGb:0.0} GB libres (umbral {DiskWarnPercent:0}%)",
                MeasuredAtUtc = DateTime.UtcNow
            };
        }

        private async Task<List<MissionControlSignalViewModel>> ComputeWorkerSignalsAsync(
            DateTime nowUtc,
            CancellationToken ct)
        {
            var heartbeats = (await _heartbeatService.GetAllAsync(ct))
                .ToDictionary(h => h.WorkerName, StringComparer.Ordinal);

            var reconciliationInterval = TimeSpan.FromHours(
                Math.Clamp(_reconciliationOptions.CurrentValue.IntervalHours, 1, 168));
            var monthlyInterval = TimeSpan.FromMinutes(
                Math.Clamp(_monthlyReportOptions.CurrentValue.PollingIntervalMinutes, 1, 720));
            var investorInterval = TimeSpan.FromMinutes(
                Math.Clamp(_investorStatementOptions.CurrentValue.PollingIntervalMinutes, 1, 720));

            return
            [
                BuildWorkerSignal(
                    heartbeats, PlatformWorkerNames.Reminder, "Worker Recordatorios",
                    enabled: true, "cada 1 min",
                    FastWorkerWarn, FastWorkerCritical, nowUtc),
                BuildWorkerSignal(
                    heartbeats, PlatformWorkerNames.Visitas, "Worker Visitas",
                    enabled: true, "cada 30 s",
                    FastWorkerWarn, FastWorkerCritical, nowUtc),
                BuildWorkerSignal(
                    heartbeats, PlatformWorkerNames.BillingReconciliation, "Worker Reconciliación",
                    _reconciliationOptions.CurrentValue.Enabled,
                    $"cada {reconciliationInterval.TotalHours:0} h",
                    reconciliationInterval + TimeSpan.FromHours(2),
                    reconciliationInterval + TimeSpan.FromHours(26),
                    nowUtc),
                BuildWorkerSignal(
                    heartbeats, PlatformWorkerNames.MonthlyReportScheduler, "Worker Resumen mensual",
                    _monthlyReportOptions.CurrentValue.SchedulerEnabled,
                    $"cada {monthlyInterval.TotalMinutes:0} min",
                    Max(TimeSpan.FromMinutes(15), monthlyInterval * 3),
                    Max(TimeSpan.FromMinutes(60), monthlyInterval * 6),
                    nowUtc),
                BuildWorkerSignal(
                    heartbeats, PlatformWorkerNames.InvestorStatementGeneration, "Worker Cortes inversionistas",
                    _investorStatementOptions.CurrentValue.SchedulerEnabled,
                    $"cada {investorInterval.TotalMinutes:0} min",
                    Max(TimeSpan.FromMinutes(15), investorInterval * 3),
                    Max(TimeSpan.FromMinutes(60), investorInterval * 6),
                    nowUtc)
            ];
        }

        private static MissionControlSignalViewModel BuildWorkerSignal(
            IReadOnlyDictionary<string, PlatformWorkerHeartbeat> heartbeats,
            string workerName,
            string label,
            bool enabled,
            string expectedCadence,
            TimeSpan warnAfter,
            TimeSpan criticalAfter,
            DateTime nowUtc)
        {
            heartbeats.TryGetValue(workerName, out var heartbeat);

            if (!enabled)
            {
                return new MissionControlSignalViewModel
                {
                    Key = $"worker:{workerName}",
                    Label = label,
                    State = SignalState.Disabled,
                    Evidence = "Deshabilitado por configuración",
                    MeasuredAtUtc = heartbeat?.LastBeatUtc
                };
            }

            if (heartbeat is null)
            {
                return new MissionControlSignalViewModel
                {
                    Key = $"worker:{workerName}",
                    Label = label,
                    State = SignalState.Warning,
                    Evidence = "Sin latido registrado (¿app recién desplegada o worker nunca inició?)",
                    MeasuredAtUtc = null
                };
            }

            var age = nowUtc - heartbeat.LastBeatUtc;
            var state = age > criticalAfter
                ? SignalState.Critical
                : age > warnAfter
                    ? SignalState.Warning
                    : SignalState.Ok;

            return new MissionControlSignalViewModel
            {
                Key = $"worker:{workerName}",
                Label = label,
                State = state,
                Evidence = $"Último latido hace {FormatAge(age)} (esperado {expectedCadence})",
                MeasuredAtUtc = heartbeat.LastBeatUtc
            };
        }

        private sealed record WebhookData(
            int UnprocessedCount,
            DateTime? OldestUnprocessedUtc,
            int ErrorsLast24h,
            DateTime? LastReceivedUtc);

        private async Task<WebhookData> LoadWebhookDataAsync(DateTime nowUtc, CancellationToken ct)
        {
            var last24h = nowUtc.AddHours(-24);

            // Mismo criterio que BillingHealthService.UnprocessedEvents (unificar en Ola 3).
            var unprocessedQuery = _context.EventosPago
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(e => !e.Procesado &&
                    (e.EstadoProcesamiento == "Recibido" || e.EstadoProcesamiento == "Error"));

            var unprocessedCount = await unprocessedQuery.CountAsync(ct);
            var oldestUnprocessed = await unprocessedQuery
                .Select(e => (DateTime?)e.FechaRecepcionUtc)
                .MinAsync(ct);

            var errors24h = await _context.EventosPago
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(e => e.EstadoProcesamiento == "Error" && e.FechaRecepcionUtc >= last24h, ct);

            var lastReceived = await _context.EventosPago
                .IgnoreQueryFilters()
                .AsNoTracking()
                .MaxAsync(e => (DateTime?)e.FechaRecepcionUtc, ct);

            return new WebhookData(unprocessedCount, oldestUnprocessed, errors24h, lastReceived);
        }

        private static MissionControlSignalViewModel BuildWebhookSignal(WebhookData data, DateTime nowUtc)
        {
            var state = SignalState.Ok;
            var parts = new List<string>();

            if (data.LastReceivedUtc is null)
            {
                parts.Add("Sin eventos registrados aún");
            }
            else
            {
                var freshness = nowUtc - data.LastReceivedUtc.Value;
                parts.Add($"Último recibido hace {FormatAge(freshness)}");

                if (freshness > WebhookFreshnessCritical)
                {
                    state = SignalState.Critical;
                }
                else if (freshness > WebhookFreshnessWarn)
                {
                    state = Worst(state, SignalState.Warning);
                }
            }

            if (data.UnprocessedCount > 0)
            {
                state = Worst(state, SignalState.Warning);
                parts.Add($"{data.UnprocessedCount} sin procesar");
            }

            if (data.ErrorsLast24h > 0)
            {
                state = Worst(state, SignalState.Warning);
                parts.Add($"{data.ErrorsLast24h} errores en 24 h");
            }

            return new MissionControlSignalViewModel
            {
                Key = "webhooks",
                Label = "Webhooks Tilopay",
                State = state,
                Evidence = string.Join(" · ", parts),
                MeasuredAtUtc = nowUtc,
                LinkUrl = "/Platform/BillingHealth"
            };
        }

        private sealed record WhatsAppData(int ErrorsLast24h, int AffectedTenants);

        private async Task<WhatsAppData> LoadWhatsAppDataAsync(DateTime nowUtc, CancellationToken ct)
        {
            var last24h = nowUtc.AddHours(-24);

            var baseQuery = _context.WhatsAppMessageLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(m => m.Direction == WhatsAppMessageDirections.Outbound
                            && m.ErrorCode != null
                            && m.CreatedAtUtc >= last24h);

            var errors = await baseQuery.CountAsync(ct);
            var tenants = errors == 0
                ? 0
                : await baseQuery.Select(m => m.TenantId).Distinct().CountAsync(ct);

            return new WhatsAppData(errors, tenants);
        }

        private static MissionControlSignalViewModel BuildWhatsAppSignal(WhatsAppData data, DateTime nowUtc)
        {
            var state = data.ErrorsLast24h >= WhatsAppErrorsCriticalThreshold
                ? SignalState.Critical
                : data.ErrorsLast24h > 0
                    ? SignalState.Warning
                    : SignalState.Ok;

            return new MissionControlSignalViewModel
            {
                Key = "whatsapp",
                Label = "WhatsApp Meta",
                State = state,
                Evidence = data.ErrorsLast24h == 0
                    ? "Sin errores outbound en 24 h"
                    : $"{data.ErrorsLast24h} errores en 24 h · {data.AffectedTenants} tenants afectados",
                MeasuredAtUtc = nowUtc,
                LinkUrl = "/Platform/Tenants"
            };
        }

        // ------------------------------------------------------------------
        // Colas de trabajo
        // ------------------------------------------------------------------

        private async Task<List<MissionControlQueueViewModel>> BuildQueuesAsync(
            WebhookData? webhookData,
            WhatsAppData? whatsAppData,
            DateTime nowUtc,
            CancellationToken ct)
        {
            var queues = new List<MissionControlQueueViewModel>();

            // Pagos en revisión manual: posible dinero cobrado sin activar.
            var manualReviewQuery = _context.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => p.Estado == EstadoPagoProveedor.ManualReview);
            queues.Add(new MissionControlQueueViewModel
            {
                Key = "manual-review",
                Label = "Pagos en revisión manual",
                Count = await manualReviewQuery.CountAsync(ct),
                OldestItemUtc = await manualReviewQuery
                    .Select(p => (DateTime?)p.FechaCreacionUtc).MinAsync(ct),
                LinkUrl = "/Platform/RecurringCheckouts",
                IsMoneyRelated = true
            });

            // Checkouts pendientes Tilopay (mismo filtro que la pantalla de conciliación).
            var pendingQuery = _context.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => p.Proveedor == PaymentProviderType.Tilopay &&
                            p.Estado == EstadoPagoProveedor.Pendiente);
            queues.Add(new MissionControlQueueViewModel
            {
                Key = "pending-checkouts",
                Label = "Checkouts pendientes",
                Count = await pendingQuery.CountAsync(ct),
                OldestItemUtc = await pendingQuery
                    .Select(p => (DateTime?)p.FechaCreacionUtc).MinAsync(ct),
                LinkUrl = "/Platform/RecurringCheckouts",
                IsMoneyRelated = true
            });

            if (webhookData is not null)
            {
                queues.Add(new MissionControlQueueViewModel
                {
                    Key = "webhooks-unprocessed",
                    Label = "Webhooks sin procesar",
                    Count = webhookData.UnprocessedCount,
                    OldestItemUtc = webhookData.OldestUnprocessedUtc,
                    LinkUrl = "/Platform/BillingHealth",
                    IsMoneyRelated = true
                });
            }

            // Renovaciones vencidas: mismo predicado que BillingHealthService (unificar en Ola 3).
            var overdueQuery = _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s =>
                    s.Proveedor == PaymentProviderType.Tilopay &&
                    s.TilopayRecurringPlanId != null &&
                    s.Estado == EstadoSuscripcion.Activa &&
                    !s.CancelAtPeriodEnd &&
                    s.FechaProximoCobroUtc != null &&
                    s.FechaProximoCobroUtc < nowUtc &&
                    // No es vencida si TiloPay va a cobrar más tarde (expire posterior en el proveedor).
                    (s.ProviderExpiresAtUtc == null || s.ProviderExpiresAtUtc < nowUtc));
            queues.Add(new MissionControlQueueViewModel
            {
                Key = "overdue-renewals",
                Label = "Renovaciones vencidas",
                Count = await overdueQuery.CountAsync(ct),
                OldestItemUtc = await overdueQuery
                    .Select(s => s.FechaProximoCobroUtc).MinAsync(ct),
                LinkUrl = "/Platform/BillingHealth",
                IsMoneyRelated = true
            });

            // Morosas por estado almacenado (la vista exacta con estado efectivo sigue en BillingHealth).
            queues.Add(new MissionControlQueueViewModel
            {
                Key = "morosas",
                Label = "Suscripciones morosas",
                Count = await _context.Suscripciones
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .CountAsync(s => s.Estado == EstadoSuscripcion.Morosa, ct),
                LinkUrl = "/Platform/BillingHealth",
                IsMoneyRelated = true
            });

            // Trials que vencen en los próximos 7 días (OldestItemUtc = el que vence primero).
            var trialCutoff = nowUtc.AddDays(7);
            var trialsQuery = _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.Estado == EstadoSuscripcion.Trial &&
                            s.FechaTrialFin != null &&
                            s.FechaTrialFin >= nowUtc &&
                            s.FechaTrialFin <= trialCutoff);
            queues.Add(new MissionControlQueueViewModel
            {
                Key = "expiring-trials",
                Label = "Trials que vencen ≤7 días",
                Count = await trialsQuery.CountAsync(ct),
                OldestItemUtc = await trialsQuery.Select(s => s.FechaTrialFin).MinAsync(ct),
                LinkUrl = "/Platform/Tenants",
                IsMoneyRelated = false
            });

            // Tenants con más de 3 reservas online sin atender.
            var pendingRegistrationQuery = _context.Tenants
                .AsNoTracking()
                .Where(t => t.CommercialAccessMode == TenantCommercialAccessMode.PendingVerification);
            queues.Add(new MissionControlQueueViewModel
            {
                Key = "pending-registrations",
                Label = "Registros pendientes / sospechosos",
                Count = await pendingRegistrationQuery.CountAsync(ct),
                OldestItemUtc = await pendingRegistrationQuery
                    .Select(t => (DateTime?)t.FechaCreacion).MinAsync(ct),
                LinkUrl = "/Platform/Tenants",
                IsMoneyRelated = false
            });

            queues.Add(new MissionControlQueueViewModel
            {
                Key = "unattended-bookings",
                Label = "Tenants con reservas desatendidas (>3)",
                Count = await _context.BookingRequests
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(r => r.Estado == BookingRequestStates.Pending)
                    .GroupBy(r => r.TenantId)
                    .Where(g => g.Count() > 3)
                    .CountAsync(ct),
                LinkUrl = "/Platform/Tenants",
                IsMoneyRelated = false
            });

            if (whatsAppData is not null)
            {
                queues.Add(new MissionControlQueueViewModel
                {
                    Key = "whatsapp-errors",
                    Label = "Tenants con errores WhatsApp (24 h)",
                    Count = whatsAppData.AffectedTenants,
                    LinkUrl = "/Platform/Tenants",
                    IsMoneyRelated = false
                });
            }

            return queues;
        }

        // ------------------------------------------------------------------
        // Pulso del día (día local del negocio, no UTC)
        // ------------------------------------------------------------------

        private async Task<MissionControlPulseViewModel> BuildPulseAsync(CancellationToken ct)
        {
            var nowLocal = _businessDateTimeProvider.NowOffset();
            var dayStartUtc = new DateTimeOffset(nowLocal.Date, nowLocal.Offset).UtcDateTime;

            var pagosHoy = await _context.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(p => p.Estado == EstadoPagoProveedor.Confirmado &&
                                 p.FechaConfirmacionUtc >= dayStartUtc, ct);

            var mensajesHoy = await _context.WhatsAppMessageLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(m => m.Direction == WhatsAppMessageDirections.Outbound &&
                                 m.CreatedAtUtc >= dayStartUtc, ct);

            var reservasHoy = await _context.BookingRequests
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(r => r.CreatedAtUtc >= dayStartUtc, ct);

            return new MissionControlPulseViewModel
            {
                PagosConfirmadosHoy = pagosHoy,
                MensajesWhatsAppHoy = mensajesHoy,
                ReservasRecibidasHoy = reservasHoy
            };
        }

        // ------------------------------------------------------------------
        // Aislamiento de señal y utilidades
        // ------------------------------------------------------------------

        private async Task<MissionControlSignalViewModel> ComputeSafeAsync(
            string key,
            string label,
            Func<Task<MissionControlSignalViewModel>> compute)
        {
            try
            {
                return await compute();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo computando la señal {SignalKey} del Mission Control.", key);
                return UnknownSignal(key, label, $"Error al medir: {ex.Message}");
            }
        }

        private MissionControlSignalViewModel ComputeSafe(
            string key,
            string label,
            Func<MissionControlSignalViewModel> compute)
        {
            try
            {
                return compute();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo computando la señal {SignalKey} del Mission Control.", key);
                return UnknownSignal(key, label, $"Error al medir: {ex.Message}");
            }
        }

        private async Task<List<MissionControlSignalViewModel>> ComputeSafeAsync(
            string key,
            string label,
            Func<Task<List<MissionControlSignalViewModel>>> compute)
        {
            try
            {
                return await compute();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo computando las señales {SignalKey} del Mission Control.", key);
                return [UnknownSignal(key, label, $"Error al medir: {ex.Message}")];
            }
        }

        private async Task<T?> ComputeSafeDataAsync<T>(Func<Task<T>> compute, string key) where T : class
        {
            try
            {
                return await compute();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo computando {SignalKey} del Mission Control.", key);
                return null;
            }
        }

        private static MissionControlSignalViewModel UnknownSignal(string key, string label, string evidence) =>
            new()
            {
                Key = key,
                Label = label,
                State = SignalState.Unknown,
                Evidence = evidence,
                MeasuredAtUtc = DateTime.UtcNow
            };

        private static SignalState Worst(SignalState a, SignalState b) => a >= b ? a : b;

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

        private static string FormatAge(TimeSpan age)
        {
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }

            if (age.TotalMinutes < 1)
            {
                return $"{age.TotalSeconds:0} s";
            }

            if (age.TotalHours < 1)
            {
                return $"{age.TotalMinutes:0} min";
            }

            if (age.TotalDays < 1)
            {
                return $"{age.TotalHours:0.#} h";
            }

            return $"{age.TotalDays:0.#} días";
        }
    }
}
