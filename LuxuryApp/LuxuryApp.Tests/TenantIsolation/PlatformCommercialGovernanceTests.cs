using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Refactor de gobierno comercial de plataforma. Cubre los cuatro bugs reales encontrados en
    /// la consola:
    ///   1. El contacto principal se resolvia por orden alfabetico de correo, asi que un
    ///      FUNCIONARIO le ganaba al ADMINISTRADOR (p. ej. dana.staff@ sobre
    ///      owner@).
    ///   2. El limite de funcionarios se calculaba en dos lugares: el enforcement usaba el plan
    ///      efectivo (forzado) y el display leia la fila de Suscripciones, mostrando el limite viejo.
    ///   3. El selector de plan base forzado mezclaba add-ons WhatsApp y planes legacy/prueba, y el
    ///      servidor aceptaba guardar WA400 como plan base.
    ///   4. Un tenant exento con add-on manual contaba como "add-on sin plan base" en Billing Health.
    /// </summary>
    public class PlatformCommercialGovernanceTests
    {
        // ─────────────────────────────────────────────────────────────────────────────
        // A. Contacto principal / owner
        // ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Owner_AdminGanaSobreFuncionario_AunqueElCorreoSeaAlfabeticamenteMenor()
        {
            using var harness = await OwnerHarness.CreateAsync();

            var tenantId = Guid.NewGuid();
            harness.AddTenant(tenantId, "Northside Studio");

            // Regresión: "d" < "o", asi que el orden alfabetico elegia al funcionario.
            await harness.AddUserAsync(tenantId, "dana.staff@northside-studio.example", "ALEX", AppRoles.Funcionario);
            await harness.AddUserAsync(
                tenantId,
                "owner@northside-studio.example",
                "Northside Studio",
                AppRoles.Registrado,
                AppRoles.Administrador);

            var resolution = await harness.Resolver.ResolveAsync(tenantId);

            Assert.Equal("owner@northside-studio.example", resolution.OwnerEmail);
            Assert.Equal(TenantOwnerSource.AdminRegistrado, resolution.Source);
            Assert.False(resolution.OwnerIsFallback);

            // El funcionario sigue visible como cuenta adicional, no como contacto.
            Assert.Contains(
                resolution.Funcionarios,
                user => user.Email == "dana.staff@northside-studio.example");
            Assert.DoesNotContain(
                resolution.AdditionalAdmins,
                user => user.Email == "dana.staff@northside-studio.example");
        }

        [Fact]
        public async Task Owner_SinAdministrador_UsaFuncionarioPeroAdvierte()
        {
            using var harness = await OwnerHarness.CreateAsync();

            var tenantId = Guid.NewGuid();
            harness.AddTenant(tenantId, "Solo funcionarios");
            await harness.AddUserAsync(tenantId, "funcionario@ejemplo.com", "Func", AppRoles.Funcionario);

            var resolution = await harness.Resolver.ResolveAsync(tenantId);

            Assert.Equal("funcionario@ejemplo.com", resolution.OwnerEmail);
            Assert.Equal(TenantOwnerSource.FallbackFuncionario, resolution.Source);
            Assert.True(resolution.OwnerIsFallback);
            Assert.Contains(resolution.Warnings, warning => warning.Contains("FUNCIONARIO"));
            Assert.Contains(resolution.Warnings, warning => warning.Contains("no tiene ninguna cuenta con rol Administrador"));
        }

        [Fact]
        public async Task Owner_VariosAdmins_EligeElRegistradoYAdvierte()
        {
            using var harness = await OwnerHarness.CreateAsync();

            var tenantId = Guid.NewGuid();
            harness.AddTenant(tenantId, "Dos admins");

            // "aaa" gana alfabeticamente pero NO es el Registrado.
            await harness.AddUserAsync(tenantId, "aaa-admin@ejemplo.com", "Segundo", AppRoles.Administrador);
            await harness.AddUserAsync(tenantId, "zzz-dueno@ejemplo.com", "Dueño", AppRoles.Registrado, AppRoles.Administrador);

            var resolution = await harness.Resolver.ResolveAsync(tenantId);

            Assert.Equal("zzz-dueno@ejemplo.com", resolution.OwnerEmail);
            Assert.Equal(TenantOwnerSource.AdminRegistrado, resolution.Source);
            Assert.Contains(resolution.Warnings, warning => warning.Contains("2 administradores"));
        }

        [Fact]
        public async Task Owner_ResolveBatch_NoMezclaTenants()
        {
            using var harness = await OwnerHarness.CreateAsync();

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            harness.AddTenant(tenantA, "Tenant A");
            harness.AddTenant(tenantB, "Tenant B");

            await harness.AddUserAsync(tenantA, "admin-a@ejemplo.com", "A", AppRoles.Administrador);
            await harness.AddUserAsync(tenantB, "admin-b@ejemplo.com", "B", AppRoles.Administrador);
            // Un funcionario en B con correo alfabeticamente menor que el admin de B.
            await harness.AddUserAsync(tenantB, "aaa-func-b@ejemplo.com", "Func B", AppRoles.Funcionario);

            var batch = await harness.Resolver.ResolveBatchAsync(new[] { tenantA, tenantB });

            Assert.Equal("admin-a@ejemplo.com", batch[tenantA].OwnerEmail);
            Assert.Equal("admin-b@ejemplo.com", batch[tenantB].OwnerEmail);
        }

        [Fact]
        public async Task Owner_TenantSinUsuarios_DevuelveVacioConAdvertencia()
        {
            using var harness = await OwnerHarness.CreateAsync();

            var tenantId = Guid.NewGuid();
            harness.AddTenant(tenantId, "Vacio");
            await harness.Context.SaveChangesAsync();

            var resolution = await harness.Resolver.ResolveAsync(tenantId);

            Assert.Null(resolution.Owner);
            Assert.Equal(TenantOwnerSource.None, resolution.Source);
            Assert.True(resolution.HasWarnings);
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // B. Plan efectivo y limite de funcionarios
        // ─────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("LC_M_03", 3)]
        [InlineData("LC_M_05", 5)]
        [InlineData("LC_A_07", 7)]
        public async Task PlanForzado_DefineElLimiteEfectivo(string planCode, int expectedLimit)
        {
            using var harness = await OwnerHarness.CreateAsync();

            var tenantId = Guid.NewGuid();
            // Suscripcion vieja con OTRO limite: es la que producia el bug de "limite 7".
            var oldPlanId = SeedPlan(harness.Context, "LC_M_07", "LuxuryCloud Mensual 7 funcionarios", 7);
            var forcedPlanId = SeedPlan(harness.Context, planCode, $"Plan {planCode}", expectedLimit);

            harness.Context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant exento",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = forcedPlanId
            });

            harness.Context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = oldPlanId,
                CodigoPlan = "LC_M_07",
                MaxFuncionarios = 7,
                Estado = EstadoSuscripcion.Activa,
                FechaInicio = DateTime.UtcNow.AddMonths(-3),
                FechaFin = DateTime.UtcNow.AddMonths(1)
            });

            await harness.Context.SaveChangesAsync();

            var access = await harness.AccessResolver.ResolveAsync(tenantId);

            Assert.True(access.CanAccessApp);
            Assert.True(access.IsForcedByPlatform);
            Assert.Equal(planCode, access.EffectivePlanCode);
            Assert.Equal(expectedLimit, access.EffectiveEmployeeLimit);
            Assert.Equal(TenantAccessBillingSource.Manual, access.BillingSource);
            Assert.Equal(PlanCatalogKind.BaseCommercial, access.EffectivePlanKind);

            // El resumen que ve el cliente debe dar el MISMO limite, no el 7 de la suscripcion.
            var summary = await harness.BuildSummaryAsync(tenantId);
            Assert.NotNull(summary);
            Assert.Equal(expectedLimit, summary!.MaxFuncionarios);
            Assert.Equal($"Plan {planCode}", summary.PlanName);
            Assert.True(summary.IsPlatformGrantedAccess);
        }

        [Fact]
        public async Task PlanForzado_SinSuscripcion_IgualMuestraPlanYLimite()
        {
            using var harness = await OwnerHarness.CreateAsync();

            var tenantId = Guid.NewGuid();
            var forcedPlanId = SeedPlan(harness.Context, "LC_M_05", "LuxuryCloud Mensual 5 funcionarios", 5);

            harness.Context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Northside Studio",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = forcedPlanId
            });
            await harness.Context.SaveChangesAsync();

            // Antes esta rama devolvia null (sin suscripcion ni add-on) y la cuenta mostraba "0 / ∞".
            var summary = await harness.BuildSummaryAsync(tenantId);

            Assert.NotNull(summary);
            Assert.Equal(5, summary!.MaxFuncionarios);
            Assert.Equal("LuxuryCloud Mensual 5 funcionarios", summary.PlanName);
            Assert.True(summary.CanAccessApp);
            Assert.True(summary.IsPlatformGrantedAccess);
        }

        [Fact]
        public async Task TenantPagado_NoCambiaSuLimite_YMarcaFuenteProveedor()
        {
            using var harness = await OwnerHarness.CreateAsync();

            var tenantId = Guid.NewGuid();
            var planId = SeedPlan(harness.Context, "LC_M_02", "LuxuryCloud Mensual 2 funcionarios", 2);

            harness.Context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Alpha",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.RequiresSubscription
            });

            harness.Context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                CodigoPlan = "LC_M_02",
                MaxFuncionarios = 2,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = 6120,
                ProviderSubscriptionId = "sub-alpha",
                FechaInicio = DateTime.UtcNow.AddMonths(-1),
                FechaFin = DateTime.UtcNow.AddMonths(1)
            });
            await harness.Context.SaveChangesAsync();

            var access = await harness.AccessResolver.ResolveAsync(tenantId);

            Assert.True(access.CanAccessApp);
            Assert.False(access.IsForcedByPlatform);
            Assert.Equal(2, access.EffectiveEmployeeLimit);
            Assert.Equal(TenantAccessBillingSource.ProviderRecurring, access.BillingSource);
            Assert.Equal("sub-alpha", access.ProviderSubscriptionId);

            var summary = await harness.BuildSummaryAsync(tenantId);
            Assert.Equal(2, summary!.MaxFuncionarios);
            Assert.False(summary.IsPlatformGrantedAccess);
        }

        [Fact]
        public async Task PlanForzadoAddonWhatsApp_NoAportaLimiteYAdvierte()
        {
            using var harness = await OwnerHarness.CreateAsync();

            var tenantId = Guid.NewGuid();
            // Configuracion invalida que el estado actual de la base podria tener heredada.
            var addonPlanId = SeedPlan(harness.Context, PlanCodes.WhatsApp400, "WhatsApp 400", maxFuncionarios: null);
            harness.Context.Planes.Local.First(plan => plan.Id == addonPlanId).LimiteMensajesMensual = 400;

            harness.Context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Mal configurado",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = addonPlanId
            });
            await harness.Context.SaveChangesAsync();

            var access = await harness.AccessResolver.ResolveAsync(tenantId);

            Assert.Equal(PlanCatalogKind.WhatsAppAddon, access.EffectivePlanKind);
            // NO hereda LimiteMensajesMensual como si fuera cupo de personal.
            Assert.Null(access.EffectiveEmployeeLimit);
            Assert.Contains(access.Warnings, warning => warning.Contains("no es un plan base valido"));
        }

        [Fact]
        public async Task PlanForzadoLegacy_ResuelveLimitePeroPideMigracion()
        {
            using var harness = await OwnerHarness.CreateAsync();

            var tenantId = Guid.NewGuid();
            var legacyPlanId = SeedPlan(harness.Context, PlanCodes.Pro, "Pro", 7);

            harness.Context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Legacy",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = legacyPlanId
            });
            await harness.Context.SaveChangesAsync();

            var access = await harness.AccessResolver.ResolveAsync(tenantId);

            Assert.Equal(PlanCatalogKind.LegacyBase, access.EffectivePlanKind);
            Assert.Equal(7, access.EffectiveEmployeeLimit);
            Assert.Contains(access.Warnings, warning => warning.Contains("Migrar a un plan de la calculadora"));
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // C. Clasificacion de planes / selector
        // ─────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("LC_M_01", PlanCatalogKind.BaseCommercial)]
        [InlineData("LC_M_11", PlanCatalogKind.BaseCommercial)]
        [InlineData("LC_A_05", PlanCatalogKind.BaseCommercial)]
        [InlineData("WA400", PlanCatalogKind.WhatsAppAddon)]
        [InlineData("WA800", PlanCatalogKind.WhatsAppAddon)]
        [InlineData("WA1200", PlanCatalogKind.WhatsAppAddon)]
        [InlineData("BASIC", PlanCatalogKind.LegacyBase)]
        [InlineData("PRO", PlanCatalogKind.LegacyBase)]
        [InlineData("BUSINESS", PlanCatalogKind.LegacyBase)]
        [InlineData("TEST_PROD_BASIC_100", PlanCatalogKind.Validation)]
        [InlineData("TEST_RECURRING", PlanCatalogKind.Validation)]
        [InlineData("CODIGO_RARO", PlanCatalogKind.Unknown)]
        [InlineData(null, PlanCatalogKind.Unknown)]
        public void PlanCatalogRules_ClasificaCadaFamilia(string? codigo, PlanCatalogKind expected) =>
            Assert.Equal(expected, PlanCatalogRules.Classify(codigo));

        [Fact]
        public void PlanCatalogRules_PlanDeValidacionGanaSobreElCodigo() =>
            Assert.Equal(
                PlanCatalogKind.Validation,
                PlanCatalogRules.Classify("LC_M_03", esPlanValidacion: true));

        [Fact]
        public void PlanCatalogRules_AddonWhatsAppNuncaEsPlanBase()
        {
            foreach (var code in PlanCodes.WhatsAppAddons)
            {
                Assert.False(
                    PlanCatalogRules.IsBasePlan(PlanCatalogRules.Classify(code)),
                    $"{code} no debe poder usarse como plan base.");
            }
        }

        [Fact]
        public void PlanCatalogRules_DesconocidoNoEsPlanBase_FailClosed()
        {
            Assert.False(PlanCatalogRules.IsBasePlan(PlanCatalogKind.Unknown));
            Assert.False(PlanCatalogRules.IsBasePlan((Plan?)null));
        }

        [Fact]
        public void PlanCatalogRules_LegacyYValidacionSonSoloAvanzado()
        {
            Assert.True(PlanCatalogRules.IsAdvancedOnly(PlanCatalogKind.LegacyBase));
            Assert.True(PlanCatalogRules.IsAdvancedOnly(PlanCatalogKind.Validation));
            Assert.False(PlanCatalogRules.IsAdvancedOnly(PlanCatalogKind.BaseCommercial));
        }

        [Fact]
        public void PlanCatalogRules_OrdenaCalculadoraPorFuncionarios_NoPorPrecio()
        {
            // El orden por precio intercalaba WA400 entre LC_M_01 y LC_M_02.
            var plans = new[]
            {
                new Plan { Codigo = "LC_M_03", Nombre = "M3", MaxFuncionarios = 3, PrecioMensual = 30_000m },
                new Plan { Codigo = "LC_M_01", Nombre = "M1", MaxFuncionarios = 1, PrecioMensual = 12_000m },
                new Plan { Codigo = "LC_A_01", Nombre = "A1", MaxFuncionarios = 1, PrecioMensual = 120_000m, BillingCycle = BillingCycle.Annual },
                new Plan { Codigo = "LC_M_02", Nombre = "M2", MaxFuncionarios = 2, PrecioMensual = 20_000m }
            };

            var ordered = plans.OrderBy(PlanCatalogRules.SortKey).Select(plan => plan.Codigo).ToArray();

            Assert.Equal(new[] { "LC_M_01", "LC_M_02", "LC_M_03", "LC_A_01" }, ordered);
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // D. Aislamiento cross-tenant de las metricas de plataforma
        // ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Metricas_UsanElTenantSeleccionado_NoElDelUsuarioDePlataforma()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantPlataforma = Guid.NewGuid();
            var tenantConsultado = Guid.NewGuid();

            context.Tenants.Add(new Tenant { Id = tenantPlataforma, Nombre = "Tenant plataforma", Activo = true });
            context.Tenants.Add(new Tenant { Id = tenantConsultado, Nombre = "Tenant consultado", Activo = true });
            await context.SaveChangesAsync();

            // El guard de tenant prohibe mezclar tenants en un mismo SaveChanges, asi que se siembra
            // cada tenant dentro de su propio contexto (igual que en produccion).
            // El tenant consultado tiene 2 citas; el del usuario de plataforma tiene 5.
            await SeedCitasAsync(context, tenantProvider, tenantConsultado, count: 2);
            await SeedCitasAsync(context, tenantProvider, tenantPlataforma, count: 5);

            // Se simula la sesion del super admin: su tenant es OTRO. Si las metricas dependieran
            // del tenant del usuario en vez del solicitado, este es el escenario que lo delata.
            tenantProvider.TenantId = tenantPlataforma;

            var metrics = new LuxuryApp.Services.Platform.PlatformMetricsService(context);
            var usage = await metrics.GetTenantUsageAsync(tenantConsultado);

            Assert.Equal(2, usage.Citas30d);

            var batch = await metrics.GetTenantUsageBatchAsync(new[] { tenantConsultado, tenantPlataforma });
            Assert.Equal(2, batch[tenantConsultado].Citas30d);
            Assert.Equal(5, batch[tenantPlataforma].Citas30d);
        }

        [Fact]
        public async Task Metricas_RechazanTenantIdVacio_EnVezDeDevolverCeros()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var metrics = new LuxuryApp.Services.Platform.PlatformMetricsService(context);

            // Un Guid.Empty colado al filtro devolveria 0 en todo, que se leeria como
            // "este tenant no tiene actividad" en vez de como un error de programacion.
            await Assert.ThrowsAsync<ArgumentException>(
                () => metrics.GetTenantUsageAsync(Guid.Empty));
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────────────

        private static Guid SeedPlan(
            ApplicationDbContext context,
            string codigo,
            string nombre,
            int? maxFuncionarios)
        {
            var id = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = id,
                Codigo = codigo,
                Nombre = nombre,
                PrecioMensual = 10_000m,
                Moneda = "CRC",
                Activo = true,
                MaxFuncionarios = maxFuncionarios
            });
            return id;
        }

        private static LuxuryApp.Models.Calendar.Cita BuildCita(Guid tenantId, int funcionarioId, DateTime fecha) =>
            new()
            {
                TenantId = tenantId,
                FuncionarioId = funcionarioId,
                FechaHoraCita = fecha,
                NombreCliente = "Cliente"
            };

        /// <summary>
        /// Siembra puesto + funcionario + citas de UN tenant con el contexto de ese tenant activo.
        /// Cita.FuncionarioId es FK obligatoria y el guard valida que el principal sea del mismo tenant.
        /// </summary>
        private static async Task SeedCitasAsync(
            ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            Guid tenantId,
            int count)
        {
            tenantProvider.TenantId = tenantId;

            var puesto = new LuxuryApp.Models.Funcionarios.Puesto
            {
                TenantId = tenantId,
                NombrePuesto = "Estilista",
                Activo = true
            };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new LuxuryApp.Models.Funcionarios.Funcionario
            {
                TenantId = tenantId,
                Nombre = "Funcionario",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#000000",
                FechaIngreso = DateTime.UtcNow.AddYears(-1),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();

            for (var i = 0; i < count; i++)
            {
                context.Citas.Add(BuildCita(tenantId, funcionario.IdFuncionario, DateTime.UtcNow.AddDays(-1 - i)));
            }

            await context.SaveChangesAsync();
        }

        /// <summary>
        /// Contexto SQLite con los servicios REALES del refactor (resolver de owner, resolver de
        /// acceso comercial y resumen de suscripcion). Se usan implementaciones reales a proposito:
        /// un fake no probaria que display y enforcement leen el mismo limite.
        /// </summary>
        private sealed class OwnerHarness : IDisposable
        {
            private readonly IDisposable _connection;
            private readonly RoleManager<IdentityRole> _roleManager;
            private readonly UserManager<AppUsuario> _userManager;
            private readonly ServiceProvider _provider;

            private OwnerHarness(
                ApplicationDbContext context,
                IDisposable connection,
                ServiceProvider provider,
                UserManager<AppUsuario> userManager,
                RoleManager<IdentityRole> roleManager,
                ITenantOwnerResolver resolver,
                ITenantCommercialAccessResolver accessResolver,
                ISubscriptionSummaryService summaryService)
            {
                Context = context;
                _connection = connection;
                _provider = provider;
                _userManager = userManager;
                _roleManager = roleManager;
                Resolver = resolver;
                AccessResolver = accessResolver;
                SummaryService = summaryService;
            }

            public ApplicationDbContext Context { get; }
            public ITenantOwnerResolver Resolver { get; }
            public ITenantCommercialAccessResolver AccessResolver { get; }
            public ISubscriptionSummaryService SummaryService { get; }

            public static Task<OwnerHarness> CreateAsync()
            {
                var tenantProvider = new TestTenantProvider();
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton(context);
                services.AddIdentityCore<AppUsuario>()
                    .AddRoles<IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>();

                var provider = services.BuildServiceProvider();

                var cache = new MemoryCache(new MemoryCacheOptions());
                var accessCache = new TenantCommercialAccessCache(cache);
                var clock = new FixedBusinessDateTimeProvider();
                var suscripcionService = new SuscripcionService(
                    context,
                    cache,
                    accessCache,
                    clock,
                    Options.Create(new TilopayRepeatOptions()),
                    NullLogger<SuscripcionService>.Instance);

                var accessResolver = new TenantCommercialAccessResolver(
                    context, cache, accessCache, suscripcionService, clock);

                var summaryService = new SubscriptionSummaryService(
                    context,
                    suscripcionService,
                    new StubWhatsAppSettingsForGovernance(),
                    accessResolver);

                return Task.FromResult(new OwnerHarness(
                    context,
                    connection,
                    provider,
                    provider.GetRequiredService<UserManager<AppUsuario>>(),
                    provider.GetRequiredService<RoleManager<IdentityRole>>(),
                    new TenantOwnerResolver(context),
                    accessResolver,
                    summaryService));
            }

            public void AddTenant(Guid tenantId, string nombre) =>
                Context.Tenants.Add(new Tenant { Id = tenantId, Nombre = nombre, Activo = true });

            /// <summary>
            /// Crea el usuario con roles REALES en AspNetUserRoles (no un campo de texto), que es
            /// exactamente lo que el resolver de owner tiene que leer.
            /// </summary>
            public async Task AddUserAsync(
                Guid tenantId,
                string email,
                string name,
                params string[] roles)
            {
                await Context.SaveChangesAsync();

                foreach (var role in roles)
                {
                    if (!await _roleManager.RoleExistsAsync(role))
                    {
                        await _roleManager.CreateAsync(new IdentityRole(role));
                    }
                }

                var user = new AppUsuario
                {
                    Id = Guid.NewGuid().ToString("N"),
                    UserName = email,
                    Email = email,
                    Name = name,
                    TenantId = tenantId,
                    State = true,
                    EmailConfirmed = true,
                    SecurityStamp = Guid.NewGuid().ToString("N")
                };

                var created = await _userManager.CreateAsync(user);
                Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

                if (roles.Length > 0)
                {
                    var assigned = await _userManager.AddToRolesAsync(user, roles);
                    Assert.True(assigned.Succeeded, string.Join("; ", assigned.Errors.Select(e => e.Description)));
                }
            }

            public Task<BillingSubscriptionSummaryViewModel?> BuildSummaryAsync(Guid tenantId) =>
                SummaryService.BuildAsync(tenantId);

            public void Dispose()
            {
                _provider.Dispose();
                Context.Dispose();
                _connection.Dispose();
            }
        }

        /// <summary>
        /// Ninguno de estos casos tiene add-on de WhatsApp activo, asi que el resumen no debe
        /// consultar la configuracion: los miembros que se usarian lanzan para detectarlo.
        /// </summary>
        private sealed class StubWhatsAppSettingsForGovernance : ITenantWhatsAppSettingsService
        {
            public Task<TenantWhatsAppSettingsSnapshot> GetSettingsForTenantAsync(
                Guid tenantId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("No deberia consultarse sin add-on activo.");

            public Task<TenantWhatsAppSettingsSnapshot> EnsureDefaultSettingsAsync(
                Guid tenantId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();

            public Task<bool> IsWhatsAppEnabledForTenantAsync(
                Guid tenantId, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);

            public Task<TenantWhatsAppSendDecision> CanSendNotificationAsync(
                Guid tenantId, string notificationType, long? reservedMessageLogId = null,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();

            public Task<int> GetTodayUsageAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                Task.FromResult(0);

            public Task<bool> HasActiveWhatsAppAddonAsync(
                Guid tenantId, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);

            public Task UpdateSettingsAsync(
                Guid tenantId, LuxuryApp.Models.WhatsApp.TenantWhatsAppSettingsUpdateDto dto,
                string? updatedByUserId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();
        }
    }
}
