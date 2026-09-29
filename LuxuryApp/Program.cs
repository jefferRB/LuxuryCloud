using LuxuryApp.Datos;
using LuxuryApp.Emails;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Middleware;
using LuxuryApp.Services;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Comprobantes;
using LuxuryApp.Services.Contracts;
using LuxuryApp.Services.DataBase;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Informacion;
using LuxuryApp.Services.Layout;
using LuxuryApp.Services.Localization;
using LuxuryApp.Services.Notifications;
using LuxuryApp.Services.Payments;
using LuxuryApp.Services.PublicPages;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.PublicSite;
using LuxuryApp.Services.Productos;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Workers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;
using Resend;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

if (AppContext.TryGetSwitch("System.Globalization.Invariant", out var globalizationInvariant) && globalizationInvariant)
{
    throw new InvalidOperationException("La globalizacion de .NET no puede estar en modo invariant para Luxury.");
}

var defaultCulture = CultureInfo.GetCultureInfo("es-CR");
CultureInfo.DefaultThreadCurrentCulture = defaultCulture;
CultureInfo.DefaultThreadCurrentUICulture = defaultCulture;

// QuestPDF (generación de PDF del comprobante interno). Licencia Community:
// gratuita para empresas con ingresos anuales < USD 1M.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// Hosting detrás de un reverse proxy (Nginx en Linux): se toman la IP real del cliente y el
// esquema original de X-Forwarded-For / X-Forwarded-Proto. Se vacían las listas de proxies y
// redes conocidas (por defecto solo loopback), así que los encabezados se aceptan de cualquier
// origen: esto solo es seguro si Kestrel no queda expuesto directamente y todo el tráfico entra
// por el proxy. La IP resultante alimenta el rate limiting por IP.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto;

    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Data Protection: las llaves cifran/firman las cookies de autenticación. Deben vivir en
// una ruta ESTABLE fuera del directorio de publicación para que reiniciar el servicio o
// publicar una nueva versión no cierre todas las sesiones. La ruta es configurable
// (DataProtection:KeysPath / env var DataProtection__KeysPath); en producción tiene un
// valor por defecto Linux estable y en desarrollo (Windows) se conserva el almacén por
// defecto de la plataforma para no forzar rutas Linux absolutas.
var dataProtectionBuilder = builder.Services.AddDataProtection()
    .SetApplicationName(LuxuryApp.Services.Identity.AuthCookiePolicy.DataProtectionApplicationName);

var dataProtectionKeysPath = LuxuryApp.Services.Identity.AuthCookiePolicy.ResolveDataProtectionKeysPath(
    builder.Configuration["DataProtection:KeysPath"],
    builder.Environment.IsDevelopment());

if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    // Falla de forma clara y temprana si la ruta no puede crearse/usarse (permisos),
    // en lugar de arrancar con un almacén efímero que cerraría sesiones silenciosamente.
    var keysDirectory = Directory.CreateDirectory(dataProtectionKeysPath);
    dataProtectionBuilder.PersistKeysToFileSystem(keysDirectory);
}

builder.Services.AddScoped<TenantSessionConnectionInterceptor>();

builder.Services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("ConexionSql"));
    options.AddInterceptors(serviceProvider.GetRequiredService<TenantSessionConnectionInterceptor>());
});

builder.Services
    .AddIdentity<AppUsuario, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders()
    .AddPasswordValidator<SuperAdminPasswordValidator>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        PlatformAuthorizationPolicies.PlatformSuperAdmin,
        policy => policy.RequireClaim(CustomClaimTypes.PlatformSuperAdmin, bool.TrueString));

    options.AddPolicy(
        AppAuthorizationPolicies.RequireTenantAdmin,
        policy => policy.RequireRole(AppRoles.Administrador));

    options.AddPolicy(
        AppAuthorizationPolicies.RequireFuncionario,
        policy => policy.RequireRole(AppRoles.Funcionario));

    options.AddPolicy(
        AppAuthorizationPolicies.RequireAsociado,
        policy => policy.RequireRole(AppRoles.Asociado));
});

// Autorización por permisos (módulo de Asociados). El proveedor arma al vuelo una política por
// cada clave del catálogo; el handler resuelve Administrador/superadmin => acceso total y
// Asociado => permisos leídos de base de datos en cada request (cacheados por request), para que
// quitar un permiso surta efecto sin obligar a cerrar sesión.
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider,
    PermissionPolicyProvider>();
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    PermissionAuthorizationHandler>();

builder.Services.AddScoped<TenantSessionSecurityValidator>();
builder.Services.AddScoped<LegacyUserStateRepairService>();

// Reloj abstraído para probar de forma determinista el tope absoluto de sesión (90 días).
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AbsoluteSessionLifetimeEnforcer>();

