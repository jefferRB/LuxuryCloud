using System.Security.Claims;
using System.Text;
using LuxuryApp.Filters;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Account;
using LuxuryApp.Services.Contracts;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.PublicSite;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.Identity
{
    public class AccountsController : Controller
    {
        private readonly UserManager<AppUsuario> _userManager;
        private readonly SignInManager<AppUsuario> _signInManager;
        private readonly ApplicationDbContext _context;
        private readonly TenantProvisioningService _tenantProvisioningService;
        private readonly IContractService _contractService;
        private readonly IPublicSiteContentService _publicSiteContentService;
        private readonly TilopayRepeatOptions _tilopayRepeatOptions;
        private readonly IWebHostEnvironment _environment;
        private readonly IAccountEmailService _accountEmailService;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;
        private readonly RegistrationSecurityService _registrationSecurityService;
        private readonly TurnstileVerificationService _turnstileVerificationService;
        private readonly RegistrationSecurityOptions _registrationSecurityOptions;
        private readonly ITenantCommercialAccessCache _accessCache;
        private readonly LuxuryApp.Services.Asociados.IPostLoginDestinationService _postLoginDestinationService;
        private readonly IAccountSettingsPageBuilder _accountSettingsPageBuilder;
        private readonly ILogger<AccountsController> _logger;

        public AccountsController(
            UserManager<AppUsuario> userManager,
            SignInManager<AppUsuario> signInManager,
            ApplicationDbContext context,
            TenantProvisioningService tenantProvisioningService,
            IContractService contractService,
            IPublicSiteContentService publicSiteContentService,
            IOptions<TilopayRepeatOptions> tilopayRepeatOptions,
            IWebHostEnvironment environment,
            IAccountEmailService accountEmailService,
            ITenantDisplayNameService tenantDisplayNameService,
            RegistrationSecurityService registrationSecurityService,
            TurnstileVerificationService turnstileVerificationService,
            IOptions<RegistrationSecurityOptions> registrationSecurityOptions,
            ITenantCommercialAccessCache accessCache,
            LuxuryApp.Services.Asociados.IPostLoginDestinationService postLoginDestinationService,
            IAccountSettingsPageBuilder accountSettingsPageBuilder,
            ILogger<AccountsController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _context = context;
            _tenantProvisioningService = tenantProvisioningService;
            _contractService = contractService;
            _publicSiteContentService = publicSiteContentService;
            _tilopayRepeatOptions = tilopayRepeatOptions.Value;
            _environment = environment;
            _accountEmailService = accountEmailService;
            _tenantDisplayNameService = tenantDisplayNameService;
            _registrationSecurityService = registrationSecurityService;
            _turnstileVerificationService = turnstileVerificationService;
            _registrationSecurityOptions = registrationSecurityOptions.Value;
            _accessCache = accessCache;
            _postLoginDestinationService = postLoginDestinationService;
            _accountSettingsPageBuilder = accountSettingsPageBuilder;
            _logger = logger;
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Index() => View();

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Registro(
            string? returnurl = null,
            Guid? selectedPlanId = null,
            CancellationToken cancellationToken = default)
        {
            ViewData["ReturnUrl"] = returnurl;
            ViewData["SelectedPlanName"] = await _publicSiteContentService.GetPlanNameAsync(selectedPlanId, cancellationToken);
            PopulateRegistrationSecurityViewData();
            var model = new RegistroViewModel
            {
                SelectedPlanId = selectedPlanId
            };

            await PopulateCurrentContractAsync(model, cancellationToken);
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        [EnableRateLimiting("Registration")]
        public async Task<IActionResult> Registro(
            RegistroViewModel model,
            string? returnurl = null,
            CancellationToken cancellationToken = default)
        {
            ViewData["ReturnUrl"] = returnurl;
            ViewData["SelectedPlanName"] = await _publicSiteContentService.GetPlanNameAsync(model.SelectedPlanId, cancellationToken);
            PopulateRegistrationSecurityViewData();
            var clientIp = ContractRequestMetadataResolver.ResolveClientIp(HttpContext);
            var normalizedEmail = RegistrationSecurityService.NormalizeEmail(model.Email);
            var registrationRateLimit = _registrationSecurityService.CheckRateLimit(
                RegistrationSecurityActions.Registration,
                clientIp,
                normalizedEmail);

            if (!registrationRateLimit.Allowed)
            {
                return RateLimitedView(model);
            }

            if (Request.HasFormContentType && _registrationSecurityService.IsHoneypotTriggered(Request.Form))
            {
                _logger.LogWarning(
                    "Registro rechazado por honeypot. TraceIdentifier {TraceIdentifier}. IpPresent {IpPresent}. MaskedEmail {MaskedEmail}.",
                    HttpContext.TraceIdentifier,
                    !string.IsNullOrWhiteSpace(clientIp),
                    SensitiveDataMasker.MaskEmail(model.Email));

                return RedirectToAction(nameof(ConfirmacionRegistro));
            }

            model.Email = normalizedEmail;
            model.Name = RegistrationSecurityService.NormalizeBusinessName(model.Name);

            var currentContractDocumentIdBeforePopulate = model.CurrentContractDocumentId;
            var contractAcceptanceFieldName = nameof(RegistroViewModel.AcceptCurrentContract);
            var postedFormKeys = Request.HasFormContentType
                ? Request.Form.Keys.ToArray()
                : Array.Empty<string>();
            var postedCheckboxValues = ContractAcceptanceBindingHelper.GetSubmittedValues(
                Request,
                contractAcceptanceFieldName);
            var boundContractAcceptanceValue = model.AcceptCurrentContract;

            await PopulateCurrentContractAsync(model, cancellationToken);
            ModelState.Remove(nameof(RegistroViewModel.CurrentContractDocumentId));
            var normalizedContractAcceptanceValue = NormalizeContractAcceptance(model);
            var submittedContractDocumentId = model.CurrentContractDocumentId;
            var safeReturnUrl = Url.IsLocalUrl(returnurl)
                ? returnurl!
                : Url.Content("~/") ?? "/";

            if (!model.HasCurrentContract)
            {
                ModelState.AddModelError(string.Empty, "No hay un contrato vigente configurado en este momento. Contacta soporte.");
            }

            var registrationValidation = _registrationSecurityService.ValidateRegistration(model.Email, model.Name);
            foreach (var error in registrationValidation.Errors)
            {
                ModelState.AddModelError(error.FieldName, error.Message);
            }

            if (!await ValidateTurnstileAsync(cancellationToken))
            {
                ModelState.AddModelError(
                    string.Empty,
                    "No pudimos validar la proteccion anti-abuso. Intenta nuevamente.");
            }

            LogDevelopmentRegistrationContractTrace(
                contractAcceptanceFieldName,
                postedFormKeys,
                postedCheckboxValues,
                boundContractAcceptanceValue,
                normalizedContractAcceptanceValue,
                currentContractDocumentIdBeforePopulate,
                model.CurrentContractDocumentId,
                submittedContractDocumentId,
                model.HasCurrentContract);

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            try
            {
                var provisioning = await _tenantProvisioningService.RegisterAsync(
                    new TenantRegistrationRequest
                    {
                        Email = model.Email,
                        Password = model.Password,
                        Name = model.Name,
                        PhoneNumber = model.PhoneNumber,
                        AccessCode = model.AccessCode,
                        AcceptCurrentContract = model.AcceptCurrentContract,
                        RequiresEmailConfirmation = _registrationSecurityOptions.RequireEmailConfirmation,
                        SubmittedContractDocumentId = model.CurrentContractDocumentId,
                        ContractIpAddress = ContractRequestMetadataResolver.ResolveClientIp(HttpContext),
                        ContractUserAgent = ContractRequestMetadataResolver.ResolveUserAgent(HttpContext)
                    },
                    cancellationToken);

                if (!provisioning.Succeeded || provisioning.User is null)
                {
                    LogDevelopmentProvisioningFailure(provisioning.Errors);

                    foreach (var error in provisioning.Errors)
                    {
                        ModelState.AddModelError(string.Empty, error);
                    }

                    return View(model);
                }

                if (provisioning.RequiresEmailConfirmation)
                {
                    await SendRegistrationConfirmationEmailAsync(
                        provisioning.User,
                        model.SelectedPlanId,
                        cancellationToken);

                    return RedirectToAction(nameof(ConfirmacionRegistro));
                }

                await _signInManager.SignInAsync(provisioning.User, isPersistent: false);

                if (provisioning.RequiresPlanSelection)
                {
                    if (model.SelectedPlanId.HasValue && model.SelectedPlanId.Value != Guid.Empty)
                    {
                        var selectedPlan = await _publicSiteContentService.FindAvailablePlanAsync(
                            model.SelectedPlanId.Value,
                            cancellationToken);
                        var selectedRepeatPlan = selectedPlan is null
                            ? null
                            : _tilopayRepeatOptions.FindRegistrationByCode(selectedPlan.Codigo);

                        if (ShouldAutoContinueToCheckout(selectedRepeatPlan))
                        {
                            _logger.LogInformation(
                                "Registro completado con plan preseleccionado listo para checkout recurrente. TenantId {TenantId}. UserId {UserId}. PlanId {PlanId}. PlanCode {PlanCode}.",
                                provisioning.TenantId,
                                provisioning.User.Id,
                                selectedPlan!.Id,
                                selectedPlan.Codigo ?? selectedPlan.Nombre);

                            return RedirectToAction(
                                "ContinuarCheckout",
                                "Billing",
                                new { planId = selectedPlan.Id });
                        }
                    }

                    return RedirectToAction("Planes", "Billing", new { selectedPlanId = model.SelectedPlanId });
                }

                return LocalRedirect(safeReturnUrl);
            }
            catch (Exception ex)
            {
                var correlationId = HttpContext.TraceIdentifier;

                _logger.LogError(
                    ex,
                    "Error en registro SaaS. CorrelationId {CorrelationId}. MaskedEmail {MaskedEmail}.",
                    correlationId,
                    SensitiveDataMasker.MaskEmail(model.Email));

                ModelState.AddModelError(
                    string.Empty,
                    $"No fue posible crear la cuenta. Si el problema continúa, comparte este código con soporte: {correlationId}");

                return View(model);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ConfirmacionRegistro() => View();

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ConfirmarEmail(
            string? userId,
            string? token,
            Guid? selectedPlanId = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
            {
                return EmailConfirmationView(success: false);
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return EmailConfirmationView(success: false);
            }

            IdentityResult result;
            if (user.EmailConfirmed)
            {
                result = IdentityResult.Success;
            }
            else
            {
                string decodedToken;
                try
                {
                    decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    _logger.LogWarning(
                        ex,
                        "Token de confirmacion de correo con formato invalido. UserId {UserId}. TenantId {TenantId}.",
                        user.Id,
                        user.TenantId);

                    return EmailConfirmationView(success: false);
                }

                result = await _userManager.ConfirmEmailAsync(user, decodedToken);
            }

            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Confirmacion de correo fallida. UserId {UserId}. TenantId {TenantId}.",
                    user.Id,
                    user.TenantId);

                return EmailConfirmationView(success: false);
            }

            await MarkPendingTenantAsEmailConfirmedAsync(user, cancellationToken);
            await _signInManager.SignInAsync(user, isPersistent: false);

            TempData["Mensaje"] = "Correo confirmado. Ya puedes continuar con tu plan.";

            if (selectedPlanId.HasValue && selectedPlanId.Value != Guid.Empty)
            {
                return RedirectToAction(
                    "ContinuarCheckout",
                    "Billing",
                    new { planId = selectedPlanId.Value });
            }

            return RedirectToAction("Planes", "Billing");
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Acceso(string? returnurl = null)
        {
            ViewData["ReturnUrl"] = returnurl;
            // El checkbox aparece marcado por defecto: en un dispositivo de confianza la
            // sesión persiste. El usuario puede desmarcarlo para una cookie de sesión.
            return View(new AccesoViewModel { RememberMe = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        [EnableRateLimiting("Authentication")]
        public async Task<IActionResult> Acceso(
            AccesoViewModel model,
            string? returnurl = null,
            CancellationToken cancellationToken = default)
        {
            ViewData["ReturnUrl"] = returnurl;
            var safeReturnUrl = Url.IsLocalUrl(returnurl)
                ? returnurl!
                : Url.Content("~/") ?? "/";

            var loginRateLimit = _registrationSecurityService.CheckRateLimit(
                RegistrationSecurityActions.Login,
                ContractRequestMetadataResolver.ResolveClientIp(HttpContext),
                model.Email);

            if (!loginRateLimit.Allowed)
            {
                return RateLimitedView(model);
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var usuario = await _userManager.FindByEmailAsync(model.Email.Trim());

            if (usuario == null)
            {
                await Task.Delay(500);
                ModelState.AddModelError(string.Empty, "No pudimos iniciar sesión con esos datos. Revisa tu correo y contraseña.");
                return View(model);
            }

            var result = await _signInManager.CheckPasswordSignInAsync(
                usuario,
                model.Password,
                lockoutOnFailure: true);

            if (result.Succeeded)
            {
                if (await RequiresEmailConfirmationAsync(usuario, cancellationToken))
                {
                    _logger.LogWarning(
                        "Intento de acceso con correo no confirmado. UserId {UserId}. TenantId {TenantId}.",
                        usuario.Id,
                        usuario.TenantId);

                    await Task.Delay(250, cancellationToken);
                    ModelState.AddModelError(
                        string.Empty,
                        "Debes confirmar tu correo antes de continuar. Revisa tu bandeja de entrada.");
                    return View(model);
                }

                if (!usuario.State)
                {
                    _logger.LogWarning(
                        "Intento de acceso con usuario deshabilitado. UserId {UserId}. TenantId {TenantId}.",
                        usuario.Id,
                        usuario.TenantId);

                    await Task.Delay(250);
                    ModelState.AddModelError(string.Empty, "Tu cuenta no estÃ¡ disponible en este momento.");
                    return View(model);
                }

                var tenantDisponible = usuario.TenantId != Guid.Empty &&
                    await _context.Tenants
                        .AsNoTracking()
                        .AnyAsync(t =>
                            t.Id == usuario.TenantId &&
                            (usuario.IsPlatformSuperAdmin || t.Activo));

                if (!tenantDisponible)
                {
                    _logger.LogError(
                        "Intento de acceso con usuario sin tenant válido. UserId {UserId}. TenantId {TenantId}.",
                        usuario.Id,
                        usuario.TenantId);

                    await Task.Delay(250);
                    ModelState.AddModelError(string.Empty, "Tu cuenta no está disponible en este momento.");
                    return View(model);
                }

                if (usuario.TwoFactorEnabled)
                {
                    // La contraseña solo completa el paso 1: la cookie de aplicación se emite
                    // hasta que VerificarCodigo valide el TOTP (o un código de recuperación).
                    await IniciarPasoDosFactoresAsync(usuario);
                    return RedirectToAction(
                        nameof(VerificarCodigo),
                        new { returnurl = safeReturnUrl, rememberMe = model.RememberMe });
                }

                await _signInManager.SignInAsync(usuario, model.RememberMe);
                return await CompletarAccesoAsync(usuario, safeReturnUrl);
            }

            if (result.IsLockedOut)
            {
                return View("Bloqueado");
            }

            ModelState.AddModelError(string.Empty, "No pudimos iniciar sesión con esos datos. Revisa tu correo y contraseña.");
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowWithoutMfaEnrollment]
        public async Task<IActionResult> SalirAplicacion()
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction(nameof(HomeController.Index), "Home");
        }

        [HttpGet]
        [AllowAnonymous]
        [AllowWithoutMfaEnrollment]
        public async Task<IActionResult> VerificarCodigo(string? returnurl = null, bool rememberMe = false)
        {
            var usuario = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (usuario is null)
            {
                return RedirectToAction(nameof(Acceso));
            }

            return View(new VerificarCodigoViewModel { ReturnUrl = returnurl, RememberMe = rememberMe });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        [AllowWithoutMfaEnrollment]
        public async Task<IActionResult> VerificarCodigo(VerificarCodigoViewModel model)
        {
            var usuario = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (usuario is null)
            {
                return RedirectToAction(nameof(Acceso));
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var safeReturnUrl = Url.IsLocalUrl(model.ReturnUrl)
                ? model.ReturnUrl!
                : Url.Content("~/") ?? "/";

            var codigo = model.Codigo.Replace(" ", string.Empty).Replace("-", string.Empty);
            var result = await _signInManager.TwoFactorAuthenticatorSignInAsync(
                codigo,
                model.RememberMe,
                rememberClient: false);

            if (result.Succeeded)
            {
                return await CompletarAccesoAsync(usuario, safeReturnUrl);
            }

            if (result.IsLockedOut)
            {
                return View("Bloqueado");
            }

            await Task.Delay(250);
            ModelState.AddModelError(
                string.Empty,
                "El código no es válido. Genera uno nuevo en tu aplicación e intenta otra vez.");
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        [AllowWithoutMfaEnrollment]
        public async Task<IActionResult> UsarCodigoRecuperacion(CodigoRecuperacionViewModel model)
        {
            var usuario = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (usuario is null)
            {
                return RedirectToAction(nameof(Acceso));
            }

            var safeReturnUrl = Url.IsLocalUrl(model.ReturnUrl)
                ? model.ReturnUrl!
                : Url.Content("~/") ?? "/";

            if (!ModelState.IsValid)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "El código de recuperación no es válido o ya fue usado.");
                return View(nameof(VerificarCodigo), new VerificarCodigoViewModel { ReturnUrl = model.ReturnUrl });
            }

            var codigo = model.Codigo.Replace(" ", string.Empty).Trim();
            var result = await _signInManager.TwoFactorRecoveryCodeSignInAsync(codigo);

            if (result.Succeeded)
            {
                await AuditarCodigoRecuperacionAsync(usuario);
                return await CompletarAccesoAsync(usuario, safeReturnUrl);
            }

            if (result.IsLockedOut)
            {
                return View("Bloqueado");
            }

            await Task.Delay(250);
            ModelState.AddModelError(
                string.Empty,
                "El código de recuperación no es válido o ya fue usado.");
            return View(nameof(VerificarCodigo), new VerificarCodigoViewModel { ReturnUrl = model.ReturnUrl });
        }

        /// <summary>
        /// Pasos posteriores a la emisión de la cookie de aplicación: portal de funcionario,
        /// gate de contrato y redirect final. Compartido por el login con contraseña y por la
        /// verificación TOTP/código de recuperación.
        /// </summary>
        private async Task<IActionResult> CompletarAccesoAsync(AppUsuario usuario, string safeReturnUrl)
        {
            // Funcionario: portal limitado. No es la parte contratante del SaaS,
            // por lo que se omite el gate de contrato y se le envía a su portal.
            var esFuncionario = await _userManager.IsInRoleAsync(usuario, AppRoles.Funcionario);
            var esAdministrador = await _userManager.IsInRoleAsync(usuario, AppRoles.Administrador);
            if (esFuncionario && !esAdministrador)
            {
                return safeReturnUrl.StartsWith("/MiPortal", StringComparison.OrdinalIgnoreCase)
                    ? LocalRedirect(safeReturnUrl)
                    : Redirect("/MiPortal");
            }

            // Asociado: tampoco es la parte contratante y, sobre todo, puede no tener acceso al
            // Dashboard. Se le envía al primer módulo que sí puede abrir, respetando el returnUrl
            // solo cuando lleva a un lugar permitido.
            var esAsociado = await _userManager.IsInRoleAsync(usuario, AppRoles.Asociado);
            if (esAsociado && !esAdministrador)
            {
                var principal = await _signInManager.CreateUserPrincipalAsync(usuario);

                if (EsRetornoExplicito(safeReturnUrl) &&
                    await _postLoginDestinationService.PuedeAbrirAsync(principal, safeReturnUrl))
                {
                    return LocalRedirect(safeReturnUrl);
                }

                return LocalRedirect(await _postLoginDestinationService.ResolveAsync(principal));
            }

            var contractStatus = await _contractService.GetAcceptanceStatusAsync(usuario.Id);
            if (contractStatus.BlocksApplicationAccess)
            {
                return RedirectToAction("Reaccept", "Contract", new { returnurl = safeReturnUrl });
            }

            return LocalRedirect(safeReturnUrl);
        }

        /// <summary>
        /// True si el <c>returnUrl</c> apunta a algún lugar concreto (y no a la raíz, que es el
        /// valor por defecto cuando el usuario simplemente entró a iniciar sesión).
        /// </summary>
        private static bool EsRetornoExplicito(string safeReturnUrl) =>
            !string.IsNullOrWhiteSpace(safeReturnUrl) &&
            safeReturnUrl.TrimEnd('/') is not ("" or "~");

        /// <summary>
        /// Emite la cookie intermedia de dos factores con el mismo formato interno que usa
        /// SignInManager: GetTwoFactorAuthenticationUserAsync lee el userId del claim Name.
        /// </summary>
        private async Task IniciarPasoDosFactoresAsync(AppUsuario usuario)
        {
            var identity = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
            identity.AddClaim(new Claim(ClaimTypes.Name, usuario.Id));
            await HttpContext.SignInAsync(
                IdentityConstants.TwoFactorUserIdScheme,
                new ClaimsPrincipal(identity));
        }

        /// <summary>
        /// El uso de un código de recuperación de un superadmin queda en la bitácora de
        /// plataforma. Se resuelve por RequestServices para no ampliar el constructor de un
        /// controlador público; TryLogAsync nunca lanza (el fallo queda en el log, S6).
        /// </summary>
        private Task AuditarCodigoRecuperacionAsync(AppUsuario usuario)
        {
            if (!usuario.IsPlatformSuperAdmin)
            {
                return Task.CompletedTask;
            }

            var auditService = HttpContext.RequestServices.GetRequiredService<IPlatformAuditService>();
            return auditService.TryLogAsync(new PlatformAuditEntry
            {
                Action = PlatformAuditActions.MfaRecoveryCodeUsed,
                EntityType = PlatformAuditEntityTypes.User,
                EntityId = usuario.Id,
                TargetUserId = usuario.Id,
                TargetUserEmail = usuario.Email
            });
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult OlvidoPassword() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        [EnableRateLimiting("PasswordReset")]
        public async Task<IActionResult> OlvidoPassword(
            OlvidoPasswordViewModel model,
            CancellationToken cancellationToken)
        {
            var resetRateLimit = _registrationSecurityService.CheckRateLimit(
                RegistrationSecurityActions.ForgotPassword,
                ContractRequestMetadataResolver.ResolveClientIp(HttpContext),
                model.Email);

            if (!resetRateLimit.Allowed)
            {
                return RateLimitedView(model);
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var user = await _userManager.FindByEmailAsync(model.Email.Trim());
            if (user is not null)
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
                var resetUrl = Url.Action(
                    nameof(ResetPassword),
                    "Accounts",
                    new { userId = user.Id, token = encodedToken },
                    Request.Scheme)!;

                _logger.LogInformation(
                    "OlvidoPassword solicitado. UserId: {UserId}.",
                    user.Id);

                try
                {
                    await _accountEmailService.SendPasswordResetEmailAsync(
                        user.Email!,
                        user.Name ?? user.UserName ?? "Usuario",
                        resetUrl,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error al enviar email OlvidoPassword. UserId: {UserId}.",
                        user.Id);
                }
            }

            return RedirectToAction(nameof(ConfirmacionOlvidoPassword));
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ConfirmacionOlvidoPassword() => View();

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ResetPassword(string? userId, string? token)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
            {
                return RedirectToAction(nameof(Acceso));
            }

            return View(new ResetPasswordViewModel { UserId = userId, Token = token });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowAnonymous]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var user = await _userManager.FindByIdAsync(model.UserId);
            if (user is null)
            {
                TempData["Mensaje"] = "Contraseña actualizada. Ingresa con tu nueva contraseña.";
                return RedirectToAction(nameof(Acceso));
            }

            var decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(model.Token));
            var result = await _userManager.ResetPasswordAsync(user, decodedToken, model.Password);

            if (result.Succeeded)
            {
                TempData["Mensaje"] = "Contraseña actualizada correctamente. Ingresa con tu nueva contraseña.";
                return RedirectToAction(nameof(Acceso));
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        /// <summary>
        /// Mi cuenta: perfil, seguridad (incluida la doble autenticación), preferencias y, para el
        /// dueño del negocio, la configuración fiscal.
        ///
        /// <para>
        /// La página se LEE como un todo pero se ESCRIBE por partes: cada sección conserva su
        /// propio POST. Ver <see cref="AccountSettingsPageViewModel"/>.
        /// </para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Cuenta(CancellationToken cancellationToken)
        {
            var page = await _accountSettingsPageBuilder.BuildAsync(User, cancellationToken);
            if (page is null)
            {
                return RedirectToAction(nameof(Acceso));
            }

            return View(page);
        }

        /// <summary>
        /// Guarda SOLO el perfil personal. No toca preferencias, ni IVA, ni 2FA: cada uno tiene su
        /// propia acción, así un fallo en una no arrastra a las demás.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cuenta(CuentaViewModel model, CancellationToken cancellationToken)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Acceso));
            }

            var normalizedName = _tenantDisplayNameService.NormalizeDisplayName(model.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                ModelState.AddModelError(nameof(CuentaViewModel.Name), "El nombre es requerido.");
            }

            const string invalidDisplayNameMessage = "El nombre no puede contener saltos de línea ni caracteres de control.";
            var alreadyHasInvalidCharacterError =
                ModelState.TryGetValue(nameof(CuentaViewModel.Name), out var nameState) &&
                nameState.Errors.Any(error => error.ErrorMessage == invalidDisplayNameMessage);

            if (_tenantDisplayNameService.ContainsInvalidDisplayNameCharacters(model.Name) &&
                !alreadyHasInvalidCharacterError)
            {
                ModelState.AddModelError(
                    nameof(CuentaViewModel.Name),
                    invalidDisplayNameMessage);
            }

            if (!ModelState.IsValid)
            {
                // Se repinta la página completa con el ModelState de ESTE request; el correo
                // vuelve del servidor (nunca del formulario) y se conserva lo que el usuario
                // escribió para que no tenga que teclearlo otra vez.
                var conErrores = await _accountSettingsPageBuilder.BuildAsync(User, cancellationToken);
                if (conErrores is null)
                {
                    return RedirectToAction(nameof(Acceso));
                }

                conErrores.Profile = new CuentaViewModel
                {
                    Email = user.Email ?? string.Empty,
                    Name = normalizedName.Length > 0 ? normalizedName : model.Name,
                    PhoneNumber = model.PhoneNumber
                };
                conErrores.FocusSection = AccountSettingsSections.Perfil;

                return View(conErrores);
            }

            user.Name = normalizedName;
            user.PhoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber)
                ? null
                : model.PhoneNumber.Trim();

            var result = await _userManager.UpdateAsync(user);
            if (result.Succeeded)
            {
                await _signInManager.RefreshSignInAsync(user);
                TempData["Mensaje"] = "Datos actualizados correctamente.";
            }
            else
            {
                TempData["Error"] = "No se pudieron guardar los cambios. Intenta de nuevo.";
            }

            return RedirectToAction(nameof(Cuenta));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnviarEnlaceCambioPassword(CancellationToken cancellationToken)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Acceso));
            }

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var resetUrl = Url.Action(
                nameof(ResetPassword),
                "Accounts",
                new { userId = user.Id, token = encodedToken },
                Request.Scheme)!;

            _logger.LogInformation(
                "Cambio de contraseña solicitado desde Cuenta. UserId: {UserId}.",
                user.Id);

            try
            {
                await _accountEmailService.SendPasswordResetEmailAsync(
                    user.Email!,
                    user.Name ?? user.UserName ?? "Usuario",
                    resetUrl,
                    cancellationToken);
                TempData["Mensaje"] = "Te enviamos un enlace para cambiar tu contraseña. Revisa tu correo.";
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error al enviar email de cambio de contraseña. UserId: {UserId}.",
                    user.Id);
                TempData["Error"] = "No pudimos enviar el correo en este momento. Inténtalo nuevamente más tarde.";
            }

            return RedirectToAction(nameof(Cuenta));
        }

        [AllowAnonymous]
        [AllowWithoutMfaEnrollment]
        public IActionResult Bloqueado() => View();

        private async Task PopulateCurrentContractAsync(
            RegistroViewModel model,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(model);

            var activeContract = await _contractService.GetActiveContractAsync(cancellationToken);
            if (activeContract is null)
            {
                model.CurrentContractDocumentId = null;
                model.CurrentContractTitle = string.Empty;
                model.CurrentContractVersion = string.Empty;
                model.CurrentContractEffectiveFromUtc = null;
                return;
            }

            model.CurrentContractDocumentId = activeContract.Id;
            model.CurrentContractTitle = activeContract.Title;
            model.CurrentContractVersion = activeContract.VersionNumber;
            model.CurrentContractEffectiveFromUtc = activeContract.EffectiveFromUtc;
        }

        private void PopulateRegistrationSecurityViewData()
        {
            var siteKey = _turnstileVerificationService.IsEnabled &&
                !string.IsNullOrWhiteSpace(_turnstileVerificationService.SiteKey)
                    ? _turnstileVerificationService.SiteKey
                    : null;

            ViewData["TurnstileSiteKey"] = siteKey;
        }

        private async Task<bool> ValidateTurnstileAsync(CancellationToken cancellationToken)
        {
            if (!_turnstileVerificationService.IsEnabled)
            {
                return true;
            }

            var token = Request.HasFormContentType
                ? Request.Form[_turnstileVerificationService.ResponseFieldName].ToString()
                : null;

            try
            {
                return await _turnstileVerificationService.VerifyAsync(
                    token,
                    ContractRequestMetadataResolver.ResolveClientIp(HttpContext),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No fue posible validar Turnstile.");
                return false;
            }
        }

        private async Task SendRegistrationConfirmationEmailAsync(
            AppUsuario user,
            Guid? selectedPlanId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                throw new InvalidOperationException("No se puede enviar confirmacion sin email.");
            }

            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var confirmationUrl = Url.Action(
                nameof(ConfirmarEmail),
                "Accounts",
                new { userId = user.Id, token = encodedToken, selectedPlanId },
                Request.Scheme)!;

            await _accountEmailService.SendEmailConfirmationEmailAsync(
                user.Email,
                user.Name ?? user.UserName ?? "Usuario",
                confirmationUrl,
                cancellationToken);
        }

        private async Task MarkPendingTenantAsEmailConfirmedAsync(
            AppUsuario user,
            CancellationToken cancellationToken)
        {
            if (user.TenantId == Guid.Empty)
            {
                return;
            }

            var tenant = await _context.Tenants
                .FirstOrDefaultAsync(currentTenant => currentTenant.Id == user.TenantId, cancellationToken);

            if (tenant is null || tenant.CommercialAccessMode != TenantCommercialAccessMode.PendingVerification)
            {
                return;
            }

            tenant.CommercialAccessMode = TenantCommercialAccessMode.RequiresSubscription;
            tenant.CommercialNotes = "Correo confirmado; pendiente de seleccion/pago de plan.";
            tenant.CommercialUpdatedUtc = DateTime.UtcNow;
            tenant.CommercialUpdatedByUserId = user.Id;

            await _context.SaveChangesAsync(cancellationToken);
            _accessCache.Invalidate(user.TenantId);
        }

        private async Task<bool> RequiresEmailConfirmationAsync(
            AppUsuario user,
            CancellationToken cancellationToken)
        {
            if (user.EmailConfirmed || user.TenantId == Guid.Empty)
            {
                return false;
            }

            return await _context.Tenants
                .AsNoTracking()
                .AnyAsync(
                    tenant => tenant.Id == user.TenantId &&
                              tenant.CommercialAccessMode == TenantCommercialAccessMode.PendingVerification,
                    cancellationToken);
        }

        private IActionResult EmailConfirmationView(bool success)
        {
            ViewData["EmailConfirmationSucceeded"] = success;
            return View("ConfirmacionEmail");
        }

        private IActionResult RateLimitedView(object model)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ModelState.AddModelError(
                string.Empty,
                "Demasiados intentos. Espera unos minutos e intenta nuevamente.");

            return View(model);
        }

        private bool NormalizeContractAcceptance(RegistroViewModel model)
        {
            var fieldName = nameof(RegistroViewModel.AcceptCurrentContract);
            model.AcceptCurrentContract = Request.HasFormContentType
                ? ContractAcceptanceBindingHelper.IsAccepted(Request.Form, fieldName)
                : model.AcceptCurrentContract;

            ModelState.Remove(fieldName);

            if (!model.AcceptCurrentContract)
            {
                ModelState.AddModelError(
                    fieldName,
                    "Debes aceptar el contrato para crear tu cuenta.");
            }

            return model.AcceptCurrentContract;
        }

        private void LogDevelopmentRegistrationContractTrace(
            string fieldName,
            IReadOnlyCollection<string> postedFormKeys,
            IReadOnlyCollection<string> postedCheckboxValues,
            bool boundContractAcceptanceValue,
            bool normalizedContractAcceptanceValue,
            Guid? currentContractDocumentIdBeforePopulate,
            Guid? currentContractDocumentIdAfterPopulate,
            Guid? submittedContractDocumentId,
            bool hasCurrentContract)
        {
            if (!_environment.IsDevelopment())
            {
                return;
            }

            var contractRelatedKeys = postedFormKeys
                .Where(key => key.Contains("contract", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var modelStateErrors = ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .SelectMany(entry => entry.Value!.Errors.Select(error => $"{entry.Key}: {error.ErrorMessage}"))
                .ToArray();

            _logger.LogInformation(
                "Registro POST contract trace. TraceIdentifier {TraceIdentifier}. FormKeys {FormKeys}. ContractKeys {ContractKeys}. CheckboxField {CheckboxField}. RawCheckboxValues {RawCheckboxValues}. BoundViewModelValue {BoundViewModelValue}. NormalizedAcceptanceValue {NormalizedAcceptanceValue}. CurrentContractDocumentIdBeforePopulate {CurrentContractDocumentIdBeforePopulate}. CurrentContractDocumentIdAfterPopulate {CurrentContractDocumentIdAfterPopulate}. SubmittedContractDocumentIdFinal {SubmittedContractDocumentIdFinal}. HasCurrentContract {HasCurrentContract}. ModelStateIsValidBeforeRegister {ModelStateIsValidBeforeRegister}. ModelStateErrorsBeforeRegister {ModelStateErrorsBeforeRegister}.",
                HttpContext.TraceIdentifier,
                postedFormKeys,
                contractRelatedKeys,
                fieldName,
                postedCheckboxValues,
                boundContractAcceptanceValue,
                normalizedContractAcceptanceValue,
                currentContractDocumentIdBeforePopulate,
                currentContractDocumentIdAfterPopulate,
                submittedContractDocumentId,
                hasCurrentContract,
                ModelState.IsValid,
                modelStateErrors);
        }

        private void LogDevelopmentProvisioningFailure(IReadOnlyCollection<string> errors)
        {
            if (!_environment.IsDevelopment())
            {
                return;
            }

            _logger.LogInformation(
                "Registro provisioning returned failure. TraceIdentifier {TraceIdentifier}. Errors {Errors}.",
                HttpContext.TraceIdentifier,
                errors);
        }

        private bool ShouldAutoContinueToCheckout(TilopayRepeatPlanRegistration? repeatPlan)
        {
            if (repeatPlan is null || !_tilopayRepeatOptions.Enabled)
            {
                return false;
            }

            if (repeatPlan.Plan.IsAddon)
            {
                return false;
            }

            if (repeatPlan.Plan.IsValidation)
            {
                return _tilopayRepeatOptions.EnableTestRecurringPlan;
            }

            return _tilopayRepeatOptions.UseRecurringCheckoutForPublicPlans;
        }
    }
}
