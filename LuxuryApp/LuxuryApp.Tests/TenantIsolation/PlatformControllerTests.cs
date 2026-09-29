using System.Security.Claims;
using System.Globalization;
using System.Text.Json;
using LuxuryApp.Controllers.Platform;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PlatformControllerTests
    {
        [Fact]
        public void CreatePromotionalCode_ShouldBindUsingCreateFormPrefix()
        {
            var action = typeof(PlatformController).GetMethod(nameof(PlatformController.CreatePromotionalCode));

            Assert.NotNull(action);

            var formParameter = action!.GetParameters()
                .Single(parameter => parameter.ParameterType == typeof(PlatformPromotionalCodeCreateViewModel));

            var bindAttribute = formParameter
                .GetCustomAttributes(typeof(BindAttribute), inherit: false)
                .OfType<BindAttribute>()
                .SingleOrDefault();

            Assert.NotNull(bindAttribute);
            Assert.Equal(nameof(PlatformPromotionalCodesPageViewModel.CreateForm), bindAttribute!.Prefix);
        }

        [Fact]
        public void UpdateTenantWhatsAppSettings_ShouldBindUsingWhatsAppSettingsPrefix()
        {
            var action = typeof(PlatformController).GetMethod(nameof(PlatformController.UpdateTenantWhatsAppSettings));

            Assert.NotNull(action);

            var formParameter = action!.GetParameters()
                .Single(parameter => parameter.ParameterType == typeof(TenantWhatsAppSettingsUpdateDto));

            var bindAttribute = formParameter
                .GetCustomAttributes(typeof(BindAttribute), inherit: false)
                .OfType<BindAttribute>()
                .SingleOrDefault();

            Assert.NotNull(bindAttribute);
            Assert.Equal("whatsappSettings", bindAttribute!.Prefix);
        }

        [Fact]
        public async Task BooleanCheckboxBinding_WhenHiddenFalseComesBeforeCheckedValue_ShouldBindFalse()
        {
            var boundValue = await BindBooleanFromFormAsync("false", "true");

            Assert.False(boundValue);
        }

        [Fact]
        public async Task BooleanCheckboxBinding_WhenCheckedValueComesBeforeHiddenFalse_ShouldBindTrue()
        {
            var boundValue = await BindBooleanFromFormAsync("true", "false");

            Assert.True(boundValue);
        }

        [Fact]
        public async Task UpdateTenantWhatsAppSettings_ShouldPersistConfigurationInsideTargetTenantScope()
        {
            var tenantId = Guid.NewGuid();
            using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            using var provider = BuildWhatsAppPlatformServiceProvider(connection);
            using var scope = provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ProyectoIdentity.Datos.ApplicationDbContext>();
            await context.Database.EnsureCreatedAsync();
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant WhatsApp" });
            await context.SaveChangesAsync();

            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            var controller = new PlatformController(
                context,
                CreateResolver(context, cache, accessCache),
                accessCache,
                provider.GetRequiredService<TenantExecutionService>(),
                new StubMetaWhatsAppClient(),
                new NullPlatformAuditService(),
                new NullPlatformMetricsService(),
                new NullPlatformHealthService(),
                new NullPlatformWhatsAppStatusService(),
                new NullPlatformMissionControlService(),
                new FakeTenantOwnerResolver());
            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("platform-user", Guid.NewGuid(), isPlatformSuperAdmin: true));

            var result = await controller.UpdateTenantWhatsAppSettings(
                tenantId,
                new TenantWhatsAppSettingsUpdateDto
                {
                    IsEnabled = true,
                    SendConfirmationOnCreate = true,
                    SendReminderThreeHoursBefore = false,
                    DailyMessageLimit = 45,
                    Notes = "Cliente premium"
                },
                CancellationToken.None);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            // Tras el rename de la Ola Mission Control, guardar configuración devuelve a la tabla de tenants.
            Assert.Equal(nameof(PlatformController.Tenants), redirect.ActionName);

            var settings = await context.TenantWhatsAppSettings.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.True(settings.IsEnabled);
            Assert.True(settings.SendConfirmationOnCreate);
            Assert.False(settings.SendReminderThreeHoursBefore);
            Assert.Equal(45, settings.DailyMessageLimit);
            Assert.Equal("platform-user", settings.UpdatedByUserId);
        }

        [Fact]
        public async Task TestMetaWhatsAppConfiguration_ShouldReturnJsonWithTenantSettings()
        {
            var tenantId = Guid.NewGuid();
            using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            using var provider = BuildWhatsAppPlatformServiceProvider(connection);
            using var scope = provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ProyectoIdentity.Datos.ApplicationDbContext>();
            await context.Database.EnsureCreatedAsync();
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Diagnostico" });
            await context.SaveChangesAsync();

            context.TenantWhatsAppSettings.Add(new TenantWhatsAppSettings
            {
                TenantId = tenantId,
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                TimeZoneId = "America/Costa_Rica",
                UpdatedByUserId = "platform-user"
            });
            await context.SaveChangesAsync();

            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            var controller = new PlatformController(
                context,
                CreateResolver(context, cache, accessCache),
                accessCache,
                provider.GetRequiredService<TenantExecutionService>(),
                new StubMetaWhatsAppClient(),
                new NullPlatformAuditService(),
                new NullPlatformMetricsService(),
                new NullPlatformHealthService(),
                new NullPlatformWhatsAppStatusService(),
                new NullPlatformMissionControlService(),
                new FakeTenantOwnerResolver());
            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("platform-user", Guid.NewGuid(), isPlatformSuperAdmin: true));

            var result = await controller.TestMetaWhatsAppConfiguration(tenantId, CancellationToken.None);

            var json = Assert.IsType<JsonResult>(result);
            var payload = JsonSerializer.Serialize(json.Value);
            Assert.Contains("\"success\":true", payload);
            Assert.Contains(tenantId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"isEnabled\":true", payload, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"phoneNumberBelongsToConfiguredWaba\":true", payload, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CreatePromotionalCode_ShouldPersistCodeAndRedirect()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var planId = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Full",
                PrecioMensual = 99,
                Moneda = "CRC",
                Activo = true
            });

            await context.SaveChangesAsync();

            var controller = CreateController(context, "platform-user-1");

            var result = await controller.CreatePromotionalCode(
                new PlatformPromotionalCodeCreateViewModel
                {
                    Codigo = " vip30 ",
                    PlanId = planId,
                    DiasGratis = 30,
                    MaxUsos = 1,
                    Activo = true
                },
                CancellationToken.None);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(PlatformController.PromotionalCodes), redirect.ActionName);

            var persistedCode = Assert.Single(context.PromotionalCodes);
            Assert.Equal("VIP30", persistedCode.Codigo);
            Assert.Equal(planId, persistedCode.PlanId);
            Assert.Equal("platform-user-1", persistedCode.CreadoPorUserId);
        }

        [Fact]
        public async Task CreatePromotionalCode_WhenDuplicateCodeExists_ShouldAttachFieldErrorToCreateFormPrefix()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var planId = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Full",
                PrecioMensual = 99,
                Moneda = "CRC",
                Activo = true
            });

            context.PromotionalCodes.Add(new PromotionalCode
            {
                Id = Guid.NewGuid(),
                Codigo = "VIP30",
                Activo = true,
                DiasGratis = 30,
                PlanId = planId,
                FechaCreacionUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var controller = CreateController(context, "platform-user-2");

            var result = await controller.CreatePromotionalCode(
                new PlatformPromotionalCodeCreateViewModel
                {
                    Codigo = "VIP30",
                    PlanId = planId,
                    DiasGratis = 30,
                    MaxUsos = 1,
                    Activo = true
                },
                CancellationToken.None);

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("PromotionalCodes", view.ViewName);
            Assert.False(controller.ModelState.IsValid);
            Assert.Contains($"{nameof(PlatformPromotionalCodesPageViewModel.CreateForm)}.{nameof(PlatformPromotionalCodeCreateViewModel.Codigo)}", controller.ModelState.Keys);
        }

        // ── Guardado del plan base forzado (validacion server-side, no solo la UI) ──

        [Theory]
        [InlineData(PlanCodes.WhatsApp400)]
        [InlineData(PlanCodes.WhatsApp800)]
        [InlineData(PlanCodes.WhatsApp1200)]
        public async Task UpdateTenantCommercialSettings_RechazaAddonWhatsAppComoPlanBase(string addonCode)
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var _c = context;
            using var _n = connection;

            var tenantId = Guid.NewGuid();
            var addonPlanId = Guid.NewGuid();

            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant", Activo = true });
            context.Planes.Add(new Plan
            {
                Id = addonPlanId,
                Codigo = addonCode,
                Nombre = $"WhatsApp {addonCode}",
                Moneda = "CRC",
                PrecioMensual = 6_000m,
                LimiteMensajesMensual = 400,
                Activo = true
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, "platform-user-3");

            var result = await controller.UpdateTenantCommercialSettings(
                tenantId,
                TenantCommercialAccessMode.Exempt,
                addonPlanId,
                commercialNotes: null,
                CancellationToken.None);

            Assert.IsType<RedirectToActionResult>(result);
            var error = Assert.IsType<string>(controller.TempData["PlatformError"]);
            Assert.Contains("no puede usarse como plan base forzado", error);

            // Nada se persistio: el tenant sigue como estaba.
            var reloaded = await context.Tenants.FindAsync(tenantId);
            Assert.Equal(TenantCommercialAccessMode.RequiresSubscription, reloaded!.CommercialAccessMode);
            Assert.Null(reloaded.ForcedPlanId);
        }

        [Fact]
        public async Task UpdateTenantCommercialSettings_AceptaPlanBaseComercial()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var _c = context;
            using var _n = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Northside Studio", Activo = true });
            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = "LC_M_05",
                Nombre = "LuxuryCloud Mensual 5 funcionarios",
                Moneda = "CRC",
                PrecioMensual = 50_000m,
                MaxFuncionarios = 5,
                Activo = true
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, "platform-user-4");

            var result = await controller.UpdateTenantCommercialSettings(
                tenantId,
                TenantCommercialAccessMode.Exempt,
                planId,
                commercialNotes: "Canje",
                CancellationToken.None);

            Assert.IsType<RedirectToActionResult>(result);
            Assert.Null(controller.TempData["PlatformError"]);

            var reloaded = await context.Tenants.FindAsync(tenantId);
            Assert.Equal(TenantCommercialAccessMode.Exempt, reloaded!.CommercialAccessMode);
            Assert.Equal(planId, reloaded.ForcedPlanId);
        }

        [Fact]
        public async Task UpdateTenantCommercialSettings_ElSelectorLegacyGanaSobreElComercial()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var _c = context;
            using var _n = connection;

            var tenantId = Guid.NewGuid();
            var comercialPlanId = Guid.NewGuid();
            var legacyPlanId = Guid.NewGuid();

            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Legacy", Activo = true });
            context.Planes.Add(new Plan
            {
                Id = comercialPlanId, Codigo = "LC_M_02", Nombre = "Mensual 2",
                Moneda = "CRC", PrecioMensual = 24_000m, MaxFuncionarios = 2, Activo = true
            });
            context.Planes.Add(new Plan
            {
                Id = legacyPlanId, Codigo = PlanCodes.Pro, Nombre = "Pro",
                Moneda = "CRC", PrecioMensual = 35_000m, MaxFuncionarios = 7, Activo = true
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, "platform-user-5");

            var result = await controller.UpdateTenantCommercialSettings(
                tenantId,
                TenantCommercialAccessMode.Exempt,
                comercialPlanId,
                commercialNotes: null,
                CancellationToken.None,
                legacyForcedPlanId: legacyPlanId);

            Assert.IsType<RedirectToActionResult>(result);
            var reloaded = await context.Tenants.FindAsync(tenantId);
            Assert.Equal(legacyPlanId, reloaded!.ForcedPlanId);

            // La eleccion legacy se avisa explicitamente en lugar de pasar en silencio.
            var success = Assert.IsType<string>(controller.TempData["PlatformSuccess"]);
            Assert.Contains("legacy", success, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task UpdateTenantCommercialSettings_NoPermiteMoverAPendienteDeVerificacion()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var _c = context;
            using var _n = connection;

            var tenantId = Guid.NewGuid();
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Ya verificado",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.RequiresSubscription
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, "platform-user-6");

            var result = await controller.UpdateTenantCommercialSettings(
                tenantId,
                TenantCommercialAccessMode.PendingVerification,
                forcedPlanId: null,
                commercialNotes: null,
                CancellationToken.None);

            Assert.IsType<RedirectToActionResult>(result);
            Assert.NotNull(controller.TempData["PlatformError"]);

            var reloaded = await context.Tenants.FindAsync(tenantId);
            Assert.Equal(TenantCommercialAccessMode.RequiresSubscription, reloaded!.CommercialAccessMode);
        }

        [Fact]
        public async Task UpdateTenantCommercialSettings_ExentoSinPlanForzado_EsRechazado()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var _c = context;
            using var _n = connection;

            var tenantId = Guid.NewGuid();
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Sin plan", Activo = true });
            await context.SaveChangesAsync();

            var controller = CreateController(context, "platform-user-7");

            var result = await controller.UpdateTenantCommercialSettings(
                tenantId,
                TenantCommercialAccessMode.Exempt,
                forcedPlanId: null,
                commercialNotes: null,
                CancellationToken.None);

            Assert.IsType<RedirectToActionResult>(result);
            var error = Assert.IsType<string>(controller.TempData["PlatformError"]);
            Assert.Contains("requieren un plan forzado activo", error);
        }

        private static PlatformController CreateController(ProyectoIdentity.Datos.ApplicationDbContext context, string userId)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            using var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, userId) },
                        authenticationType: "TestAuth"))
            };

            var controller = new PlatformController(
                context,
                CreateResolver(context, cache, accessCache),
                accessCache,
                new TenantExecutionService(
                    serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<TenantExecutionService>.Instance),
                new StubMetaWhatsAppClient(),
                new NullPlatformAuditService(),
                new NullPlatformMetricsService(),
                new NullPlatformHealthService(),
                new NullPlatformWhatsAppStatusService(),
                new NullPlatformMissionControlService(),
                new FakeTenantOwnerResolver())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };

            controller.TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider());
            return controller;
        }

        private static async Task<bool> BindBooleanFromFormAsync(params string[] values)
        {
            var metadataProvider = new EmptyModelMetadataProvider();
            var metadata = metadataProvider.GetMetadataForType(typeof(bool));
            var actionContext = new ActionContext(
                new DefaultHttpContext(),
                new RouteData(),
                new ActionDescriptor(),
                new ModelStateDictionary());
            var valueProvider = new FormValueProvider(
                BindingSource.Form,
                new FormCollection(new Dictionary<string, StringValues>
                {
                    ["whatsappSettings.IsEnabled"] = new StringValues(values)
                }),
                CultureInfo.InvariantCulture);
            var bindingContext = DefaultModelBindingContext.CreateBindingContext(
                actionContext,
                valueProvider,
                metadata,
                bindingInfo: null,
                modelName: "whatsappSettings.IsEnabled");
            var binder = new SimpleTypeModelBinder(typeof(bool), NullLoggerFactory.Instance);

            await binder.BindModelAsync(bindingContext);

            Assert.True(bindingContext.Result.IsModelSet);
            return Assert.IsType<bool>(bindingContext.Result.Model);
        }

        private static ITenantCommercialAccessResolver CreateResolver(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            MemoryCache cache,
            TenantCommercialAccessCache accessCache)
        {
            var subscriptionService = new SuscripcionService(
                context,
                cache,
                accessCache,
                new FixedBusinessDateTimeProvider(),
                Options.Create(new TilopayRepeatOptions()),
                NullLogger<SuscripcionService>.Instance);

            return new TenantCommercialAccessResolver(
                context,
                cache,
                accessCache,
                subscriptionService,
                new FixedBusinessDateTimeProvider());
        }

        private static ServiceProvider BuildWhatsAppPlatformServiceProvider(SqliteConnection connection)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache();
            services.AddHttpContextAccessor();
            services.AddSingleton<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
            services.AddScoped<ITenantProvider, TenantProvider>();
            services.AddDbContext<ProyectoIdentity.Datos.ApplicationDbContext>(options => options.UseSqlite(connection));
            services.AddScoped<IOptionsMonitor<MetaWhatsAppOptions>>(_ =>
                new StaticOptionsMonitor<MetaWhatsAppOptions>(new MetaWhatsAppOptions { Enabled = true }));
            services.AddScoped<IBusinessDateTimeProvider>(_ => new FixedBusinessDateTimeProvider());
            services.AddScoped<ITenantCommercialAccessCache, TenantCommercialAccessCache>();
            services.AddScoped<SuscripcionService>();
            services.Configure<TilopayRepeatOptions>(_ => { });
            services.AddScoped<ITenantCommercialAccessResolver, TenantCommercialAccessResolver>();
            services.AddScoped<ITenantWhatsAppSettingsService, TenantWhatsAppSettingsService>();
            services.AddSingleton<TenantExecutionService>();
            return services.BuildServiceProvider();
        }

        private sealed class StubMetaWhatsAppClient : IMetaWhatsAppClient
        {
            public bool IsValidPhoneNumber(string? phoneNumber) => true;

            public string? NormalizePhoneNumber(string? phoneNumber) => phoneNumber;

            public Task<MetaWhatsAppSendResult> SendConfirmationTemplateAsync(
                string recipientPhone,
                string customerName,
                string businessName,
                string appointmentDate,
                string appointmentTime,
                string professionalName,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(MetaWhatsAppSendResult.Succeeded("confirmation-test", System.Net.HttpStatusCode.OK, null));

            public Task<MetaWhatsAppSendResult> SendReminderTemplateAsync(
                string recipientPhone,
                string customerName,
                string businessName,
                string appointmentTime,
                string professionalName,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(MetaWhatsAppSendResult.Succeeded("reminder-test", System.Net.HttpStatusCode.OK, null));

            public Task<MetaWhatsAppSendResult> SendCancellationTemplateAsync(
                string recipientPhone,
                WhatsAppCancellationTemplateParameters parameters,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(MetaWhatsAppSendResult.Succeeded("cancellation-test", System.Net.HttpStatusCode.OK, null));

            public Task<MetaWhatsAppSendResult> SendTextMessageAsync(
                string recipientPhone,
                string message,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(MetaWhatsAppSendResult.Succeeded("text-test", System.Net.HttpStatusCode.OK, null));

            public Task<MetaWhatsAppConfigurationDiagnosticResult> TestConfigurationAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new MetaWhatsAppConfigurationDiagnosticResult(
                    Success: true,
                    Configuration: MetaWhatsAppConfigurationSnapshot.Create(new MetaWhatsAppOptions
                    {
                        Enabled = true,
                        GraphApiVersion = "v25.0",
                        BaseUrl = "https://graph.facebook.com",
                        PhoneNumberId = "1000000000000001",
                        WhatsAppBusinessAccountId = "2000000000000002",
                        AccessToken = "EAATEST000000000000000000000",
                        AppSecret = "00000000000000000000000000000000",
                        ConfirmationTemplateName = "luxurycloud_confirmacion_cita_v1",
                        ReminderTemplateName = "luxurycloud_recordatorio_cita_3h_v1",
                        DefaultCountryCode = "506",
                        RequestTimeoutSeconds = 15,
                        SendConfirmationOnCreate = true,
                        SendReminderBeforeAppointment = true
                    }),
                    PhoneNumberProbe: new MetaWhatsAppEndpointProbeResult(
                        Success: true,
                        Endpoint: "https://graph.facebook.com/v25.0/1000000000000001?fields=id,display_phone_number,verified_name",
                        HttpStatus: 200,
                        DisplayPhoneNumber: "+50688889999",
                        VerifiedName: "LuxuryCloud",
                        ErrorType: null,
                        ErrorCode: null,
                        ErrorSubcode: null,
                        ErrorMessage: null,
                        FbTraceId: null,
                        ResponsePreview: null),
                    WabaPhoneNumbersProbe: new MetaWhatsAppEndpointProbeResult(
                        Success: true,
                        Endpoint: "https://graph.facebook.com/v25.0/2000000000000002/phone_numbers?fields=id,display_phone_number,verified_name",
                        HttpStatus: 200,
                        DisplayPhoneNumber: null,
                        VerifiedName: null,
                        ErrorType: null,
                        ErrorCode: null,
                        ErrorSubcode: null,
                        ErrorMessage: null,
                        FbTraceId: null,
                        ResponsePreview: null),
                    PhoneNumberBelongsToConfiguredWaba: true));
        }

        [Fact]
        public async Task TogglePromotionalCode_ShouldAuditBeforeAndAfterState()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var planId = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan audit",
                PrecioMensual = 10,
                Moneda = "CRC",
                Activo = true
            });

            var codeId = Guid.NewGuid();
            context.PromotionalCodes.Add(new PromotionalCode
            {
                Id = codeId,
                Codigo = "AUDIT-TEST",
                Activo = true,
                PlanId = planId
            });
            await context.SaveChangesAsync();

            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            var auditService = new RecordingPlatformAuditService();
            using var provider = new ServiceCollection().BuildServiceProvider();

            var controller = new PlatformController(
                context,
                CreateResolver(context, cache, accessCache),
                accessCache,
                new TenantExecutionService(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<TenantExecutionService>.Instance),
                new StubMetaWhatsAppClient(),
                auditService,
                new NullPlatformMetricsService(),
                new NullPlatformHealthService(),
                new NullPlatformWhatsAppStatusService(),
                new NullPlatformMissionControlService(),
                new FakeTenantOwnerResolver());
            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("platform-user", Guid.NewGuid(), isPlatformSuperAdmin: true));

            var result = await controller.TogglePromotionalCode(codeId, CancellationToken.None);

            Assert.IsType<RedirectToActionResult>(result);

            var updated = await context.PromotionalCodes.SingleAsync(code => code.Id == codeId);
            Assert.False(updated.Activo);

            var entry = Assert.Single(auditService.Entries);
            Assert.Equal(PlatformAuditActions.PromotionalCodeToggled, entry.Action);
            Assert.Equal(PlatformAuditEntityTypes.PromotionalCode, entry.EntityType);
            Assert.Equal(codeId.ToString(), entry.EntityId);
            Assert.Contains("\"Activo\":true", entry.BeforeJson);
            Assert.Contains("\"Activo\":false", entry.AfterJson);
            Assert.Contains("AUDIT-TEST", entry.BeforeJson);
        }

        private sealed class RecordingPlatformAuditService : IPlatformAuditService
        {
            public List<PlatformAuditEntry> Entries { get; } = new();

            public Task LogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default)
            {
                Entries.Add(entry);
                return Task.CompletedTask;
            }

            public Task TryLogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default) =>
                LogAsync(entry, cancellationToken);

            public Task<IReadOnlyList<PlatformAuditLog>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<IReadOnlyList<PlatformAuditLog>> GetByTenantAsync(Guid tenantId, int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<IReadOnlyList<PlatformAuditLog>> GetByUserAsync(string targetUserId, int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<int> CountActorFailuresSinceAsync(string actorUserId, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
                Task.FromResult(0);
        }

        private sealed class NullPlatformAuditService : IPlatformAuditService
        {
            public Task LogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task TryLogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<PlatformAuditLog>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<IReadOnlyList<PlatformAuditLog>> GetByTenantAsync(Guid tenantId, int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<IReadOnlyList<PlatformAuditLog>> GetByUserAsync(string targetUserId, int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<int> CountActorFailuresSinceAsync(string actorUserId, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
                Task.FromResult(0);
        }

        private sealed class NullPlatformMetricsService : IPlatformMetricsService
        {
            public Task<Dictionary<Guid, PlatformTenantUsageViewModel>> GetTenantUsageBatchAsync(
                IReadOnlyList<Guid> tenantIds,
                CancellationToken cancellationToken = default)
            {
                var result = tenantIds
                    .Distinct()
                    .ToDictionary(tenantId => tenantId, _ => new PlatformTenantUsageViewModel());

                return Task.FromResult(result);
            }

            public Task<PlatformTenantUsageViewModel> GetTenantUsageAsync(
                Guid tenantId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PlatformTenantUsageViewModel());
        }

        private sealed class NullPlatformHealthService : IPlatformHealthService
        {
            public PlatformTenantHealthViewModel ComputeHealth(
                bool canAccessApp,
                PlatformTenantUsageViewModel usage,
                bool whatsAppEnabled,
                bool hasWhatsAppRecentError,
                bool hasPendingCheckout,
                bool isExpiringSoon) =>
                new() { State = TenantHealthState.Saludable };
        }

        private sealed class NullPlatformWhatsAppStatusService : IPlatformWhatsAppStatusService
        {
            public Task<Dictionary<Guid, PlatformWhatsAppAddonState>> GetBatchStatusAsync(
                IReadOnlyList<Guid> tenantIds,
                CancellationToken cancellationToken = default)
            {
                var result = tenantIds
                    .Distinct()
                    .ToDictionary(tenantId => tenantId, _ => new PlatformWhatsAppAddonState());

                return Task.FromResult(result);
            }

            public Task<PlatformWhatsAppAddonState> GetSingleStatusAsync(
                Guid tenantId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PlatformWhatsAppAddonState());
        }

        private sealed class FakeTempDataProvider : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

            public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            {
            }
        }

        private sealed class NullPlatformMissionControlService : LuxuryApp.Services.Platform.IPlatformMissionControlService
        {
            public Task<LuxuryApp.Models.Platform.MissionControl.MissionControlSnapshotViewModel> GetSnapshotAsync(
                bool forceRefresh = false,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new LuxuryApp.Models.Platform.MissionControl.MissionControlSnapshotViewModel());
        }
    }
}