builder.Services.ConfigureApplicationCookie(options =>
{
    AuthCookiePolicy.ConfigureApplicationCookie(options, builder.Environment.IsDevelopment());

    // Compone: tope absoluto 90d -> TenantSessionSecurityValidator -> SecurityStampValidator.
    // Extraído a LuxuryCookieValidation para poder ejercitar el pipeline HTTP real en pruebas.
    options.Events.OnValidatePrincipal = LuxuryCookieValidation.ValidatePrincipalAsync;
});

builder.Services.Configure<IdentityOptions>(options =>
{
    // Mínimo global 8; las cuentas de plataforma exigen 12 vía SuperAdminPasswordValidator.
    options.Password.RequiredLength = 8;
    options.Password.RequireUppercase = true;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
});

builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.Zero;
});

builder.Services.Configure<LuxuryApp.Services.Identity.PlatformSecurityOptions>(
    builder.Configuration.GetSection(LuxuryApp.Services.Identity.PlatformSecurityOptions.SectionName));

builder.Services.AddControllersWithViews(options =>
{
    options.ModelBinderProviders.Insert(0, new FlexibleDecimalModelBinderProvider());

    var policy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.Filters.Add(new Microsoft.AspNetCore.Mvc.Authorization.AuthorizeFilter(policy));

    // Enrolamiento obligatorio de TOTP para superadmins (S1). Inerte mientras
    // Security:Mfa:SuperAdminEnforcement sea false.
    options.Filters.Add<LuxuryApp.Filters.RequireMfaEnrollmentFilter>();
});

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
});

builder.Services.AddMemoryCache();

// Rate limiting acotado al enlace público de reservas (/reservar/*). Frena floods y spam
// por IP sin tocar el resto del pipeline: solo el controlador público opta con
// [EnableRateLimiting("PublicBooking")]. Detrás de nginx, UseForwardedHeaders ya deja el
// IP real del cliente en RemoteIpAddress, así que la partición por IP es correcta.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static System.Threading.RateLimiting.RateLimitPartition<string> FixedWindow(
        HttpContext httpContext,
        string policyName,
        int permitLimit,
        TimeSpan window)
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "desconocida";
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"{policyName}:{ip}",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0
            });
    }

    options.AddPolicy("PublicBooking", httpContext =>
        FixedWindow(httpContext, "public-booking", 60, TimeSpan.FromMinutes(1)));

    // Consultas de disponibilidad del enlace público (/disponibilidad y /proximos): son de solo
    // lectura, pero cada una cuesta un barrido de agenda, así que llevan su propia cuota. Se
    // particiona por IP + negocio público (slug), de modo que el tráfico de un tenant no consuma
    // la cuota de otro. Un cliente real hace unas pocas consultas por minuto; 40 no le estorba.
    options.AddPolicy("PublicBookingAvailability", httpContext =>
    {
        // El slug entra por la URL: se acota el largo para que la clave de partición no la fije
        // el atacante. Un slug inexistente igual muere en el 404 del controlador.
        var slug = httpContext.Request.RouteValues.TryGetValue("slug", out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;

        if (slug.Length > 32)
        {
            slug = slug[..32];
        }

        return FixedWindow(
            httpContext,
            $"public-booking-availability:{slug.ToLowerInvariant()}",
            40,
            TimeSpan.FromMinutes(1));
    });

    // Envío de solicitudes de reserva (/reservar/{slug}/solicitar). Necesita su propia cuota,
    // mucho más estricta que la de navegación: desde que una solicitud queda Pending OCUPA la
    // agenda, así que un POST no es una lectura barata sino una reserva de capacidad real. Se
    // particiona por IP + negocio para que el tráfico de un tenant no consuma la cuota de otro.
    // Un cliente real envía una o dos; 10 cada 10 minutos no le estorba ni desde una IP
    // compartida, y frena que una sola IP llene la agenda de solicitudes falsas.
    options.AddPolicy("PublicBookingSubmit", httpContext =>
    {
        var slug = httpContext.Request.RouteValues.TryGetValue("slug", out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;

        if (slug.Length > 32)
        {
            slug = slug[..32];
        }

        return FixedWindow(
            httpContext,
            $"public-booking-submit:{slug.ToLowerInvariant()}",
            10,
            TimeSpan.FromMinutes(10));
    });

    options.AddPolicy("Registration", httpContext =>
        FixedWindow(httpContext, "registration", 5, TimeSpan.FromMinutes(10)));

    options.AddPolicy("Authentication", httpContext =>
        FixedWindow(httpContext, "authentication", 20, TimeSpan.FromMinutes(5)));

    options.AddPolicy("PasswordReset", httpContext =>
        FixedWindow(httpContext, "password-reset", 5, TimeSpan.FromMinutes(15)));

    options.AddPolicy("Webhook", httpContext =>
        FixedWindow(httpContext, "webhook", 120, TimeSpan.FromMinutes(1)));
});

