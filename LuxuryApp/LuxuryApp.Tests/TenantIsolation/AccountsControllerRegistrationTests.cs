using LuxuryApp.Controllers.Identity;
using LuxuryApp.Models.Identity;
using LuxuryApp.Services.Account;
using LuxuryApp.Models.Legal;
using LuxuryApp.Models.Marketing;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Contracts;
using LuxuryApp.Services.PublicSite;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class AccountsControllerRegistrationTests
    {
        [Fact]
        public async Task RegistroPost_ShouldCreatePendingVerificationTenantWhenCheckboxIsAcceptedAndHiddenDocumentIsMissing()
        {
            await using var provider = await CreateServiceProviderAsync(hasActiveContract: true);
            using var scope = provider.CreateScope();
            var controller = CreateController(scope.ServiceProvider, CreateAcceptedContractForm());
            var email = "registro.owner@luxurytest.example";

            var result = await controller.Registro(
                CreateRegistrationModel(email, acceptCurrentContract: false, currentContractDocumentId: null),
                returnurl: "/");

            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var modelStateErrors = string.Join(
                " | ",
                controller.ModelState.SelectMany(entry => entry.Value!.Errors.Select(error => error.ErrorMessage)));
            var usersCreated = await dbContext.Users.CountAsync(user => user.Email == email);
            var acceptancesCreated = await dbContext.ContractAcceptanceRecords.CountAsync();

            Assert.True(
                result is RedirectToActionResult,
                $"Expected redirect but got {result.GetType().Name}. ModelStateErrors: {modelStateErrors}. UsersCreated: {usersCreated}. AcceptancesCreated: {acceptancesCreated}.");

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("ConfirmacionRegistro", redirect.ActionName);
            Assert.Null(redirect.ControllerName);
            Assert.True(controller.ModelState.IsValid);

            var activeContract = await dbContext.ContractDocuments.SingleAsync(document => document.IsActive);
            var user = await dbContext.Users.SingleAsync(user => user.Email == email);
            var tenant = await dbContext.Tenants.SingleAsync(tenant => tenant.Id == user.TenantId);
            var acceptance = await dbContext.ContractAcceptanceRecords.SingleAsync();
            var emailService = Assert.IsType<NoOpAccountEmailService>(
                scope.ServiceProvider.GetRequiredService<IAccountEmailService>());

            Assert.Equal(activeContract.Id, acceptance.ContractDocumentId);
            Assert.Equal(user.Id, acceptance.UserId);
            Assert.False(user.EmailConfirmed);
            Assert.Equal(TenantCommercialAccessMode.PendingVerification, tenant.CommercialAccessMode);
            Assert.Equal(1, emailService.ConfirmationEmailsSent);
        }

        [Fact]
        public async Task ConfirmarEmail_ShouldActivatePendingTenantAndRedirectToPlans()
        {
            await using var provider = await CreateServiceProviderAsync(hasActiveContract: true);
            using var scope = provider.CreateScope();
            var controller = CreateController(scope.ServiceProvider, CreateAcceptedContractForm());
            var email = "confirm.owner@luxurytest.example";

            await controller.Registro(CreateRegistrationModel(email, acceptCurrentContract: false), returnurl: "/");

            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUsuario>>();
            var user = await dbContext.Users.SingleAsync(currentUser => currentUser.Email == email);
            var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));

            var result = await controller.ConfirmarEmail(user.Id, encodedToken);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Planes", redirect.ActionName);
            Assert.Equal("Billing", redirect.ControllerName);

            var updatedUser = await dbContext.Users.SingleAsync(currentUser => currentUser.Id == user.Id);
            var tenant = await dbContext.Tenants.SingleAsync(currentTenant => currentTenant.Id == user.TenantId);
            Assert.True(updatedUser.EmailConfirmed);
            Assert.Equal(TenantCommercialAccessMode.RequiresSubscription, tenant.CommercialAccessMode);
        }

        [Fact]
        public async Task RegistroPost_ShouldBlockWhenContractCheckboxIsNotAccepted()
        {
            await using var provider = await CreateServiceProviderAsync(hasActiveContract: true);
            using var scope = provider.CreateScope();
            var controller = CreateController(scope.ServiceProvider, new FormCollection(new Dictionary<string, StringValues>()));

            var result = await controller.Registro(
                CreateRegistrationModel("noaccept.owner@luxurytest.example", acceptCurrentContract: false),
                returnurl: "/");

            Assert.IsType<ViewResult>(result);
            Assert.Contains(
                controller.ModelState.SelectMany(entry => entry.Value!.Errors),
                error => error.ErrorMessage == "Debes aceptar el contrato para crear tu cuenta.");

            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Empty(await dbContext.Users.ToArrayAsync());
            Assert.Empty(await dbContext.Tenants.ToArrayAsync());
            Assert.Empty(await dbContext.ContractAcceptanceRecords.ToArrayAsync());
        }

        [Fact]
        public async Task RegistroPost_ShouldBlockWhenNoActiveContractExists()
        {
            await using var provider = await CreateServiceProviderAsync(hasActiveContract: false);
            using var scope = provider.CreateScope();
            var controller = CreateController(scope.ServiceProvider, CreateAcceptedContractForm());

            var result = await controller.Registro(
                CreateRegistrationModel("noactive.owner@luxurytest.example", acceptCurrentContract: false),
                returnurl: "/");

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<RegistroViewModel>(view.Model);

            Assert.False(model.HasCurrentContract);
            Assert.Contains(
                controller.ModelState.SelectMany(entry => entry.Value!.Errors),
                error => error.ErrorMessage == "No hay un contrato vigente configurado en este momento. Contacta soporte.");
        }

        private static AccountsController CreateController(
            IServiceProvider serviceProvider,
            IFormCollection form)
        {
            var controller = ActivatorUtilities.CreateInstance<AccountsController>(serviceProvider);
            var httpContext = ControllerTestSupport.AttachHttpContext(controller);
            httpContext.RequestServices = serviceProvider;
            httpContext.Request.Method = HttpMethods.Post;
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.Form = form;
            controller.Url = new TestUrlHelper(controller.ControllerContext);
            return controller;
        }

        private static FormCollection CreateAcceptedContractForm() =>
            new(new Dictionary<string, StringValues>
            {
                [nameof(RegistroViewModel.AcceptCurrentContract)] = new(new[] { "true", "false" })
            });

        private static RegistroViewModel CreateRegistrationModel(
            string email,
            bool acceptCurrentContract,
            Guid? currentContractDocumentId = null) =>
            new()
            {
                Name = "Luxury Owner",
                Email = email,
                PhoneNumber = "88888888",
                Password = "Valid1!",
                ConfirmPassword = "Valid1!",
                AcceptCurrentContract = acceptCurrentContract,
                CurrentContractDocumentId = currentContractDocumentId
            };

        private static async Task<ServiceProvider> CreateServiceProviderAsync(bool hasActiveContract)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddHttpContextAccessor();
            services.AddMemoryCache();
            services.AddDataProtection();
            services.AddSingleton(connection);
            services.AddSingleton<TestWebHostEnvironment>();
            services.AddSingleton<IWebHostEnvironment>(serviceProvider => serviceProvider.GetRequiredService<TestWebHostEnvironment>());
            services.AddSingleton<IHostEnvironment>(serviceProvider => serviceProvider.GetRequiredService<TestWebHostEnvironment>());
            services.AddSingleton<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
            services.AddScoped<ITenantProvider, TenantProvider>();
            services.AddSingleton<ITenantCommercialAccessCache, TenantCommercialAccessCache>();
            services.Configure<IdentityOptions>(options =>
            {
                options.Password.RequiredLength = 5;
                options.Password.RequireUppercase = true;
            });
            services.Configure<TilopayRepeatOptions>(_ => { });
            services.Configure<OpcionesOnboardingTenant>(options =>
            {
                options.RegistrationRole = "Administrador";
                options.AddRegisteredRole = true;
                options.RegisteredRole = "Registrado";
                options.CreateInitialSubscription = false;
            });
            services.Configure<RegistrationSecurityOptions>(options =>
            {
                options.RequireEmailConfirmation = true;
                options.EnableHoneypot = true;
                options.BlockSuspiciousEmailPatterns = true;
                options.Registration.IpPermitLimit = 100;
                options.Registration.EmailPermitLimit = 100;
            });
            services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
            {
                options.UseSqlite(serviceProvider.GetRequiredService<SqliteConnection>());
            });
            services
                .AddIdentity<AppUsuario, IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();
            services.AddScoped<SignInManager<AppUsuario>, NoOpSignInManager>();
            services.AddSingleton<IBusinessDateTimeProvider>(_ => new FixedBusinessDateTimeProvider());
            services.AddScoped<SuscripcionService>();
            services.AddScoped<ITenantCommercialAccessResolver, TenantCommercialAccessResolver>();
            services.AddScoped<IPromotionalCodeService, PromotionalCodeService>();
            services.AddScoped<IContractService, ContractService>();
            services.AddScoped<TenantProvisioningService>();
            services.AddScoped<ITenantDisplayNameService, TenantDisplayNameService>();
            services.AddSingleton<RegistrationSecurityService>();
            services.AddHttpClient<TurnstileVerificationService>(client =>
            {
                client.BaseAddress = new Uri("https://challenges.cloudflare.com/");
            });
            services.AddScoped<IPublicSiteContentService, FakePublicSiteContentService>();
            services.AddScoped<LuxuryApp.Services.Asociados.IPostLoginDestinationService,
                LuxuryApp.Tests.Support.StubPostLoginDestinationService>();
            services.AddScoped<IAccountEmailService, NoOpAccountEmailService>();
            services.AddScoped<LuxuryApp.Services.Account.IAccountSettingsPageBuilder,
                LuxuryApp.Services.Account.AccountSettingsPageBuilder>();

            var provider = services.BuildServiceProvider();

            using (var setupScope = provider.CreateScope())
            {
                var dbContext = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await dbContext.Database.EnsureCreatedAsync();

                if (!hasActiveContract)
                {
                    var activeContract = await dbContext.ContractDocuments.SingleAsync(document => document.IsActive);
                    activeContract.IsActive = false;
                    activeContract.UpdatedAtUtc = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();
                }
            }

            return provider;
        }

        private sealed class NoOpAccountEmailService : IAccountEmailService
        {
            public int ConfirmationEmailsSent { get; private set; }

            public Task SendPasswordResetEmailAsync(string toEmail, string displayName, string resetLink, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task SendEmailConfirmationEmailAsync(string toEmail, string displayName, string confirmationLink, CancellationToken cancellationToken = default)
            {
                ConfirmationEmailsSent++;
                return Task.CompletedTask;
            }

            public Task SendFuncionarioInvitationEmailAsync(string toEmail, string displayName, string setPasswordLink, string businessName, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task SendAccessInvitationEmailAsync(string toEmail, string displayName, string setPasswordLink, string businessName, string accessDescription, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }

        private sealed class FakePublicSiteContentService : IPublicSiteContentService
        {
            public IReadOnlyCollection<MarketingMetricViewModel> GetHeroMetrics() => [];
            public IReadOnlyCollection<MarketingModuleViewModel> GetModules() => [];

            public Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetPlanCardsAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyCollection<MarketingPlanCardViewModel>>([]);

            public Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetWhatsAppAddonCardsAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyCollection<MarketingPlanCardViewModel>>([]);

            public Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetInternalPlanCardsAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyCollection<MarketingPlanCardViewModel>>([]);

            public Task<Plan?> FindAvailablePlanAsync(Guid planId, CancellationToken cancellationToken = default) =>
                Task.FromResult<Plan?>(null);

            public Task<string?> GetPlanNameAsync(Guid? planId, CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>(null);
        }

        private sealed class NoOpSignInManager : SignInManager<AppUsuario>
        {
            public NoOpSignInManager(
                UserManager<AppUsuario> userManager,
                IHttpContextAccessor contextAccessor,
                IUserClaimsPrincipalFactory<AppUsuario> claimsFactory,
                IOptions<IdentityOptions> optionsAccessor,
                ILogger<SignInManager<AppUsuario>> logger,
                IAuthenticationSchemeProvider schemes,
                IUserConfirmation<AppUsuario> confirmation)
                : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
            {
            }

            public override Task SignInAsync(AppUsuario user, bool isPersistent, string? authenticationMethod = null) =>
                Task.CompletedTask;
        }

        private sealed class TestUrlHelper : IUrlHelper
        {
            public TestUrlHelper(ActionContext actionContext)
            {
                ActionContext = actionContext;
            }

            public ActionContext ActionContext { get; }

            public string? Action(UrlActionContext actionContext) => "/";
            public string? Content(string? contentPath) => contentPath == "~/" ? "/" : contentPath;
            public bool IsLocalUrl(string? url) => !string.IsNullOrWhiteSpace(url) && url.StartsWith("/", StringComparison.Ordinal);
            public string? Link(string? routeName, object? values) => "/";
            public string? RouteUrl(UrlRouteContext routeContext) => "/";
        }
    }
}
