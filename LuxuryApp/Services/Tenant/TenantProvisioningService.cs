using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Legal;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Contracts;
using LuxuryApp.Services.SaaS;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Tenant
{
    public class TenantProvisioningService
    {
        private const string ContractAcceptanceRequiredError = "Debes aceptar el contrato para crear tu cuenta.";
        private const string ContractValidationUnavailableError = "No fue posible validar el contrato vigente. Recarga la página e intenta de nuevo.";
        private const string ContractChangedError = "El contrato vigente cambió. Recarga la página e intenta de nuevo.";
        private const string NoActiveContractError = "No hay un contrato vigente configurado en este momento. Contacta soporte.";

        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUsuario> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly OpcionesOnboardingTenant _options;
        private readonly IContractService _contractService;
        private readonly IPromotionalCodeService _promotionalCodeService;
        private readonly ITenantCommercialAccessResolver _commercialAccessResolver;
        private readonly IHostEnvironment _environment;
        private readonly ILogger<TenantProvisioningService> _logger;

        public TenantProvisioningService(
            ApplicationDbContext context,
            UserManager<AppUsuario> userManager,
            RoleManager<IdentityRole> roleManager,
            IContractService contractService,
            IPromotionalCodeService promotionalCodeService,
            ITenantCommercialAccessResolver commercialAccessResolver,
            IOptions<OpcionesOnboardingTenant> options,
            IHostEnvironment environment,
            ILogger<TenantProvisioningService> logger)
        {
            _context = context;
            _userManager = userManager;
            _roleManager = roleManager;
            _contractService = contractService;
            _promotionalCodeService = promotionalCodeService;
            _commercialAccessResolver = commercialAccessResolver;
            _options = options.Value;
            _environment = environment;
            _logger = logger;
        }

        public async Task<TenantProvisioningResult> RegisterAsync(
            TenantRegistrationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var email = request.Email.Trim();
            var name = request.Name.Trim();
            var phoneNumber = request.PhoneNumber?.Trim();
            var accessCode = request.AccessCode?.Trim();
            var activeContract = await _contractService.GetActiveContractAsync(cancellationToken);
            var activeContractDocumentId = activeContract?.Id;

            LogDevelopmentContractTrace(request, activeContractDocumentId, "Received");

            if (!request.AcceptCurrentContract)
            {
                return FailureWithDevelopmentTrace(
                    request,
                    activeContractDocumentId,
                    "ContractAcceptanceMissing",
                    ContractAcceptanceRequiredError);
            }

            if (activeContract is null)
            {
                return FailureWithDevelopmentTrace(
                    request,
                    activeContractDocumentId,
                    "ActiveContractMissing",
                    NoActiveContractError);
            }

            if (!request.SubmittedContractDocumentId.HasValue || request.SubmittedContractDocumentId.Value == Guid.Empty)
            {
                return FailureWithDevelopmentTrace(
                    request,
                    activeContractDocumentId,
                    "SubmittedContractDocumentIdMissing",
                    ContractValidationUnavailableError);
            }

            if (request.SubmittedContractDocumentId.Value != activeContract.Id)
            {
                return FailureWithDevelopmentTrace(
                    request,
                    activeContractDocumentId,
                    "SubmittedContractDocumentIdMismatch",
                    ContractChangedError);
            }

            var existingUser = await _userManager.FindByEmailAsync(email);
            if (existingUser is not null)
            {
                return FailureWithDevelopmentTrace(
                    request,
                    activeContractDocumentId,
                    "EmailAlreadyRegistered",
                    "El correo ya está registrado.");
            }

            var executionStrategy = _context.Database.CreateExecutionStrategy();
            TenantProvisioningResult? provisioningResult = null;
            var promotionalAccessApplied = false;

            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    foreach (var roleName in GetRegistrationRoles())
                    {
                        await EnsureRoleExistsAsync(roleName);
                    }

                    var tenant = new LuxuryApp.Models.SaaS.Tenant
                    {
                        Nombre = BuildTenantName(name, email),
                        Activo = true,
                        FechaCreacion = DateTime.UtcNow,
                        CommercialAccessMode = request.RequiresEmailConfirmation
                            ? TenantCommercialAccessMode.PendingVerification
                            : TenantCommercialAccessMode.RequiresSubscription,
                        CommercialNotes = request.RequiresEmailConfirmation
                            ? "Registro pendiente de confirmacion de correo."
                            : null,
                        CommercialUpdatedUtc = request.RequiresEmailConfirmation
                            ? DateTime.UtcNow
                            : null
                    };

                    _context.Tenants.Add(tenant);
                    await _context.SaveChangesAsync(cancellationToken);

                    var initialSubscriptionCreated = await CreateInitialSubscriptionAsync(tenant.Id, cancellationToken);

                    var usuario = new AppUsuario
                    {
                        UserName = email,
                        Email = email,
                        Name = name,
                        PhoneNumber = phoneNumber,
                        State = true,
                        EmailConfirmed = !request.RequiresEmailConfirmation,
                        TenantId = tenant.Id
                    };

                    var createUserResult = await _userManager.CreateAsync(usuario, request.Password);
                    if (!createUserResult.Succeeded)
                    {
                        var errors = createUserResult.Errors.Select(error => error.Description).ToArray();
                        LogDevelopmentProvisioningFailure(
                            request,
                            activeContractDocumentId,
                            "IdentityUserCreationFailed",
                            errors);
                        provisioningResult = TenantProvisioningResult.Failure(errors);
                        await transaction.RollbackAsync(cancellationToken);
                        return;
                    }

                    foreach (var roleName in GetRegistrationRoles())
                    {
                        var addToRoleResult = await _userManager.AddToRoleAsync(usuario, roleName);
                        if (!addToRoleResult.Succeeded)
                        {
                            var errors = addToRoleResult.Errors.Select(error => error.Description).ToArray();
                            LogDevelopmentProvisioningFailure(
                                request,
                                activeContractDocumentId,
                                "IdentityRoleAssignmentFailed",
                                errors);
                            provisioningResult = TenantProvisioningResult.Failure(errors);
                            await transaction.RollbackAsync(cancellationToken);
                            return;
                        }
                    }

                    try
                    {
                        // Registration and contract evidence are committed atomically.
                        await _contractService.RegisterAcceptanceAsync(
                            usuario.Id,
                            request.SubmittedContractDocumentId.Value,
                            ContractAcceptanceSources.Register,
                            request.ContractIpAddress,
                            request.ContractUserAgent,
                            cancellationToken);
                    }
                    catch (InvalidOperationException ex)
                    {
                        var contractError = await ResolveContractRegistrationErrorAsync(
                            request,
                            ex.Message,
                            cancellationToken);
                        var errors = new[] { contractError };
                        LogDevelopmentProvisioningFailure(
                            request,
                            activeContractDocumentId,
                            "ContractAcceptanceRegistrationFailed",
                            errors);
                        provisioningResult = TenantProvisioningResult.Failure(errors);
                        await transaction.RollbackAsync(cancellationToken);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(accessCode))
                    {
                        var redemptionResult = await _promotionalCodeService.RedeemAsync(
                            accessCode,
                            tenant.Id,
                            usuario,
                            cancellationToken);

                        if (!redemptionResult.Succeeded)
                        {
                            var errors = new[]
                            {
                                redemptionResult.Error ?? "No fue posible aplicar el código de acceso."
                            };
                            LogDevelopmentProvisioningFailure(
                                request,
                                activeContractDocumentId,
                                "PromotionalCodeRedemptionFailed",
                                errors);
                            provisioningResult = TenantProvisioningResult.Failure(errors);
                            await transaction.RollbackAsync(cancellationToken);
                            return;
                        }

                        promotionalAccessApplied = true;
                    }

                    await transaction.CommitAsync(cancellationToken);

                    var access = await _commercialAccessResolver.ResolveAsync(
                        tenant.Id,
                        usuario,
                        cancellationToken);

                    _logger.LogInformation(
                        "Tenant provisionado correctamente. TenantId {TenantId}. UserId {UserId}. InitialSubscriptionCreated {InitialSubscriptionCreated}. PromotionalAccessApplied {PromotionalAccessApplied}.",
                        tenant.Id,
                        usuario.Id,
                        initialSubscriptionCreated,
                        promotionalAccessApplied);

                    provisioningResult = TenantProvisioningResult.Success(
                        usuario,
                        tenant.Id,
                        initialSubscriptionCreated,
                        promotionalAccessApplied,
                        request.RequiresEmailConfirmation,
                        requiresPlanSelection: !access.CanAccessApp);
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            });

            return provisioningResult
                ?? throw new InvalidOperationException("No se pudo completar el aprovisionamiento del tenant.");
        }

        private TenantProvisioningResult FailureWithDevelopmentTrace(
            TenantRegistrationRequest request,
            Guid? activeContractDocumentId,
            string reason,
            params string[] errors)
        {
            LogDevelopmentProvisioningFailure(request, activeContractDocumentId, reason, errors);
            return TenantProvisioningResult.Failure(errors);
        }

        private async Task<string> ResolveContractRegistrationErrorAsync(
            TenantRegistrationRequest request,
            string fallbackError,
            CancellationToken cancellationToken)
        {
            if (!request.SubmittedContractDocumentId.HasValue || request.SubmittedContractDocumentId.Value == Guid.Empty)
            {
                return ContractValidationUnavailableError;
            }

            var activeContract = await _contractService.GetActiveContractAsync(cancellationToken);
            if (activeContract is null)
            {
                return NoActiveContractError;
            }

            return activeContract.Id == request.SubmittedContractDocumentId.Value
                ? fallbackError
                : ContractChangedError;
        }

        private void LogDevelopmentProvisioningFailure(
            TenantRegistrationRequest request,
            Guid? activeContractDocumentId,
            string reason,
            IReadOnlyCollection<string> errors)
        {
            LogDevelopmentContractTrace(request, activeContractDocumentId, "Failure", reason, errors);
        }

        private void LogDevelopmentContractTrace(
            TenantRegistrationRequest request,
            Guid? activeContractDocumentId,
            string stage,
            string? reason = null,
            IReadOnlyCollection<string>? errors = null)
        {
            if (!_environment.IsDevelopment())
            {
                return;
            }

            var submittedContractDocumentId = request.SubmittedContractDocumentId;
            var hasActiveContract = activeContractDocumentId.HasValue && activeContractDocumentId.Value != Guid.Empty;
            var contractDocumentIdsMatch = hasActiveContract &&
                submittedContractDocumentId.HasValue &&
                submittedContractDocumentId.Value != Guid.Empty &&
                submittedContractDocumentId.Value == activeContractDocumentId.GetValueOrDefault();

            _logger.LogInformation(
                "Tenant provisioning contract trace. Stage {Stage}. AcceptCurrentContract {AcceptCurrentContract}. SubmittedContractDocumentId {SubmittedContractDocumentId}. HasActiveContract {HasActiveContract}. ActiveContractDocumentId {ActiveContractDocumentId}. ContractDocumentIdsMatch {ContractDocumentIdsMatch}. FailureReason {FailureReason}. ErrorsReturned {ErrorsReturned}.",
                stage,
                request.AcceptCurrentContract,
                submittedContractDocumentId,
                hasActiveContract,
                activeContractDocumentId,
                contractDocumentIdsMatch,
                reason,
                errors ?? Array.Empty<string>());
        }

        private async Task<bool> CreateInitialSubscriptionAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            if (!_options.CreateInitialSubscription)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(_options.InitialPlanName))
            {
                throw new InvalidOperationException(
                    "TenantOnboarding:InitialPlanName es obligatorio cuando CreateInitialSubscription es true.");
            }

            var plan = await _context.Planes
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    p => p.Activo && p.Nombre == _options.InitialPlanName,
                    cancellationToken);

            if (plan is null)
            {
                throw new InvalidOperationException(
                    $"No existe un plan activo con nombre '{_options.InitialPlanName}' para el onboarding del tenant.");
            }

            var now = DateTime.UtcNow;
            var subscription = new Suscripcion
            {
                TenantId = tenantId,
                PlanId = plan.Id,
                Proveedor = PaymentProviderType.None,
                Estado = _options.InitialSubscriptionState,
                FechaInicio = now,
                FechaUltimaActualizacionUtc = now,
                MotivoEstado = "Suscripción inicial creada durante el registro."
            };

            if (_options.InitialSubscriptionState == EstadoSuscripcion.Trial)
            {
                if (_options.TrialDays <= 0)
                {
                    throw new InvalidOperationException(
                        "TenantOnboarding:TrialDays debe ser mayor que cero cuando el estado inicial es Trial.");
                }

                subscription.FechaTrialFin = now.AddDays(_options.TrialDays);
            }

            _context.Suscripciones.Add(subscription);
            await _context.SaveChangesAsync(cancellationToken);

            return true;
        }

        private IEnumerable<string> GetRegistrationRoles()
        {
            yield return GetRequiredRoleName(_options.RegistrationRole, nameof(_options.RegistrationRole));

            if (_options.AddRegisteredRole)
            {
                var registeredRole = GetRequiredRoleName(_options.RegisteredRole, nameof(_options.RegisteredRole));
                if (!string.Equals(registeredRole, _options.RegistrationRole, StringComparison.OrdinalIgnoreCase))
                {
                    yield return registeredRole;
                }
            }
        }

        private static string GetRequiredRoleName(string? value, string optionName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"La opción '{optionName}' es obligatoria.");
            }

            return value.Trim();
        }

        private async Task EnsureRoleExistsAsync(string roleName)
        {
            if (await _roleManager.RoleExistsAsync(roleName))
            {
                return;
            }

            var result = await _roleManager.CreateAsync(new IdentityRole(roleName));
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"No se pudo preparar el rol '{roleName}' para el onboarding del tenant.");
            }

            _logger.LogWarning("El rol {RoleName} no existía y fue creado automáticamente durante el onboarding.", roleName);
        }

        private static string BuildTenantName(string name, string email)
        {
            var tenantName = string.IsNullOrWhiteSpace(name) ? email : name;
            return tenantName.Length <= 150
                ? tenantName
                : tenantName[..150];
        }
    }
}