builder.Services.Configure<OpcionesPago>(builder.Configuration.GetSection("Payments"));
builder.Services.Configure<OpcionesTilopay>(builder.Configuration.GetSection("Tilopay"));
builder.Services.Configure<TilopayRepeatOptions>(builder.Configuration.GetSection("TilopayRepeat"));
builder.Services.Configure<OpcionesOnboardingTenant>(builder.Configuration.GetSection("TenantOnboarding"));
builder.Services.Configure<BusinessDateTimeOptions>(builder.Configuration.GetSection(BusinessDateTimeOptions.SectionName));
builder.Services.Configure<MetaWhatsAppOptions>(builder.Configuration.GetSection(MetaWhatsAppOptions.SectionName));
builder.Services.Configure<LuxuryApp.Services.Account.AccountEmailOptions>(
    builder.Configuration.GetSection(LuxuryApp.Services.Account.AccountEmailOptions.SectionName));
builder.Services.Configure<RegistrationSecurityOptions>(
    builder.Configuration.GetSection(RegistrationSecurityOptions.SectionName));
builder.Services.AddSingleton<RegistrationSecurityService>();
builder.Services.AddHttpClient<TurnstileVerificationService>(client =>
{
    client.BaseAddress = new Uri("https://challenges.cloudflare.com/");
    client.Timeout = TimeSpan.FromSeconds(5);
});
// URL pública oficial (clave raíz "PublicBaseUrl" / env var PublicBaseUrl). Centraliza los
// enlaces absolutos de correos/procesos de fondo; nunca usa el host del request ni ngrok.
builder.Services.Configure<LuxuryApp.Services.Common.PublicSiteOptions>(builder.Configuration);
builder.Services.AddOptions<PublicImageOptions>()
    .Bind(builder.Configuration.GetSection(PublicImageOptions.SectionName))
    .Validate(options =>
        string.Equals(options.Provider, PublicImageProviders.Local, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(options.Provider, PublicImageProviders.S3Compatible, StringComparison.OrdinalIgnoreCase),
        "PublicImages:Provider debe ser Local o S3Compatible.")
    .ValidateOnStart();
builder.Services.AddOptions<S3StorageOptions>()
    .Bind(builder.Configuration.GetSection(S3StorageOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<S3StorageOptions>, S3StorageOptionsValidator>();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[] { defaultCulture };

    options.DefaultRequestCulture = new RequestCulture(defaultCulture);
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});

builder.Services.AddSingleton<RecordatorioService>();
builder.Services.AddSingleton<IBusinessDateTimeProvider, BusinessDateTimeProvider>();
builder.Services.AddScoped<EmailSender>();
builder.Services.AddTransient<EmailService, EmailSender>();
builder.Services.AddHttpClient<ResendClient>();
builder.Services.Configure<ResendClientOptions>(options =>
{
    options.ApiToken = builder.Configuration["Email:SmtpPassword"] ?? string.Empty;
});
builder.Services.AddTransient<IResend, ResendClient>();
builder.Services.AddScoped<LuxuryApp.Services.Account.IAccountEmailService,
    LuxuryApp.Services.Account.AccountEmailService>();

// Arma "Mi cuenta". Lo usan Accounts (dueño de la pantalla) y ConfiguracionFiscal
// (para repintarla con ModelState cuando el guardado del IVA falla la validación).
builder.Services.AddScoped<LuxuryApp.Services.Account.IAccountSettingsPageBuilder,
    LuxuryApp.Services.Account.AccountSettingsPageBuilder>();

builder.Services.AddHttpClient<IMetaWhatsAppClient, MetaWhatsAppClient>((serviceProvider, client) =>
{
    var options = MetaWhatsAppNormalizedOptions.Create(
        serviceProvider.GetRequiredService<IOptionsMonitor<MetaWhatsAppOptions>>().CurrentValue);
    if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
    {
        client.BaseAddress = baseUri;
    }

    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds));
});
builder.Services.AddHostedService<MetaWhatsAppOptionsLoggingService>();
builder.Services.AddScoped<ICalendarWhatsAppNotificationService, CalendarWhatsAppNotificationService>();
builder.Services.AddScoped<IWhatsAppCancellationNotifier, WhatsAppCancellationNotifier>();
builder.Services.AddScoped<IAppointmentCancellationWhatsAppService, AppointmentCancellationWhatsAppService>();
builder.Services.AddScoped<LuxuryApp.Services.Reservas.IBookingRejectionWhatsAppService,
    LuxuryApp.Services.Reservas.BookingRejectionWhatsAppService>();
builder.Services.AddScoped<IWhatsAppInboundAutoReplyService, WhatsAppInboundAutoReplyService>();
builder.Services.AddScoped<ITenantWhatsAppSettingsService, TenantWhatsAppSettingsService>();
builder.Services.AddScoped<ITenantWhatsAppFeatureService, TenantWhatsAppFeatureService>();
builder.Services.AddScoped<IWhatsAppInboxService, WhatsAppInboxService>();
// Identidad de clientes: regla ÚNICA para decidir si un nombre+teléfono corresponde a un cliente
// ya registrado. La usan el calendario (crear/editar cita), las reservas online (confirmar) y el
// formulario de "Nueva cita". No existe una segunda búsqueda por teléfono en ningún módulo.
builder.Services.AddScoped<LuxuryApp.Services.Clientes.IClienteIdentityService,
    LuxuryApp.Services.Clientes.ClienteIdentityService>();
