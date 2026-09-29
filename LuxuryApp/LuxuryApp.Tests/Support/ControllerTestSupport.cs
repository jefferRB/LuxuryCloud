using System.Security.Claims;
using LuxuryApp.Models.Comprobantes;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Services.Comprobantes;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Informacion;
using LuxuryApp.Services.Productos;
using LuxuryApp.Services.Tenant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace LuxuryApp.Tests.Support
{
    internal static class ControllerTestSupport
    {
        public static IBusinessDateTimeProvider BusinessDateTimeProvider { get; } =
            new FixedBusinessDateTimeProvider();

        public static DefaultHttpContext AttachHttpContext(Controller controller, ClaimsPrincipal? user = null)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.User = user ?? new ClaimsPrincipal(new ClaimsIdentity());

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };

            controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
            return httpContext;
        }

        public static ClaimsPrincipal BuildTenantPrincipal(
            string userId,
            Guid tenantId,
            bool isPlatformSuperAdmin = false)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId),
                new(CustomClaimTypes.UserId, userId),
                new(CustomClaimTypes.TenantId, tenantId.ToString())
            };

            if (isPlatformSuperAdmin)
            {
                claims.Add(new Claim(CustomClaimTypes.PlatformSuperAdmin, bool.TrueString));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
        }

        public static ILiquidacionSemanalService CreateLiquidacionSemanalService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            ITenantProvider tenantProvider,
            LuxuryApp.Services.Platform.IPlatformAuditService? auditService = null) =>
            new LiquidacionSemanalService(
                context,
                BusinessDateTimeProvider,
                new LuxuryApp.Services.Fiscal.TaxCalculationService(),
                new LuxuryApp.Services.Fiscal.LiquidacionFuncionarioService(),
                new LuxuryApp.Services.Fiscal.TenantFiscalConfigService(context, tenantProvider),
                CreateSystemCategoryService(context),
                auditService ?? new FakePlatformAuditService(),
                tenantProvider,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LiquidacionSemanalService>.Instance);

        /// <summary>
        /// Bitácora REAL (escribe en la base). La reversión de pagos la necesita así: su
        /// idempotencia se apoya en que la entrada de auditoría quedó realmente persistida.
        /// </summary>
        public static LuxuryApp.Services.Platform.IPlatformAuditService CreatePlatformAuditService(
            ProyectoIdentity.Datos.ApplicationDbContext context)
        {
            var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            return new LuxuryApp.Services.Platform.PlatformAuditService(
                context,
                accessor,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LuxuryApp.Services.Platform.PlatformAuditService>.Instance);
        }

        public static ISystemCategoryService CreateSystemCategoryService(
            ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new SystemCategoryService(
                context,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SystemCategoryService>.Instance);

        /// <summary>
        /// Registro de cobros con el motor fiscal REAL: es el que congela el snapshot fiscal de
        /// cada venta nueva. Sin tenantProvider resuelve la configuración por defecto de CR.
        /// </summary>
        public static ICobroService CreateCobroService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            ITenantProvider? tenantProvider = null) =>
            new CobroService(
                context,
                BusinessDateTimeProvider,
                new LuxuryApp.Services.Fiscal.TenantFiscalConfigService(
                    context,
                    // Sin tenant explícito se usa uno cualquiera: no hay fila de Tenant que
                    // encontrar, así que la configuración fiscal cae en los valores por defecto de
                    // CR (13 %, precios con IVA incluido), que es lo que esperan esos tests.
                    tenantProvider ?? new TestTenantProvider { TenantId = Guid.NewGuid() }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CobroService>.Instance);

        /// <summary>
        /// Ingresos con el MOTOR FISCAL REAL (el mismo que Dashboard y liquidaciones): si alguien
        /// vuelve a meter una división plana entre 1,13, estos tests lo detectan.
        /// </summary>
        public static ICobroQueryService CreateCobroQueryService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            ITenantProvider tenantProvider) =>
            new CobroQueryService(
                context,
                BusinessDateTimeProvider,
                new LuxuryApp.Services.Fiscal.TenantFiscalConfigService(context, tenantProvider),
                new LuxuryApp.Services.Fiscal.TaxCalculationService());

        public static LuxuryApp.Services.Fiscal.ICobroFiscalPreviewService CreateCobroFiscalPreviewService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            ITenantProvider tenantProvider) =>
            new LuxuryApp.Services.Fiscal.CobroFiscalPreviewService(
                context,
                new LuxuryApp.Services.Fiscal.TenantFiscalConfigService(context, tenantProvider),
                new LuxuryApp.Services.Fiscal.TaxCalculationService());

        /// <summary>
        /// Dashboard con el MOTOR REAL de ganancia (nada de dobles donde hay dinero): si la
        /// fórmula cambia, estos tests lo detectan igual que los del inversionista.
        /// </summary>
        public static IDashboardFinancieroQueryService CreateDashboardFinancieroQueryService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            ITenantProvider tenantProvider) =>
            new DashboardFinancieroQueryService(
                context,
                BusinessDateTimeProvider,
                CreatePeriodProfitCalculationService(context, tenantProvider));

        public static LuxuryApp.Services.Finanzas.IPeriodProfitCalculationService CreatePeriodProfitCalculationService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            ITenantProvider tenantProvider) =>
            new LuxuryApp.Services.Finanzas.PeriodProfitCalculationService(
                context,
                CreateLiquidacionSemanalService(context, tenantProvider));

        /// <summary>
        /// KPI de participación de asociados con el motor real de ganancia: es la misma cifra que
        /// muestra el Dashboard, así que un cambio en la fórmula rompe los dos a la vez.
        /// </summary>
        public static LuxuryApp.Services.Asociados.IAssociateProfitAllocationService CreateAssociateProfitAllocationService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            ITenantProvider tenantProvider) =>
            new LuxuryApp.Services.Asociados.AssociateProfitAllocationService(
                context,
                CreatePeriodProfitCalculationService(context, tenantProvider),
                BusinessDateTimeProvider);

        public static LuxuryApp.Services.Inversionistas.IInvestorService CreateInvestorService(
            ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new LuxuryApp.Services.Inversionistas.InvestorService(
                context,
                BusinessDateTimeProvider,
                new FakePlatformAuditService(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LuxuryApp.Services.Inversionistas.InvestorService>.Instance);

        public static IEgresoService CreateEgresoService(ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new EgresoService(
                context,
                BusinessDateTimeProvider,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<EgresoService>.Instance);

        public static IEgresoQueryService CreateEgresoQueryService(ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new EgresoQueryService(context, BusinessDateTimeProvider);

        public static IInformacionNegocioQueryService CreateInformacionNegocioQueryService(ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new InformacionNegocioQueryService(context, BusinessDateTimeProvider);

        public static ICalendarCommandService CreateCalendarCommandService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            ICalendarWhatsAppNotificationService? notificationService = null,
            IAppointmentCancellationWhatsAppService? cancellationNotificationService = null,
            LuxuryApp.Services.Clientes.IClienteIdentityService? clienteIdentityService = null) =>
            new CalendarCommandService(
                context,
                notificationService ?? new NoOpCalendarWhatsAppNotificationService(),
                cancellationNotificationService ?? new NoOpAppointmentCancellationWhatsAppService(),
                new VisitasAutomaticasService(context, BusinessDateTimeProvider),
                CreateAvailabilityService(context),
                clienteIdentityService ?? CreateClienteIdentityService(context),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CalendarCommandService>.Instance);

        /// <summary>
        /// Regla única de identidad de clientes. Se construye igual que en producción para que los
        /// tests ejerciten exactamente la misma normalización de teléfonos que el sistema real.
        /// </summary>
        public static LuxuryApp.Services.Clientes.IClienteIdentityService CreateClienteIdentityService(
            ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new LuxuryApp.Services.Clientes.ClienteIdentityService(
                context,
                BusinessDateTimeProvider,
                new StaticOptionsMonitor<MetaWhatsAppOptions>(new MetaWhatsAppOptions()));

        /// <summary>
        /// Fuente única de las métricas CRM del cliente (visitas atendidas, última visita,
        /// frecuencia promedio, días sin visitar).
        /// </summary>
        public static LuxuryApp.Services.Clientes.IClienteVisitMetricsService CreateClienteVisitMetricsService(
            ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new LuxuryApp.Services.Clientes.ClienteVisitMetricsService(context, BusinessDateTimeProvider);

        /// <summary>Notas de servicio del cliente sobre <c>ClienteServicioRealizado</c>.</summary>
        public static LuxuryApp.Services.Clientes.IClienteNotasService CreateClienteNotasService(
            ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new LuxuryApp.Services.Clientes.ClienteNotasService(context, BusinessDateTimeProvider);

        /// <summary>
        /// Fuente única de disponibilidad (citas + bloqueos recurrentes). La usan el calendario y
        /// las reservas públicas: en tests se construye igual que en producción.
        /// </summary>
        public static LuxuryApp.Services.Horarios.IFuncionarioAvailabilityService CreateAvailabilityService(
            ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new LuxuryApp.Services.Horarios.FuncionarioAvailabilityService(context);

        public static LuxuryApp.Services.Reservas.IBookingAvailabilityService CreateBookingAvailabilityService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            LuxuryApp.Services.BusinessTime.IBusinessDateTimeProvider? clock = null,
            LuxuryApp.Services.Reservas.IBookingCatalogService? catalog = null) =>
            new LuxuryApp.Services.Reservas.BookingAvailabilityService(
                context,
                clock ?? BusinessDateTimeProvider,
                catalog ?? new LuxuryApp.Services.Reservas.BookingCatalogService(context),
                CreateAvailabilityService(context));

        /// <summary>
        /// Panel privado de Reservas con sus dependencias reales o dobles. Centralizado acá para
        /// que agregar una dependencia al controlador no obligue a tocar cada test.
        /// </summary>
        public static LuxuryApp.Controllers.Reservas.ReservasController CreateReservasController(
            LuxuryApp.Services.Reservas.IBookingRequestService bookingRequestService,
            LuxuryApp.Services.Reservas.IBookingSettingsService settingsService,
            LuxuryApp.Services.Reservas.IBookingCatalogService catalogService,
            LuxuryApp.Services.Horarios.IRecurringScheduleService? recurringScheduleService = null,
            string? publicBaseUrl = null) =>
            new LuxuryApp.Controllers.Reservas.ReservasController(
                bookingRequestService,
                settingsService,
                catalogService,
                new LuxuryApp.Services.Reservas.BookingQrCodeService(),
                recurringScheduleService ?? new NoRecurringScheduleService(),
                Microsoft.Extensions.Options.Options.Create(
                    new LuxuryApp.Services.Common.PublicSiteOptions
                    {
                        PublicBaseUrl = publicBaseUrl ?? string.Empty
                    }));

        /// <summary>Doble sin reglas: la configuración de reservas solo lee el listado.</summary>
        private sealed class NoRecurringScheduleService : LuxuryApp.Services.Horarios.IRecurringScheduleService
        {
            public Task<LuxuryApp.Models.Horarios.RecurringSchedulePageViewModel> BuildPageAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new LuxuryApp.Models.Horarios.RecurringSchedulePageViewModel());

            public Task<LuxuryApp.Models.Horarios.RecurringScheduleRuleFormViewModel> BuildCreateFormAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new LuxuryApp.Models.Horarios.RecurringScheduleRuleFormViewModel());

            public Task<LuxuryApp.Models.Horarios.RecurringScheduleRuleFormViewModel?> BuildEditFormAsync(
                int ruleId, CancellationToken cancellationToken = default) =>
                Task.FromResult<LuxuryApp.Models.Horarios.RecurringScheduleRuleFormViewModel?>(null);

            public Task<LuxuryApp.Models.Horarios.RecurringScheduleRuleDetailViewModel?> BuildDetailAsync(
                int ruleId, CancellationToken cancellationToken = default) =>
                Task.FromResult<LuxuryApp.Models.Horarios.RecurringScheduleRuleDetailViewModel?>(null);

            public Task<LuxuryApp.Models.Horarios.RecurringScheduleConflictSummaryViewModel> DetectConflictsAsync(
                LuxuryApp.Models.Horarios.RecurringScheduleRuleFormViewModel form,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new LuxuryApp.Models.Horarios.RecurringScheduleConflictSummaryViewModel());

            public Task<LuxuryApp.Services.Horarios.RecurringScheduleSaveResult> CreateAsync(
                LuxuryApp.Models.Horarios.RecurringScheduleRuleFormViewModel form,
                string? userId,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<LuxuryApp.Services.Horarios.RecurringScheduleSaveResult> UpdateAsync(
                int ruleId,
                LuxuryApp.Models.Horarios.RecurringScheduleRuleFormViewModel form,
                string? userId,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task SetActivaAsync(int ruleId, bool activa, string? userId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task EndAsync(int ruleId, string? userId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task AddExceptionAsync(
                LuxuryApp.Models.Horarios.RecurringScheduleExceptionFormViewModel form,
                string? userId,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task RemoveExceptionAsync(int exceptionId, string? userId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        public static ICalendarQueryService CreateCalendarQueryService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            IAppointmentCancellationWhatsAppService? cancellationNotificationService = null,
            ITenantWhatsAppFeatureService? whatsAppFeatureService = null) =>
            new CalendarQueryService(
                context,
                BusinessDateTimeProvider,
                cancellationNotificationService ?? new NoOpAppointmentCancellationWhatsAppService(),
                whatsAppFeatureService ?? new FakeTenantWhatsAppFeatureService { IsEnabled = true });

        public static IProductoService CreateProductoService(ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new ProductoService(
                context,
                BusinessDateTimeProvider,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProductoService>.Instance);

        public static IProductoQueryService CreateProductoQueryService(ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new ProductoQueryService(context);

        public static ITenantDisplayNameService CreateTenantDisplayNameService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            ITenantProvider tenantProvider) =>
            new TenantDisplayNameService(context, tenantProvider, new HttpContextAccessor());

        // No-arg overload para tests que no necesitan nombre de tenant real.
        public static ITenantDisplayNameService CreateTenantDisplayNameService() =>
            new NoOpTenantDisplayNameService();

        // Almacenamiento de fotos que no toca disco (los tests no ejercitan fotos).
        public static IFuncionarioPhotoStorageService CreateFuncionarioPhotoStorageService() =>
            new NoOpFuncionarioPhotoStorageService();

        // Overload con nombre fijo para tests que verifican que el nombre del negocio
        // aparece en encabezados/nombres de archivo de los reportes.
        public static ITenantDisplayNameService CreateTenantDisplayNameService(string displayName) =>
            new FixedTenantDisplayNameService(displayName);

        public static IControlCobrosQueryService CreateControlCobrosQueryService(
            ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new ControlCobrosQueryService(context, BusinessDateTimeProvider);

        public static IComprobanteCobroService CreateComprobanteCobroService() =>
            new NoOpComprobanteCobroService();

        public static IFuncionarioPortalAccessService CreateFuncionarioPortalAccessService() =>
            new NoOpFuncionarioPortalAccessService();

        public static IFuncionarioPortalPermissionService CreateFuncionarioPortalPermissionService() =>
            new NoOpFuncionarioPortalPermissionService();

        public static LuxuryApp.Services.Account.IAccountEmailService CreateAccountEmailService() =>
            new NoOpAccountEmailService();

        public static LuxuryApp.Services.Reports.IMonthlyReportRecipientResolver CreateMonthlyReportRecipientResolver(
            ProyectoIdentity.Datos.ApplicationDbContext context) =>
            new LuxuryApp.Services.Reports.MonthlyReportRecipientResolver(context);

        public static LuxuryApp.Services.Reports.IMonthlyBusinessReportService CreateMonthlyBusinessReportService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            ITenantProvider tenantProvider,
            LuxuryApp.Services.Reports.IMonthlyReportEmailSender emailSender,
            string businessName = "Negocio de Prueba",
            string? baseUrl = "https://app.luxurycloud.test") =>
            new LuxuryApp.Services.Reports.MonthlyBusinessReportService(
                context,
                tenantProvider,
                CreateDashboardFinancieroQueryService(context, tenantProvider),
                CreateInformacionNegocioQueryService(context),
                CreateTenantDisplayNameService(businessName),
                CreateMonthlyReportRecipientResolver(context),
                new LuxuryApp.Services.Reports.MonthlyReportEmailRenderer(),
                emailSender,
                BusinessDateTimeProvider,
                Microsoft.Extensions.Options.Options.Create(new LuxuryApp.Services.Common.PublicSiteOptions
                {
                    PublicBaseUrl = baseUrl ?? string.Empty
                }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LuxuryApp.Services.Reports.MonthlyBusinessReportService>.Instance);
    }

    internal sealed class TestTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object> _values = new();

        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>(_values);

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            _values = values.ToDictionary(pair => pair.Key, pair => pair.Value);
        }
    }

    internal sealed class NoOpTenantDisplayNameService : ITenantDisplayNameService
    {
        public Task<string> GetCurrentTenantDisplayNameAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> GetTenantDisplayNameAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string?> GetPublicTenantDisplayNameBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public string NormalizeDisplayName(string? value) => value ?? string.Empty;

        public bool ContainsInvalidDisplayNameCharacters(string? value) => false;
    }

    internal sealed class FixedTenantDisplayNameService : ITenantDisplayNameService
    {
        private readonly string _displayName;

        public FixedTenantDisplayNameService(string displayName) => _displayName = displayName;

        public Task<string> GetCurrentTenantDisplayNameAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_displayName);

        public Task<string> GetTenantDisplayNameAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_displayName);

        public Task<string?> GetPublicTenantDisplayNameBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(_displayName);

        public string NormalizeDisplayName(string? value) => value ?? string.Empty;

        public bool ContainsInvalidDisplayNameCharacters(string? value) => false;
    }

    internal sealed class NoOpComprobanteCobroService : IComprobanteCobroService
    {
        public Task<ComprobanteCobro?> CrearYEnviarDesdeCobroAsync(int cobroId, string emailDestino, bool guardarEmailEnCliente, string? createdByUserId, int? funcionarioScopeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ComprobanteCobro?>(null);

        public Task<ComprobanteCobro?> ReenviarAsync(int comprobanteId, int? funcionarioScopeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ComprobanteCobro?>(null);

        public Task<ComprobanteCobro?> ObtenerParaAppAsync(int comprobanteId, int? funcionarioScopeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ComprobanteCobro?>(null);

        public Task<ComprobanteCobro?> ObtenerPorTokenPublicoAsync(string token, CancellationToken cancellationToken = default) =>
            Task.FromResult<ComprobanteCobro?>(null);

        public byte[] GenerarPdf(ComprobanteCobro comprobante) => Array.Empty<byte>();
    }

    internal sealed class NoOpFuncionarioPortalAccessService : IFuncionarioPortalAccessService
    {
        public Task<FuncionarioAccesoViewModel> ObtenerEstadoAsync(int funcionarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FuncionarioAccesoViewModel { FuncionarioId = funcionarioId });

        public Task<FuncionarioAccesoResultado> ActivarAccesoAsync(int funcionarioId, string email, FuncionarioAccesoCredencialModo modo, string? contrasenaTemporal, CancellationToken cancellationToken = default) =>
            Task.FromResult(FuncionarioAccesoResultado.Falla("NoOp"));

        public Task<FuncionarioAccesoResultado> DesactivarAccesoAsync(int funcionarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(FuncionarioAccesoResultado.Falla("NoOp"));

        public Task<FuncionarioAccesoResultado> ReactivarAccesoAsync(int funcionarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(FuncionarioAccesoResultado.Falla("NoOp"));

        public Task<FuncionarioAccesoResultado> GenerarEnlaceInvitacionAsync(int funcionarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(FuncionarioAccesoResultado.Falla("NoOp"));

        public Task<FuncionarioAccesoResultado> CambiarCorreoAsync(int funcionarioId, string nuevoEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(FuncionarioAccesoResultado.Falla("NoOp"));
    }

    internal sealed class NoOpFuncionarioPortalPermissionService : IFuncionarioPortalPermissionService
    {
        public Task<FuncionarioPortalPermisosSet> ObtenerAsync(int funcionarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FuncionarioPortalPermisosSet(new Dictionary<string, bool>()));

        public Task<bool> TienePermisoAsync(int funcionarioId, string permiso, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task CrearDefaultsAsync(int funcionarioId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> GuardarAsync(int funcionarioId, IReadOnlyDictionary<string, bool> valores, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    internal sealed class NoOpAccountEmailService : LuxuryApp.Services.Account.IAccountEmailService
    {
        public Task SendPasswordResetEmailAsync(string toEmail, string displayName, string resetLink, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendEmailConfirmationEmailAsync(string toEmail, string displayName, string confirmationLink, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendFuncionarioInvitationEmailAsync(string toEmail, string displayName, string setPasswordLink, string businessName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendAccessInvitationEmailAsync(string toEmail, string displayName, string setPasswordLink, string businessName, string accessDescription, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    internal sealed class NoOpFuncionarioPhotoStorageService : IFuncionarioPhotoStorageService
    {
        public Task<FuncionarioPhotoSaveResult> SaveAsync(
            Guid tenantId,
            Microsoft.AspNetCore.Http.IFormFile file,
            string? previousStoragePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(FuncionarioPhotoSaveResult.Ok("/uploads/test.jpg", "uploads/test.jpg"));

        public void Delete(string? storagePath) { }
    }

    internal sealed class NoOpCalendarWhatsAppNotificationService : ICalendarWhatsAppNotificationService
    {
        public Task SendAppointmentConfirmationAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAppointmentReminderAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Resultado que devolverá <see cref="SendConfirmationNowAsync"/>. Permite recorrer la matriz
        /// complemento/consentimiento sin montar todo el motor de WhatsApp.
        /// </summary>
        public LuxuryApp.Services.Calendar.WhatsAppConfirmationSendResult? NextConfirmationResult { get; set; }

        public Task<LuxuryApp.Services.Calendar.WhatsAppConfirmationSendResult> SendConfirmationNowAsync(int citaId, string source, CancellationToken cancellationToken = default) =>
            Task.FromResult(NextConfirmationResult ?? new LuxuryApp.Services.Calendar.WhatsAppConfirmationSendResult(
                LuxuryApp.Services.Calendar.WhatsAppConfirmationOutcome.Sent,
                "Confirmación de WhatsApp enviada.",
                Reason: LuxuryApp.Services.WhatsApp.WhatsAppNotificationReason.Eligible));

        public Task QueueAppointmentConfirmationAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task QueueAppointmentReminderAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task QueueImmediateReminderOnCreateAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ProcessInboundReplyAsync(System.Text.Json.JsonElement payload, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ProcessStatusUpdateAsync(System.Text.Json.JsonElement payload, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ScheduleDueRemindersAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task GenerateDailyBatchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ProcessPendingNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RescheduleConfirmationIfPendingAsync(int citaId, DateTime newFechaHoraCita, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CancelPendingNotificationsAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Rechazar una solicitud no manda WhatsApp en los tests que no van de eso. Registra las
    /// llamadas para poder afirmar que NO se avisó.
    /// </summary>
    internal sealed class RecordingBookingRejectionWhatsAppService : LuxuryApp.Services.Reservas.IBookingRejectionWhatsAppService
    {
        public List<int> NotifiedBookingRequestIds { get; } = [];

        public LuxuryApp.Services.WhatsApp.WhatsAppNotificationReason NextReason { get; set; } =
            LuxuryApp.Services.WhatsApp.WhatsAppNotificationReason.AddonInactive;

        public Task<LuxuryApp.Services.WhatsApp.WhatsAppNotificationReason> NotifyRejectionAsync(
            int bookingRequestId,
            CancellationToken cancellationToken = default)
        {
            NotifiedBookingRequestIds.Add(bookingRequestId);
            return Task.FromResult(NextReason);
        }
    }

    /// <summary>
    /// Cancelar una cita no manda WhatsApp en los tests que no van de eso.
    /// </summary>
    internal sealed class NoOpAppointmentCancellationWhatsAppService : IAppointmentCancellationWhatsAppService
    {
        public Task<PreparedAppointmentCancellation?> PrepareAsync(
            int citaId,
            string? motivoCancelacion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PreparedAppointmentCancellation?>(null);

        public Task SendAsync(
            PreparedAppointmentCancellation prepared,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AppointmentCancellationNoticePreview> PreviewAsync(
            int citaId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AppointmentCancellationNoticePreview.NoAplica);
    }
}
