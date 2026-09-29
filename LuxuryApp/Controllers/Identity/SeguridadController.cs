using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LuxuryApp.Filters;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Controllers.Identity
{
    /// <summary>
    /// Enrolamiento y administración de TOTP (MFA). Obligatorio para superadmins vía
    /// <see cref="RequireMfaEnrollmentFilter"/>; opcional para el resto de usuarios.
    /// El QR se dibuja client-side desde la URI otpauth embebida: no existe ningún
    /// endpoint que sirva la imagen.
    /// </summary>
    [Authorize]
    public class SeguridadController : Controller
    {
        private const string Issuer = "LuxuryCloud";
        private const string RecoveryCodesTempDataKey = "MfaRecoveryCodes";
        private const int RecoveryCodeCount = 8;

        private readonly UserManager<AppUsuario> _userManager;
        private readonly SignInManager<AppUsuario> _signInManager;
        private readonly IPlatformAuditService _auditService;
        private readonly IOptionsMonitor<PlatformSecurityOptions> _securityOptions;
        private readonly ILogger<SeguridadController> _logger;

        public SeguridadController(
            UserManager<AppUsuario> userManager,
            SignInManager<AppUsuario> signInManager,
            IPlatformAuditService auditService,
            IOptionsMonitor<PlatformSecurityOptions> securityOptions,
            ILogger<SeguridadController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _auditService = auditService;
            _securityOptions = securityOptions;
            _logger = logger;
        }

        [HttpGet]
        [AllowWithoutMfaEnrollment]
        public async Task<IActionResult> Enrolar()
        {
            var usuario = await _userManager.GetUserAsync(User);
            if (usuario is null)
            {
                return RedirectToAction("Acceso", "Accounts");
            }

            if (usuario.TwoFactorEnabled)
            {
                return View(new EnrolarMfaViewModel
                {
                    TwoFactorActivo = true,
                    PuedeDeshabilitar = PuedeDeshabilitar(usuario)
                });
            }

            var clave = await ObtenerClaveAutenticadorAsync(usuario);
            return View(BuildEnrolarViewModel(usuario, clave));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowWithoutMfaEnrollment]
        public async Task<IActionResult> Confirmar(EnrolarMfaViewModel model)
        {
            var usuario = await _userManager.GetUserAsync(User);
            if (usuario is null)
            {
                return RedirectToAction("Acceso", "Accounts");
            }

            if (usuario.TwoFactorEnabled)
            {
                return RedirectToAction(nameof(Enrolar));
            }

            var clave = await ObtenerClaveAutenticadorAsync(usuario);

            if (!ModelState.IsValid)
            {
                return View(nameof(Enrolar), BuildEnrolarViewModel(usuario, clave, model.Codigo));
            }

            var codigo = NormalizarCodigo(model.Codigo);
            var codigoValido = await _userManager.VerifyTwoFactorTokenAsync(
                usuario,
                _userManager.Options.Tokens.AuthenticatorTokenProvider,
                codigo);

            if (!codigoValido)
            {
                ModelState.AddModelError(
                    nameof(model.Codigo),
                    "El código no coincide. Revisa que el reloj de tu teléfono esté en hora e intenta de nuevo.");
                return View(nameof(Enrolar), BuildEnrolarViewModel(usuario, clave));
            }

            await _userManager.SetTwoFactorEnabledAsync(usuario, true);
            var codigosRecuperacion = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(usuario, RecoveryCodeCount);

            // SetTwoFactorEnabledAsync rota el security stamp; sin refrescar la cookie, el
            // validador de stamp (ValidationInterval=Zero) mataría la sesión en el siguiente
            // request y el usuario nunca vería sus códigos de recuperación.
            await _signInManager.RefreshSignInAsync(usuario);

            await AuditarAsync(usuario, PlatformAuditActions.MfaEnabled);

            _logger.LogInformation("MFA TOTP habilitado para UserId {UserId}.", usuario.Id);

            TempData[RecoveryCodesTempDataKey] = JsonSerializer.Serialize(codigosRecuperacion?.ToArray() ?? Array.Empty<string>());
            return RedirectToAction(nameof(CodigosRecuperacion));
        }

        [HttpGet]
        [AllowWithoutMfaEnrollment]
        public IActionResult CodigosRecuperacion()
        {
            if (TempData[RecoveryCodesTempDataKey] is not string serialized)
            {
                // Los códigos solo se muestran una vez, inmediatamente después de enrolar.
                return RedirectToAction(nameof(Enrolar));
            }

            var codigos = JsonSerializer.Deserialize<string[]>(serialized) ?? Array.Empty<string>();
            return View(new CodigosRecuperacionViewModel { Codigos = codigos });
        }

        /// <summary>
        /// Apaga el TOTP del usuario autenticado.
        /// </summary>
        /// <param name="volverACuenta">
        /// La acción se ofrece desde dos lugares (la pantalla de enrolamiento y la sección
        /// Seguridad de Mi cuenta) y cada uno debe volver a donde estaba. Es un booleano, no una
        /// URL: no hay forma de convertirlo en un redirect abierto.
        /// </param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Deshabilitar(bool volverACuenta = false)
        {
            var usuario = await _userManager.GetUserAsync(User);
            if (usuario is null)
            {
                return RedirectToAction("Acceso", "Accounts");
            }

            if (!PuedeDeshabilitar(usuario))
            {
                return Forbid();
            }

            await _userManager.SetTwoFactorEnabledAsync(usuario, false);
            await _userManager.ResetAuthenticatorKeyAsync(usuario);

            // Mismo motivo que en Confirmar: el stamp rotó y la cookie debe refrescarse.
            await _signInManager.RefreshSignInAsync(usuario);

            await AuditarAsync(usuario, PlatformAuditActions.MfaDisabled);

            _logger.LogInformation("MFA TOTP deshabilitado para UserId {UserId}.", usuario.Id);

            const string mensaje = "La verificación en dos pasos quedó deshabilitada.";

            // Se escribe SOLO la clave que va a leer la pantalla de destino. Dejar la otra
            // puesta la arrastraría a un request posterior y aparecería un aviso viejo.
            if (volverACuenta)
            {
                TempData["Mensaje"] = mensaje;
                return RedirectToAction(
                    "Cuenta",
                    "Accounts",
                    routeValues: null,
                    fragment: AccountSettingsSections.Seguridad);
            }

            TempData["SeguridadMensaje"] = mensaje;
            return RedirectToAction(nameof(Enrolar));
        }

        /// <summary>
        /// Cierra la sesión en todos los dispositivos rotando el security stamp del usuario.
        /// Con el validador de stamp corriendo en cada request, cualquier otra cookie viva
        /// (persistente incluida) queda invalidada en su siguiente petición; además se cierra
        /// la sesión actual y se redirige al login. No se registra información sensible.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowWithoutMfaEnrollment]
        public async Task<IActionResult> CerrarSesionEnTodosLosDispositivos()
        {
            var usuario = await _userManager.GetUserAsync(User);
            if (usuario is null)
            {
                return RedirectToAction("Acceso", "Accounts");
            }

            await _userManager.UpdateSecurityStampAsync(usuario);
            await _signInManager.SignOutAsync();

            _logger.LogInformation(
                "Cierre de sesión en todos los dispositivos solicitado. UserId {UserId}.",
                usuario.Id);

            TempData["SeguridadMensaje"] =
                "Cerramos la sesión en todos tus dispositivos. Inicia sesión nuevamente para continuar.";
            return RedirectToAction("Acceso", "Accounts");
        }

        // La regla vive en MfaEnrollmentPolicy porque Mi cuenta también necesita saber si
        // ofrecer el botón de desactivar. Una sola definición, dos consumidores.
        private bool PuedeDeshabilitar(AppUsuario usuario) =>
            MfaEnrollmentPolicy.PuedeDeshabilitar(usuario, _securityOptions.CurrentValue);

        private async Task<string> ObtenerClaveAutenticadorAsync(AppUsuario usuario)
        {
            var clave = await _userManager.GetAuthenticatorKeyAsync(usuario);
            if (string.IsNullOrEmpty(clave))
            {
                // ResetAuthenticatorKeyAsync también rota el security stamp: sin refrescar la
                // cookie aquí, el POST de confirmación llegaría con stamp viejo y el validador
                // (ValidationInterval=Zero) lo rechazaría antes de ejecutar la acción.
                await _userManager.ResetAuthenticatorKeyAsync(usuario);
                await _signInManager.RefreshSignInAsync(usuario);
                clave = await _userManager.GetAuthenticatorKeyAsync(usuario);
            }

            return clave!;
        }

        private EnrolarMfaViewModel BuildEnrolarViewModel(AppUsuario usuario, string clave, string codigo = "")
            => new()
            {
                TwoFactorActivo = false,
                PuedeDeshabilitar = false,
                ClaveFormateada = FormatearClave(clave),
                OtpauthUri = BuildOtpauthUri(usuario.Email ?? usuario.UserName ?? usuario.Id, clave),
                Codigo = codigo
            };

        private static string BuildOtpauthUri(string cuenta, string clave)
        {
            var issuer = UrlEncoder.Default.Encode(Issuer);
            var cuentaEncoded = UrlEncoder.Default.Encode(cuenta);
            return $"otpauth://totp/{issuer}:{cuentaEncoded}?secret={clave}&issuer={issuer}&digits=6";
        }

        private static string FormatearClave(string clave)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < clave.Length; i += 4)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(clave.AsSpan(i, Math.Min(4, clave.Length - i)));
            }

            return builder.ToString().ToLowerInvariant();
        }

        private static string NormalizarCodigo(string codigo) =>
            codigo.Replace(" ", string.Empty).Replace("-", string.Empty);

        /// <summary>
        /// La bitácora de plataforma registra el MFA de superadmins; para usuarios de tenant
        /// no se audita aquí (no es una acción de plataforma). TryLogAsync nunca lanza:
        /// un fallo de auditoría no frena el enrolamiento y queda en el log (S6).
        /// </summary>
        private Task AuditarAsync(AppUsuario usuario, string accion)
        {
            if (!usuario.IsPlatformSuperAdmin)
            {
                return Task.CompletedTask;
            }

            return _auditService.TryLogAsync(new PlatformAuditEntry
            {
                Action = accion,
                EntityType = PlatformAuditEntityTypes.User,
                EntityId = usuario.Id,
                TargetUserId = usuario.Id,
                TargetUserEmail = usuario.Email
            });
        }
    }
}