// Métricas CRM del cliente (visitas atendidas, última visita, frecuencia promedio, días sin
// visitar). Fuente ÚNICA: la consumirán también las campañas de recuperación y los recordatorios
// de WhatsApp, para que nadie vuelva a calcular la frecuencia por su cuenta.
builder.Services.AddScoped<LuxuryApp.Services.Clientes.IClienteVisitMetricsService,
    LuxuryApp.Services.Clientes.ClienteVisitMetricsService>();
// Notas de servicio del cliente sobre ClienteServicioRealizado (la entidad que ya existía).
builder.Services.AddScoped<LuxuryApp.Services.Clientes.IClienteNotasService,
    LuxuryApp.Services.Clientes.ClienteNotasService>();
builder.Services.AddScoped<ICalendarCommandService, CalendarCommandService>();
builder.Services.AddScoped<ICalendarQueryService, CalendarQueryService>();
builder.Services.AddScoped<IControlCobrosQueryService, ControlCobrosQueryService>();
builder.Services.AddScoped<ICobroService, CobroService>();
// Cuenta las transacciones históricas que una edición de configuración podría reinterpretar
// (cobros sin snapshot). Alimenta el aviso de confirmación de Servicios, Productos,
// Colaboradores y Configuración fiscal.
builder.Services.AddScoped<LuxuryApp.Services.Finanzas.ILegacyFinancialImpactService,
    LuxuryApp.Services.Finanzas.LegacyFinancialImpactService>();
builder.Services.AddScoped<ICobroQueryService, CobroQueryService>();
// Comprobante digital interno (no fiscal)
builder.Services.AddScoped<IComprobantePdfService, ComprobantePdfService>();
builder.Services.AddSingleton<IComprobanteHtmlRenderer, ComprobanteHtmlRenderer>();
builder.Services.AddScoped<IComprobanteEmailService, ComprobanteEmailService>();
builder.Services.AddScoped<IComprobanteCobroService, ComprobanteCobroService>();
builder.Services.AddScoped<IDashboardFinancieroQueryService, DashboardFinancieroQueryService>();
// Resumen Ejecutivo Mensual (LuxuryCloud Insights)
builder.Services.Configure<LuxuryApp.Services.Reports.MonthlyReportSchedulerOptions>(
    builder.Configuration.GetSection(LuxuryApp.Services.Reports.MonthlyReportSchedulerOptions.SectionName));
