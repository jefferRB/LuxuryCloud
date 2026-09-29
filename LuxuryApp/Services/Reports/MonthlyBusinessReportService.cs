using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using LuxuryApp.Models.Reports;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Common;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Informacion;
using LuxuryApp.Services.Tenant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Reports
{
    public sealed class MonthlyBusinessReportService : IMonthlyBusinessReportService
    {
        private const int MinYear = 2020;
        private const int MaxYear = 2100;

        private static readonly CultureInfo SpanishCulture = CultureInfo.GetCultureInfo("es-CR");

        private readonly ApplicationDbContext _context;
        private readonly ITenantProvider _tenantProvider;
        private readonly IDashboardFinancieroQueryService _dashboardService;
        private readonly IInformacionNegocioQueryService _informacionService;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;
        private readonly IMonthlyReportRecipientResolver _recipientResolver;
        private readonly IMonthlyReportEmailRenderer _renderer;
        private readonly IMonthlyReportEmailSender _emailSender;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly PublicSiteOptions _publicSiteOptions;
        private readonly ILogger<MonthlyBusinessReportService> _logger;

        public MonthlyBusinessReportService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            IDashboardFinancieroQueryService dashboardService,
            IInformacionNegocioQueryService informacionService,
            ITenantDisplayNameService tenantDisplayNameService,
            IMonthlyReportRecipientResolver recipientResolver,
            IMonthlyReportEmailRenderer renderer,
            IMonthlyReportEmailSender emailSender,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IOptions<PublicSiteOptions> publicSiteOptions,
            ILogger<MonthlyBusinessReportService> logger)
        {
            _context = context;
            _tenantProvider = tenantProvider;
            _dashboardService = dashboardService;
            _informacionService = informacionService;
            _tenantDisplayNameService = tenantDisplayNameService;
            _recipientResolver = recipientResolver;
            _renderer = renderer;
            _emailSender = emailSender;
            _businessDateTimeProvider = businessDateTimeProvider;
            _publicSiteOptions = publicSiteOptions.Value;
            _logger = logger;
        }

        public async Task<MonthlyBusinessReportViewModel> GenerateAsync(
            Guid tenantId,
            int year,
            int month,
            CancellationToken cancellationToken = default)
        {
            EnsureCurrentTenant(tenantId);
            EnsurePeriod(year, month);

            // Reutiliza los mismos servicios que alimentan las vistas Dashboard e Información:
            // no hay consultas nuevas y los números del correo coinciden con los de pantalla.
            var dashboard = await _dashboardService.BuildViewModelAsync(month, year, cancellationToken);
            var informacion = await _informacionService.BuildViewModelAsync(month, year, top: 5, cancellationToken);
            var nombreNegocio = await _tenantDisplayNameService.GetCurrentTenantDisplayNameAsync(cancellationToken);

            var tieneActividad =
                dashboard.TotalGenerado > 0m ||
                dashboard.SalidasCajaMes > 0m ||
                informacion.CantidadServiciosMes > 0 ||
                informacion.CantidadProductosMes > 0 ||
                informacion.ReservasOnlineMes > 0;

            var report = new MonthlyBusinessReportViewModel
            {
                TenantId = tenantId,
                NombreNegocio = nombreNegocio,
                Mes = month,
                Anio = year,
                MesNombre = MonthName(month),
                FechaGeneracion = _businessDateTimeProvider.Now(),
                TieneActividad = tieneActividad,

                // Dashboard Financiero
                Ingresos = dashboard.TotalGenerado,

                // DEVENGADO: misma "Ganancia del mes" que el Dashboard, del motor único. El correo
                // y la pantalla no pueden reportar dos ganancias distintas del mismo mes.
                Egresos = dashboard.TotalEgresosAnaliticos,
                GananciaReal = dashboard.ResultadoAnalitico,
                MargenGanancia = dashboard.TotalGenerado > 0m
                    ? Math.Round(dashboard.ResultadoAnalitico / dashboard.TotalGenerado * 100m, 2, MidpointRounding.ToEven)
                    : 0m,

                // CAJA: se informa aparte, nunca como ganancia.
                SalidasCaja = dashboard.SalidasCajaMes,
                Impuestos = dashboard.TotalImpuestos,
                PagoFuncionarios = dashboard.TotalPagadoFuncionarios,
                TotalSinImpuestos = dashboard.TotalSinImpuestos,
                ServiciosGeneradosMonto = dashboard.TotalServicios,
                ProductosGeneradosMonto = dashboard.TotalProductos,
                IngresosEfectivo = dashboard.IngresosEfectivo,
                IngresosSinpe = dashboard.IngresosSinpe,
                IngresosTarjeta = dashboard.IngresosTarjeta,

                // Información del negocio
                ServiciosRealizados = informacion.CantidadServiciosMes,
                ProductosVendidos = informacion.CantidadProductosMes,
                CitasOnlineReservadas = informacion.ReservasOnlineMes,
                ServicioMasSolicitadoNombre = informacion.ServicioMasSolicitado,
                ServicioMasSolicitadoCantidad = informacion.TotalServicioMasSolicitado,
                ProductoMasVendidoNombre = informacion.ProductoMasVendido,
                ProductoMasVendidoCantidad = informacion.TotalProductoMasVendido,
                FuncionarioEstrellaNombre = informacion.FuncionarioMasCitas,
                FuncionarioEstrellaCantidadCitas = informacion.TotalFuncionarioCitas,
                DiaMasOcupado = informacion.DiaMasOcupado,
                DiaMasOcupadoCantidad = informacion.TotalDiaMasOcupado,
                DiaMenosOcupado = informacion.DiaMasLibre,
                DiaMenosOcupadoCantidad = informacion.TotalDiaMasLibre,
                HoraMasOcupada = informacion.HoraMasOcupada,
                HoraMasOcupadaCantidad = (int)informacion.PromedioHoraMasOcupada,
                HoraMenosOcupada = informacion.HoraMasLibre,
                HoraMenosOcupadaCantidad = (int)informacion.PromedioHoraMasLibre
            };

            var settings = await _context.TenantMonthlyReportSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            if (settings is not null)
            {
                report.IncluirDatosFinancieros = settings.IncludeFinancialData;
                report.IncluirDatosOperativos = settings.IncludeOperationalData;
                report.IncluirRecomendaciones = settings.IncludeRecommendations;
                report.IncluirComparativa = settings.IncludeMonthOverMonth;
            }

            if (report.IncluirComparativa)
            {
                await ApplyMonthOverMonthAsync(report, year, month, cancellationToken);
            }

            MonthlyReportInsights.Apply(report);
            return report;
        }

        /// <summary>
        /// Compara contra el mes calendario anterior reutilizando los mismos servicios de
        /// dashboard/información. No divide entre cero: si el mes anterior estaba en cero, la
        /// variación queda null y los insights lo describen como "nuevo movimiento".
        /// </summary>
        private async Task ApplyMonthOverMonthAsync(
            MonthlyBusinessReportViewModel report,
            int year,
            int month,
            CancellationToken cancellationToken)
        {
            var previous = new DateTime(year, month, 1).AddMonths(-1);
            report.MesAnteriorNombre = MonthName(previous.Month);

            var prevDashboard = await _dashboardService.BuildViewModelAsync(previous.Month, previous.Year, cancellationToken);
            var prevInformacion = await _informacionService.BuildViewModelAsync(previous.Month, previous.Year, top: 5, cancellationToken);

            report.IngresosMesAnterior = prevDashboard.TotalGenerado;
            report.EgresosMesAnterior = prevDashboard.TotalEgresosAnaliticos;
            report.GananciaRealMesAnterior = prevDashboard.ResultadoAnalitico;
            report.ServiciosRealizadosMesAnterior = prevInformacion.CantidadServiciosMes;
            report.ProductosVendidosMesAnterior = prevInformacion.CantidadProductosMes;
            report.CitasOnlineMesAnterior = prevInformacion.ReservasOnlineMes;

            report.TieneComparativa =
                prevDashboard.TotalGenerado > 0m ||
                prevDashboard.SalidasCajaMes > 0m ||
                prevInformacion.CantidadServiciosMes > 0 ||
                prevInformacion.CantidadProductosMes > 0 ||
                prevInformacion.ReservasOnlineMes > 0;

            report.VariacionIngresosPorcentaje = Variation(report.Ingresos, report.IngresosMesAnterior);
            report.VariacionGananciaPorcentaje = Variation(report.GananciaReal, report.GananciaRealMesAnterior);
            report.VariacionServiciosPorcentaje = Variation(report.ServiciosRealizados, report.ServiciosRealizadosMesAnterior);
            report.VariacionProductosPorcentaje = Variation(report.ProductosVendidos, report.ProductosVendidosMesAnterior);
            report.VariacionCitasOnlinePorcentaje = Variation(report.CitasOnlineReservadas, report.CitasOnlineMesAnterior);
        }

        /// <summary>Variación % (actual vs anterior). null si el mes anterior era cero (no comparable).</summary>
        private static decimal? Variation(decimal current, decimal previous)
        {
            if (previous == 0m)
            {
                return null;
            }

            return Math.Round((current - previous) / previous * 100m, 1, MidpointRounding.ToEven);
        }

        public Task<string> RenderEmailHtmlAsync(
            MonthlyBusinessReportViewModel report,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(report);
            return Task.FromResult(_renderer.RenderHtml(report, BuildDashboardUrl()));
        }

        public async Task<MonthlyReportSendResult> SendTestAsync(
            Guid tenantId,
            int year,
            int month,
            string recipientEmail,
            string triggeredByUserId,
            CancellationToken cancellationToken = default)
        {
            EnsureCurrentTenant(tenantId);
            EnsurePeriod(year, month);

            var normalizedEmail = TryNormalizeEmail(recipientEmail);
            if (normalizedEmail is null)
            {
                return MonthlyReportSendResult.Failed("El correo de destino no es válido.");
            }

            var report = await GenerateAsync(tenantId, year, month, cancellationToken);
            var subject = BuildSubject(report, isTest: true);

            var log = await SendAndLogAsync(
                report,
                subject,
                normalizedEmail,
                isTest: true,
                triggeredByUserId,
                // Cada prueba usa clave nueva: las pruebas SÍ pueden repetirse.
                idempotencyKey: $"mreport-test-{Guid.NewGuid():N}",
                cancellationToken);

            return log.Status == MonthlyReportEmailStatus.Sent
                ? new MonthlyReportSendResult(
                    MonthlyReportSendOutcome.Sent,
                    $"Correo de prueba enviado a {normalizedEmail}.",
                    SentCount: 1)
                : MonthlyReportSendResult.Failed(
                    $"No se pudo enviar la prueba: {log.ErrorMessage ?? "error desconocido"}.");
        }

        public async Task<MonthlyReportSendResult> SendMonthlyReportAsync(
            Guid tenantId,
            int year,
            int month,
            string triggeredByUserId,
            CancellationToken cancellationToken = default)
        {
            EnsureCurrentTenant(tenantId);
            EnsurePeriod(year, month);

            var settings = await _context.TenantMonthlyReportSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            // Regla crítica: sin configuración activa NUNCA hay envío real.
            if (settings is null || !settings.IsEnabled)
            {
                return MonthlyReportSendResult.Skipped(
                    "El resumen mensual no está activado para este negocio. Activá la configuración antes de enviar.");
            }

            var resolution = await _recipientResolver.ResolveAsync(tenantId, settings, cancellationToken);
            var recipients = resolution.IncludedEmails;
            if (recipients.Count == 0)
            {
                return MonthlyReportSendResult.Failed(
                    "No hay destinatarios válidos: no se encontró correo de administrador ni correos adicionales.");
            }

            var report = await GenerateAsync(tenantId, year, month, cancellationToken);
            var subject = BuildSubject(report, isTest: false);

            var sent = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var recipient in recipients)
            {
                // Idempotencia: un envío real ya exitoso para este tenant/mes/correo no se repite.
                var alreadySent = await _context.TenantMonthlyReportEmailLogs
                    .AsNoTracking()
                    .AnyAsync(
                        l => l.TenantId == tenantId &&
                             l.ReportYear == year &&
                             l.ReportMonth == month &&
                             l.RecipientEmail == recipient &&
                             !l.IsTest &&
                             l.Status == MonthlyReportEmailStatus.Sent,
                        cancellationToken);

                if (alreadySent)
                {
                    skipped++;
                    await RegisterSkippedAsync(report, subject, recipient, triggeredByUserId, cancellationToken);
                    continue;
                }

                var log = await SendAndLogAsync(
                    report,
                    subject,
                    recipient,
                    isTest: false,
                    triggeredByUserId,
                    idempotencyKey: BuildRealIdempotencyKey(tenantId, year, month, recipient),
                    cancellationToken);

                if (log.Status == MonthlyReportEmailStatus.Sent)
                {
                    sent++;
                }
                else
                {
                    failed++;
                }
            }

            return BuildAggregateResult(sent, skipped, failed);
        }

        // ─────────────── Envío + bitácora ───────────────

        private async Task<TenantMonthlyReportEmailLog> SendAndLogAsync(
            MonthlyBusinessReportViewModel report,
            string subject,
            string recipientEmail,
            bool isTest,
            string triggeredByUserId,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            var log = new TenantMonthlyReportEmailLog
            {
                TenantId = report.TenantId,
                ReportYear = report.Anio,
                ReportMonth = report.Mes,
                RecipientEmail = recipientEmail,
                Subject = subject,
                Status = MonthlyReportEmailStatus.Pending,
                IsTest = isTest,
                TriggeredByUserId = triggeredByUserId,
                ContentHash = ComputeContentHash(report),
                CreatedAt = _businessDateTimeProvider.Now()
            };

            _context.TenantMonthlyReportEmailLogs.Add(log);
            await _context.SaveChangesAsync(cancellationToken);

            var dashboardUrl = BuildDashboardUrl();
            var attempt = await _emailSender.SendAsync(
                recipientEmail,
                subject,
                _renderer.RenderHtml(report, dashboardUrl),
                _renderer.RenderText(report, dashboardUrl),
                idempotencyKey,
                report.TenantId,
                cancellationToken);

            if (attempt.Success)
            {
                log.Status = MonthlyReportEmailStatus.Sent;
                log.ProviderMessageId = attempt.ProviderMessageId;
                log.SentAt = _businessDateTimeProvider.Now();
            }
            else
            {
                log.Status = MonthlyReportEmailStatus.Failed;
                log.ErrorMessage = Truncate(attempt.Error, 500);
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (log.Status == MonthlyReportEmailStatus.Sent && !isTest)
            {
                // Carrera contra el índice único de envío real: otro proceso ya lo marcó Sent.
                // El correo del proveedor es idempotente por clave, así que no hay duplicado real.
                _logger.LogWarning(
                    ex,
                    "Envío real duplicado detectado por índice único (tenant {TenantId}, {Year}-{Month}, {Email}).",
                    report.TenantId,
                    report.Anio,
                    report.Mes,
                    Security.SensitiveDataMasker.MaskEmail(recipientEmail));

                _context.Entry(log).State = EntityState.Detached;
                log.Status = MonthlyReportEmailStatus.Skipped;
            }

            return log;
        }

        private async Task RegisterSkippedAsync(
            MonthlyBusinessReportViewModel report,
            string subject,
            string recipientEmail,
            string triggeredByUserId,
            CancellationToken cancellationToken)
        {
            _context.TenantMonthlyReportEmailLogs.Add(new TenantMonthlyReportEmailLog
            {
                TenantId = report.TenantId,
                ReportYear = report.Anio,
                ReportMonth = report.Mes,
                RecipientEmail = recipientEmail,
                Subject = subject,
                Status = MonthlyReportEmailStatus.Skipped,
                IsTest = false,
                TriggeredByUserId = triggeredByUserId,
                ErrorMessage = "Ya existe un envío real exitoso para este mes y destinatario.",
                ContentHash = ComputeContentHash(report),
                CreatedAt = _businessDateTimeProvider.Now()
            });

            await _context.SaveChangesAsync(cancellationToken);
        }

        // ─────────────── Helpers ───────────────

        /// <summary>Valida y normaliza un correo (trim + minúsculas). Null si no es válido.</summary>
        public static string? TryNormalizeEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            var trimmed = email.Trim();
            if (!MailAddress.TryCreate(trimmed, out var parsed) ||
                !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return trimmed.ToLowerInvariant();
        }

        /// <summary>Separa la lista de correos adicionales (coma o punto y coma).</summary>
        public static IReadOnlyList<string> ParseAdditionalRecipients(string? additionalRecipients)
        {
            if (string.IsNullOrWhiteSpace(additionalRecipients))
            {
                return Array.Empty<string>();
            }

            return additionalRecipients
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        public static string MonthName(int month)
        {
            var name = SpanishCulture.DateTimeFormat.GetMonthName(month);
            return string.IsNullOrEmpty(name)
                ? string.Empty
                : char.ToUpper(name[0], SpanishCulture) + name[1..];
        }

        private static string BuildSubject(MonthlyBusinessReportViewModel report, bool isTest)
        {
            var prefix = isTest ? "[Prueba] " : string.Empty;
            var negocio = string.IsNullOrWhiteSpace(report.NombreNegocio)
                ? "tu negocio"
                : report.NombreNegocio;

            return Truncate($"{prefix}Resumen mensual de {negocio} – {report.MesNombre} {report.Anio}", 200)!;
        }

        /// <summary>
        /// Enlace absoluto al dashboard usando la URL pública oficial (config <c>PublicBaseUrl</c>),
        /// nunca el host del request ni un túnel de desarrollo. Si no está configurada o es inválida,
        /// devuelve null y el correo omite el botón (fallback seguro documentado).
        /// </summary>
        private string? BuildDashboardUrl()
        {
            var url = _publicSiteOptions.ResolveDashboardUrl();

            if (url is null && !string.IsNullOrWhiteSpace(_publicSiteOptions.PublicBaseUrl))
            {
                _logger.LogWarning(
                    "PublicBaseUrl no es una URL pública válida ('{Value}'). El correo mensual omitirá el botón del dashboard.",
                    _publicSiteOptions.PublicBaseUrl);
            }

            return url;
        }

        private static string BuildRealIdempotencyKey(Guid tenantId, int year, int month, string recipientEmail)
        {
            // Clave estable por tenant/mes/correo: un retry de red del mismo envío real
            // no genera dos correos en Resend.
            var emailHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(recipientEmail.ToLowerInvariant())))
                .ToLowerInvariant()[..16];

            return $"mreport-{tenantId:N}-{year}{month:D2}-{emailHash}";
        }

        private static string ComputeContentHash(MonthlyBusinessReportViewModel r)
        {
            var payload = string.Join(
                '|',
                r.TenantId.ToString("N"),
                r.Anio.ToString(CultureInfo.InvariantCulture),
                r.Mes.ToString(CultureInfo.InvariantCulture),
                r.Ingresos.ToString(CultureInfo.InvariantCulture),
                r.Egresos.ToString(CultureInfo.InvariantCulture),
                r.GananciaReal.ToString(CultureInfo.InvariantCulture),
                r.MargenGanancia.ToString(CultureInfo.InvariantCulture),
                r.ServiciosRealizados.ToString(CultureInfo.InvariantCulture),
                r.ProductosVendidos.ToString(CultureInfo.InvariantCulture),
                r.CitasOnlineReservadas.ToString(CultureInfo.InvariantCulture));

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }

        private static MonthlyReportSendResult BuildAggregateResult(int sent, int skipped, int failed)
        {
            var message = $"Enviados: {sent}. Omitidos (ya enviados): {skipped}. Fallidos: {failed}.";

            if (sent > 0 && failed == 0 && skipped == 0)
            {
                return new MonthlyReportSendResult(MonthlyReportSendOutcome.Sent, message, sent, skipped, failed);
            }

            if (sent > 0)
            {
                return new MonthlyReportSendResult(MonthlyReportSendOutcome.PartiallySent, message, sent, skipped, failed);
            }

            if (failed == 0)
            {
                return new MonthlyReportSendResult(MonthlyReportSendOutcome.Skipped, message, sent, skipped, failed);
            }

            return new MonthlyReportSendResult(MonthlyReportSendOutcome.Failed, message, sent, skipped, failed);
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private void EnsureCurrentTenant(Guid tenantId)
        {
            if (tenantId == Guid.Empty)
            {
                throw new ArgumentException("El TenantId del reporte no puede estar vacío.", nameof(tenantId));
            }

            if (!_tenantProvider.HasTenant() || _tenantProvider.GetTenantId() != tenantId)
            {
                throw new InvalidOperationException(
                    "El resumen mensual solo puede generarse dentro del contexto de su tenant.");
            }
        }

        private static void EnsurePeriod(int year, int month)
        {
            if (month is < 1 or > 12)
            {
                throw new ArgumentOutOfRangeException(nameof(month), month, "El mes debe estar entre 1 y 12.");
            }

            if (year is < MinYear or > MaxYear)
            {
                throw new ArgumentOutOfRangeException(nameof(year), year, $"El año debe estar entre {MinYear} y {MaxYear}.");
            }
        }
    }
}