builder.Services.AddSingleton<LuxuryApp.Services.Reports.IMonthlyReportEmailRenderer, LuxuryApp.Services.Reports.MonthlyReportEmailRenderer>();
builder.Services.AddScoped<LuxuryApp.Services.Reports.IMonthlyReportEmailSender, LuxuryApp.Services.Reports.MonthlyReportEmailSender>();
builder.Services.AddScoped<LuxuryApp.Services.Reports.IMonthlyReportRecipientResolver, LuxuryApp.Services.Reports.MonthlyReportRecipientResolver>();
builder.Services.AddScoped<LuxuryApp.Services.Reports.IMonthlyBusinessReportService, LuxuryApp.Services.Reports.MonthlyBusinessReportService>();
builder.Services.AddScoped<LuxuryApp.Services.Reports.IMonthlyReportScheduler, LuxuryApp.Services.Reports.MonthlyReportScheduler>();
builder.Services.AddScoped<LuxuryApp.Services.Platform.IPlatformMonthlyReportService, LuxuryApp.Services.Platform.PlatformMonthlyReportService>();
builder.Services.AddScoped<IEgresoService, EgresoService>();
builder.Services.AddScoped<IEgresoQueryService, EgresoQueryService>();
// Identidad estructural de las categorías financieras: ninguna fórmula depende del nombre visible.
builder.Services.AddScoped<ISystemCategoryService, SystemCategoryService>();
builder.Services.AddScoped<IInformacionNegocioQueryService, InformacionNegocioQueryService>();
builder.Services.AddSingleton<LuxuryApp.Services.Fiscal.ITaxCalculationService, LuxuryApp.Services.Fiscal.TaxCalculationService>();
builder.Services.AddSingleton<LuxuryApp.Services.Fiscal.ILiquidacionFuncionarioService, LuxuryApp.Services.Fiscal.LiquidacionFuncionarioService>();
builder.Services.AddScoped<LuxuryApp.Services.Fiscal.ITenantFiscalConfigService, LuxuryApp.Services.Fiscal.TenantFiscalConfigService>();
builder.Services.AddScoped<LuxuryApp.Services.Fiscal.ICobroFiscalPreviewService, LuxuryApp.Services.Fiscal.CobroFiscalPreviewService>();
builder.Services.AddScoped<ILiquidacionSemanalService, LiquidacionSemanalService>();
// Infraestructura compartida de cuentas de acceso del tenant: la usan tanto el portal de
// funcionarios como el módulo de asociados. Una sola implementación de Identity/invitaciones.
builder.Services.AddScoped<ITenantAccountProvisioningService, TenantAccountProvisioningService>();
builder.Services.AddScoped<IFuncionarioPortalAccessService, FuncionarioPortalAccessService>();
builder.Services.AddScoped<IFuncionarioPortalQueryService, FuncionarioPortalQueryService>();
builder.Services.AddScoped<IFuncionarioPortalPermissionService, FuncionarioPortalPermissionService>();
builder.Services.AddScoped<IProductoService, ProductoService>();
builder.Services.AddScoped<IProductoQueryService, ProductoQueryService>();
builder.Services.AddScoped<IContractService, ContractService>();
builder.Services.AddScoped<IPrivateNavigationService, PrivateNavigationService>();
builder.Services.AddScoped<IPublicSiteContentService, PublicSiteContentService>();
builder.Services.AddScoped<ITenantPublicPageQueryService, TenantPublicPageQueryService>();
builder.Services.AddScoped<ITenantPublicPageSettingsService, TenantPublicPageSettingsService>();
builder.Services.AddSingleton<IBusinessScheduleService, BusinessScheduleService>();
builder.Services.AddScoped<ITenantPublicPageAnalyticsService, TenantPublicPageAnalyticsService>();
builder.Services.AddScoped<ITenantPublicPageRedirectService, TenantPublicPageRedirectService>();
builder.Services.AddScoped<IPublicUrlValidationService, PublicUrlValidationService>();
builder.Services.AddScoped<IPublicAssetQuotaService, PublicAssetQuotaService>();
builder.Services.AddSingleton<IPublicImageProfileProvider, PublicImageProfileProvider>();
builder.Services.AddScoped<IPublicImageUploadService, PublicImageUploadService>();
builder.Services.AddScoped<IUploadedFileSecurityScanner, NoOpUploadedFileSecurityScanner>();
builder.Services.AddScoped<LocalPublicImageStorageService>();
builder.Services.AddScoped<S3CompatiblePublicImageStorageService>();
builder.Services.AddScoped<IPublicImageStorageService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<PublicImageOptions>>().Value;
    return string.Equals(options.Provider, PublicImageProviders.S3Compatible, StringComparison.OrdinalIgnoreCase)
        ? serviceProvider.GetRequiredService<S3CompatiblePublicImageStorageService>()
        : serviceProvider.GetRequiredService<LocalPublicImageStorageService>();
});
builder.Services.AddScoped<ITenantDisplayNameService, TenantDisplayNameService>();
// Reservas online por tenant (Fase 1)
builder.Services.AddScoped<IBookingCatalogService, BookingCatalogService>();
builder.Services.AddScoped<IBookingAvailabilityService, BookingAvailabilityService>();
builder.Services.AddScoped<IBookingSettingsService, BookingSettingsService>();
builder.Services.AddSingleton<IBookingQrCodeService, BookingQrCodeService>();
builder.Services.AddScoped<IPublicBookingService, PublicBookingService>();
builder.Services.AddScoped<IBookingRequestService, BookingRequestService>();
builder.Services.AddScoped<IFuncionarioPhotoStorageService, FuncionarioPhotoStorageService>();

// Bloqueos recurrentes de horario. IFuncionarioAvailabilityService es la ÚNICA fuente de
// disponibilidad: la consumen el calendario (crear/editar/mover/redimensionar) y las reservas
// públicas, para que no existan dos criterios distintos de "está ocupado".
builder.Services.AddScoped<LuxuryApp.Services.Horarios.IFuncionarioAvailabilityService,
    LuxuryApp.Services.Horarios.FuncionarioAvailabilityService>();
builder.Services.AddScoped<LuxuryApp.Services.Horarios.IRecurringScheduleService,
    LuxuryApp.Services.Horarios.RecurringScheduleService>();

// Inversionistas y distribución de ganancias. El cálculo vive en InvestorProfitCalculationService
// y reutiliza el motor fiscal + liquidaciones existentes; no hay lógica financiera en controladores.
builder.Services.AddScoped<LuxuryApp.Services.Inversionistas.IInvestorService,
    LuxuryApp.Services.Inversionistas.InvestorService>();
builder.Services.AddScoped<IPeriodProfitCalculationService, PeriodProfitCalculationService>();
builder.Services.AddScoped<LuxuryApp.Services.Inversionistas.IInvestorStatementService,
    LuxuryApp.Services.Inversionistas.InvestorStatementService>();
builder.Services.AddScoped<LuxuryApp.Services.Inversionistas.IInvestorStatementDocumentService,
    LuxuryApp.Services.Inversionistas.InvestorStatementDocumentService>();
builder.Services.AddScoped<LuxuryApp.Services.Inversionistas.IInvestorStatementPdfService,
    LuxuryApp.Services.Inversionistas.InvestorStatementPdfService>();
builder.Services.AddSingleton<LuxuryApp.Services.Inversionistas.IInvestorStatementEmailRenderer,
    LuxuryApp.Services.Inversionistas.InvestorStatementEmailRenderer>();
builder.Services.AddScoped<LuxuryApp.Services.Inversionistas.IInvestorStatementEmailSender,
    LuxuryApp.Services.Inversionistas.InvestorStatementEmailSender>();
builder.Services.AddScoped<LuxuryApp.Services.Inversionistas.IInvestorStatementEmailService,
    LuxuryApp.Services.Inversionistas.InvestorStatementEmailService>();
// Lectura operacional: separa el ciclo abierto (live) del corte emitido (snapshot).
builder.Services.AddScoped<LuxuryApp.Services.Inversionistas.IInvestorCycleService,
    LuxuryApp.Services.Inversionistas.InvestorCycleService>();
// Cierre automático de cortes. Inerte hasta InvestorStatements:SchedulerEnabled=true.
builder.Services.Configure<LuxuryApp.Services.Inversionistas.InvestorStatementSchedulerOptions>(
    builder.Configuration.GetSection(
        LuxuryApp.Services.Inversionistas.InvestorStatementSchedulerOptions.SectionName));
builder.Services.AddScoped<LuxuryApp.Services.Inversionistas.IInvestorStatementScheduler,
    LuxuryApp.Services.Inversionistas.InvestorStatementScheduler>();

// Asociados del negocio. El módulo NO reimplementa nada: los porcentajes y la ganancia siguen
// viviendo en Inversionistas, y el acceso/las invitaciones en TenantAccountProvisioningService.
builder.Services.AddScoped<LuxuryApp.Services.Asociados.IAssociateService,
    LuxuryApp.Services.Asociados.AssociateService>();
builder.Services.AddScoped<LuxuryApp.Services.Asociados.IAssociateAccessService,
    LuxuryApp.Services.Asociados.AssociateAccessService>();
builder.Services.AddScoped<LuxuryApp.Services.Asociados.IAssociatePermissionService,
    LuxuryApp.Services.Asociados.AssociatePermissionService>();
builder.Services.AddScoped<LuxuryApp.Services.Asociados.IAssociateProfitAllocationService,
    LuxuryApp.Services.Asociados.AssociateProfitAllocationService>();
builder.Services.AddScoped<LuxuryApp.Services.Asociados.IPostLoginDestinationService,
    LuxuryApp.Services.Asociados.PostLoginDestinationService>();

builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddHostedService<ReminderWorker>();
builder.Services.AddScoped<VisitasAutomaticasService>();
builder.Services.AddHostedService<VisitasBackgroundService>();
// Envío automático del Resumen Ejecutivo Mensual. Inerte hasta MonthlyReports:SchedulerEnabled=true.
builder.Services.AddHostedService<LuxuryApp.Workers.MonthlyReportSchedulerService>();
// Cierre automático de estados de cuenta de inversionistas.
// Inerte hasta InvestorStatements:SchedulerEnabled=true (y requiere GeneracionAutomatica por tenant).
builder.Services.AddHostedService<LuxuryApp.Workers.InvestorStatementGenerationWorker>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
builder.Services.AddScoped<ITenantProvider, TenantProvider>();
builder.Services.AddSingleton<TenantExecutionService>();
builder.Services.AddScoped<TenantProvisioningService>();
builder.Services.AddScoped<IUserClaimsPrincipalFactory<AppUsuario>, CustomClaimsPrincipalFactory>();

builder.Services.AddScoped<SuscripcionService>();
builder.Services.AddScoped<LuxuryApp.Services.SaaS.ISubscriptionSummaryService, LuxuryApp.Services.SaaS.SubscriptionSummaryService>();
builder.Services.AddSingleton<LuxuryApp.Services.SaaS.ISubscriptionPricingCatalog, LuxuryApp.Services.SaaS.SubscriptionPricingCatalog>();
builder.Services.AddScoped<LuxuryApp.Services.SaaS.IPlanChangeService, LuxuryApp.Services.SaaS.PlanChangeService>();
builder.Services.AddScoped<LuxuryApp.Services.SaaS.IPlanChangeDecisionService, LuxuryApp.Services.SaaS.PlanChangeDecisionService>();
builder.Services.AddSingleton<ITenantCommercialAccessCache, TenantCommercialAccessCache>();
builder.Services.AddScoped<ITenantCommercialAccessResolver, TenantCommercialAccessResolver>();
// Contacto principal del tenant (admin > funcionario). Fuente unica: no debe quedar ningun
// OrderBy(email).First() resolviendo "el correo del tenant" en otro servicio.
builder.Services.AddScoped<LuxuryApp.Services.Tenant.ITenantOwnerResolver, LuxuryApp.Services.Tenant.TenantOwnerResolver>();
builder.Services.AddScoped<IPromotionalCodeService, PromotionalCodeService>();
builder.Services.AddScoped<LuxuryApp.Services.Platform.IPlatformAuditService, LuxuryApp.Services.Platform.PlatformAuditService>();
builder.Services.AddScoped<LuxuryApp.Services.Platform.IPlatformUserAdminService, LuxuryApp.Services.Platform.PlatformUserAdminService>();
builder.Services.AddScoped<LuxuryApp.Services.Platform.IPlatformMetricsService, LuxuryApp.Services.Platform.PlatformMetricsService>();
builder.Services.AddScoped<LuxuryApp.Services.Platform.IPlatformHealthService, LuxuryApp.Services.Platform.PlatformHealthService>();
builder.Services.AddScoped<LuxuryApp.Services.Platform.IPlatformWhatsAppStatusService, LuxuryApp.Services.Platform.PlatformWhatsAppStatusService>();
builder.Services.AddScoped<LuxuryApp.Services.Platform.IPlatformTenantProfileService, LuxuryApp.Services.Platform.PlatformTenantProfileService>();
// Mission Control: heartbeat singleton (crea su propio scope EF) + snapshot cacheado de señales/colas.
builder.Services.AddSingleton<LuxuryApp.Services.Platform.IWorkerHeartbeatService, LuxuryApp.Services.Platform.WorkerHeartbeatService>();
builder.Services.AddScoped<LuxuryApp.Services.Platform.IPlatformMissionControlService, LuxuryApp.Services.Platform.PlatformMissionControlService>();
// Snapshot comercial mensual (AD-4): historia de MRR/churn/trials que no se puede
// reconstruir retroactivamente. El worker queda inerte con Platform:CommercialSnapshot:Enabled=false.
builder.Services.Configure<LuxuryApp.Models.Platform.PlatformCommercialSnapshotOptions>(
    builder.Configuration.GetSection(LuxuryApp.Models.Platform.PlatformCommercialSnapshotOptions.SectionName));
builder.Services.AddScoped<LuxuryApp.Services.Platform.IPlatformCommercialSnapshotService, LuxuryApp.Services.Platform.PlatformCommercialSnapshotService>();
builder.Services.AddHostedService<LuxuryApp.Workers.CommercialSnapshotWorker>();

builder.Services.AddScoped<SaaSPaymentService>();
builder.Services.AddScoped<PaymentProviderResolver>();

// Reconciliación automática y diagnóstico del módulo Billing (red de seguridad diaria).
// El worker queda inerte con BillingReconciliation:Enabled=false.
builder.Services.Configure<LuxuryApp.Services.Billing.BillingReconciliationOptions>(
    builder.Configuration.GetSection("BillingReconciliation"));
// Recuperación de pago (pago fallido → gracia → notificación → suspensión). AutoSuspend=false
// por defecto: en producción inicial se observan incidentes/alertas sin cortar acceso.
builder.Services.Configure<LuxuryApp.Services.Billing.BillingPaymentRecoveryOptions>(
    builder.Configuration.GetSection(LuxuryApp.Services.Billing.BillingPaymentRecoveryOptions.SectionName));
builder.Services.AddScoped<LuxuryApp.Services.Billing.IBillingReconciliationService, LuxuryApp.Services.Billing.BillingReconciliationService>();
builder.Services.AddScoped<LuxuryApp.Services.Billing.IBillingHealthService, LuxuryApp.Services.Billing.BillingHealthService>();
builder.Services.AddScoped<LuxuryApp.Services.Billing.IPaymentRecoveryService, LuxuryApp.Services.Billing.PaymentRecoveryService>();
builder.Services.AddScoped<LuxuryApp.Services.Billing.IPaymentMethodUpdateService, LuxuryApp.Services.Billing.PaymentMethodUpdateService>();
builder.Services.AddScoped<IPendingTenantExpirationService, PendingTenantExpirationService>();
// Correo real de recuperación (Resend) + servicio de notificación que respeta SendEmailNotifications.
builder.Services.AddScoped<LuxuryApp.Services.Billing.IPaymentRecoveryEmailSender, LuxuryApp.Services.Billing.PaymentRecoveryEmailSender>();
builder.Services.AddScoped<LuxuryApp.Services.Billing.IPaymentRecoveryNotificationService, LuxuryApp.Services.Billing.PaymentRecoveryNotificationService>();
builder.Services.AddHostedService<LuxuryApp.Workers.BillingReconciliationWorker>();
// Worker de alta frecuencia: reintenta la cancelación del suscriptor viejo tras un cambio de plan.
builder.Services.AddHostedService<LuxuryApp.Workers.PlanChangeCancellationRetryWorker>();
// Worker liviano de ciclo de vida: cierra localmente las cancelaciones vencidas (arranque + cada N min).
builder.Services.AddHostedService<LuxuryApp.Workers.SubscriptionLifecycleWorker>();
// Worker de recuperación de pago: cierra gracias vencidas (dry-run salvo AutoSuspendAfterGrace=true).
builder.Services.AddHostedService<LuxuryApp.Workers.PaymentRecoveryWorker>();
builder.Services.AddHostedService<LuxuryApp.Workers.PendingTenantExpirationWorker>();

// Cliente admin de TiloPay Repeat: resuelve id_suscriptor y gestiona el suscriptor del proveedor.
// Deshabilitado por defecto (TilopayRepeatAdmin:Enabled=false): el flujo de compra actual no cambia.
builder.Services.Configure<OpcionesTilopayRepeatAdmin>(builder.Configuration.GetSection("TilopayRepeatAdmin"));
builder.Services.AddHttpClient<LuxuryApp.Services.Tilopay.ITilopayRepeatAdminService, LuxuryApp.Services.Tilopay.TilopayRepeatAdminService>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<OpcionesTilopay>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds));
    });
builder.Services.AddScoped<LuxuryApp.Services.Billing.ISubscriberResolutionService, LuxuryApp.Services.Billing.SubscriberResolutionService>();
builder.Services.AddScoped<LuxuryApp.Services.Billing.IProviderSubscriptionManager, LuxuryApp.Services.Billing.ProviderSubscriptionManager>();
// Cancelación SALIENTE del suscriptor del add-on de WhatsApp (Strategy B en cambio de paquete +
// cascada del plan base). Manual-safe: verifica contra TiloPay o deja pendiente + alerta.
builder.Services.AddScoped<LuxuryApp.Services.Billing.IAddonSubscriptionManager, LuxuryApp.Services.Billing.AddonSubscriptionManager>();
// Auditoría SOLO LECTURA del proveedor: cuántos add-ons de WhatsApp puede cobrarle TiloPay al
// tenant. Se dispara tras rechazar un webhook de add-on y desde la reconciliación; deja snapshot
// para que BillingHealth no muestre verde cuando el proveedor tiene doble cobro montado.
builder.Services.AddScoped<LuxuryApp.Services.Billing.IAddonProviderAuditService, LuxuryApp.Services.Billing.AddonProviderAuditService>();
// Diagnóstico de config EFECTIVA de checkout (appsettings + env vars): HasCheckoutUrl por add-on,
// enmascarado (nunca la URL/token). Log de arranque + visible en Mission Control.
builder.Services.AddSingleton<LuxuryApp.Services.Billing.IManagedPlanCheckoutInspector, LuxuryApp.Services.Billing.ManagedPlanCheckoutInspector>();
builder.Services.AddHostedService<LuxuryApp.Services.Billing.ManagedPlanCheckoutStartupLogger>();
// Aplica un cambio de plan cuyo pago ya está confirmado pero cuyo id_suscriptor nuevo llegó tarde
// (TiloPay no lo manda en el webhook). Lo usan el webhook y la reconciliación.
builder.Services.AddScoped<LuxuryApp.Services.Billing.IPlanChangeLateApplicationService, LuxuryApp.Services.Billing.PlanChangeLateApplicationService>();
// Sincroniza el expire real de TiloPay (getSuscriptorRepeat) con la vigencia local: extiende si el
// proveedor cobra más tarde, alerta si cobra más temprano. Corre en la reconciliación (diaria + rápida).
builder.Services.AddScoped<LuxuryApp.Services.Billing.IProviderExpirySyncService, LuxuryApp.Services.Billing.ProviderExpirySyncService>();
builder.Services.AddHttpClient<PublicCallbackHealthService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient<TilopayService>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<OpcionesTilopay>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds));
});
builder.Services.AddScoped<IPaymentProvider>(serviceProvider => serviceProvider.GetRequiredService<TilopayService>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            return Task.CompletedTask;
        });

        await next();
    });
}
// Debe ir lo más arriba posible para envolver autenticación, routing, controladores y EF Core.
// Traduce las cancelaciones del cliente (cambio rápido de módulo, cerrar pestaña, refrescar,
// doble click) en un 499 silencioso y evita que lleguen al UseExceptionHandler como error 500.
app.UseMiddleware<ClientDisconnectMiddleware>();

//linux nginx
app.UseForwardedHeaders();

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();
app.UseMiddleware<ContractAcceptanceMiddleware>();
app.UseMiddleware<SuscripcionMiddleware>();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    await IdentitySeeder.SeedRolesAsync(services);
    await IdentitySeeder.SeedPlatformAccessAsync(services);
    await services.GetRequiredService<LegacyUserStateRepairService>().RepairAsync();
}

app.Run();
