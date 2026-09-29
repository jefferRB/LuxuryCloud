using System.Security.Claims;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Services.Account;
using LuxuryApp.Services.Asociados;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Services.Tenant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Asociados
{
    /// <summary>
    /// Asociados del negocio: inversionistas, socios, marketing, contabilidad y demás
    /// colaboradores que no son funcionarios operativos.
    ///
    /// <para>
    /// Autorización: <c>Associates.View</c> para mirar y <c>Associates.Manage</c> para tocar. El
    /// administrador del tenant cumple ambos automáticamente. Esconder el módulo del menú NO es
    /// seguridad: cada acción está protegida acá y la URL directa devuelve 403.
    /// </para>
    ///
    /// <para>
    /// El controlador es delgado a propósito: no crea usuarios, no calcula participaciones y no
    /// valida porcentajes. Coordina servicios y arma ViewModels.
    /// </para>
    /// </summary>
    [Authorize]
    [RequirePermission(AppPermissions.AssociatesView)]
    public sealed class AsociadosController : Controller
    {
        /// <summary>Frase del correo de invitación: describe a qué se le da acceso al asociado.</summary>
        private const string DescripcionAccesoAsociado =
            "la información del negocio en LuxuryCloud que te compartieron";

        private readonly IAssociateService _associateService;
        private readonly IAssociateAccessService _accessService;
        private readonly IAssociatePermissionService _permissionService;
        private readonly IAccountEmailService _accountEmailService;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;
        private readonly IAuthorizationService _authorizationService;
        private readonly ILogger<AsociadosController> _logger;

        public AsociadosController(
            IAssociateService associateService,
            IAssociateAccessService accessService,
            IAssociatePermissionService permissionService,
            IAccountEmailService accountEmailService,
            ITenantDisplayNameService tenantDisplayNameService,
            IAuthorizationService authorizationService,
            ILogger<AsociadosController> logger)
        {
            _associateService = associateService;
            _accessService = accessService;
            _permissionService = permissionService;
            _accountEmailService = accountEmailService;
            _tenantDisplayNameService = tenantDisplayNameService;
            _authorizationService = authorizationService;
            _logger = logger;
        }

        // ─────────────── Listado y detalle ───────────────

        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var vm = await _associateService.BuildIndexAsync(await PuedeAdministrarAsync(), cancellationToken);
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> Detalle(int id, CancellationToken cancellationToken)
        {
            var vm = await _associateService.BuildDetailAsync(id, await PuedeAdministrarAsync(), cancellationToken);
            return vm is null ? NotFound() : View(vm);
        }

        // ─────────────── Alta ───────────────

        [HttpGet]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> Crear(CancellationToken cancellationToken)
        {
            var vm = await _associateService.BuildCreateFormAsync(cancellationToken);
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> Crear(AssociateFormViewModel form, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return View("Form", await _associateService.RehydrateFormAsync(form, cancellationToken));
            }

            int associateId;
            try
            {
                associateId = await _associateService.CreateAsync(form, CurrentUserId(), cancellationToken);
            }
            catch (Exception ex) when (ex is AssociateValidationException or InvestorValidationException)
            {
                AgregarErrorDeNegocio(ex);
                return View("Form", await _associateService.RehydrateFormAsync(form, cancellationToken));
            }

            // El acceso se crea DESPUÉS de que el asociado exista: si Identity falla, el asociado
            // queda registrado correctamente y se puede reintentar desde su detalle.
            if (form.DarAcceso)
            {
                var resultado = await _accessService.ActivarAccesoAsync(
                    associateId,
                    form.Email ?? string.Empty,
                    form.ModoCredencial,
                    form.ContrasenaTemporal,
                    form.Permisos ?? new List<string>(),
                    CurrentUserId(),
                    cancellationToken);

                if (!resultado.Exitoso)
                {
                    TempData["Error"] =
                        "El asociado se creó, pero no fue posible habilitar su acceso: " +
                        string.Join(" ", resultado.Errores);

                    return RedirectToAction(nameof(Detalle), new { id = associateId });
                }

                await EnviarInvitacionSiCorrespondeAsync(resultado, cancellationToken);

                TempData["Mensaje"] = form.ModoCredencial == AssociateCredentialMode.Invitacion
                    ? "Asociado creado. Se envió una invitación para que defina su contraseña."
                    : "Asociado creado con contraseña temporal. Compartila de forma segura.";
            }
            else
            {
                TempData["Mensaje"] = "Asociado creado correctamente.";
            }

            return RedirectToAction(nameof(Detalle), new { id = associateId });
        }

        // ─────────────── Información general ───────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> GuardarDatos(
            int id,
            AssociateFormViewModel form,
            CancellationToken cancellationToken)
        {
            form.Id = id;

            try
            {
                await _associateService.UpdateAsync(id, form, CurrentUserId(), cancellationToken);
                TempData["Mensaje"] = "Datos del asociado actualizados.";
            }
            catch (Exception ex) when (ex is AssociateValidationException or InvestorValidationException)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Detalle), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> CambiarEstado(int id, bool activo, CancellationToken cancellationToken)
        {
            try
            {
                await _associateService.SetActivoAsync(id, activo, CurrentUserId(), cancellationToken);
                TempData["Mensaje"] = activo
                    ? "El asociado quedó activo."
                    : "El asociado quedó inactivo. Sus datos y su participación se conservan.";
            }
            catch (AssociateValidationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Detalle), new { id });
        }

        // ─────────────── Acceso al sistema ───────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> ActivarAcceso(
            int id,
            string? email,
            string? modoCredencial,
            string? contrasenaTemporal,
            List<string>? permisos,
            CancellationToken cancellationToken)
        {
            var modo = string.Equals(modoCredencial, "temporal", StringComparison.OrdinalIgnoreCase)
                ? AssociateCredentialMode.ContrasenaTemporal
                : AssociateCredentialMode.Invitacion;

            var resultado = await _accessService.ActivarAccesoAsync(
                id,
                email ?? string.Empty,
                modo,
                contrasenaTemporal,
                permisos ?? new List<string>(),
                CurrentUserId(),
                cancellationToken);

            if (!resultado.Exitoso)
            {
                TempData["Error"] = string.Join(" ", resultado.Errores);
                return RedirectToAction(nameof(Detalle), new { id });
            }

            await EnviarInvitacionSiCorrespondeAsync(resultado, cancellationToken);

            TempData["Mensaje"] = modo == AssociateCredentialMode.Invitacion
                ? "Acceso habilitado. Se envió una invitación al correo del asociado."
                : "Acceso habilitado con contraseña temporal. Compartila de forma segura.";

            return RedirectToAction(nameof(Detalle), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> BloquearAcceso(int id, CancellationToken cancellationToken)
        {
            var resultado = await _accessService.BloquearAccesoAsync(id, CurrentUserId(), cancellationToken);
            SetTempData(resultado, "Acceso bloqueado. El asociado ya no puede iniciar sesión.");
            return RedirectToAction(nameof(Detalle), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> ReactivarAcceso(int id, CancellationToken cancellationToken)
        {
            var resultado = await _accessService.ReactivarAccesoAsync(id, CurrentUserId(), cancellationToken);
            SetTempData(resultado, "Acceso reactivado.");
            return RedirectToAction(nameof(Detalle), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> ReenviarInvitacion(int id, CancellationToken cancellationToken)
        {
            var resultado = await _accessService.GenerarEnlaceInvitacionAsync(id, CurrentUserId(), cancellationToken);

            if (!resultado.Exitoso)
            {
                TempData["Error"] = string.Join(" ", resultado.Errores);
                return RedirectToAction(nameof(Detalle), new { id });
            }

            await EnviarInvitacionSiCorrespondeAsync(resultado, cancellationToken);
            TempData["Mensaje"] = "Se reenvió la invitación al correo del asociado.";
            return RedirectToAction(nameof(Detalle), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> CambiarCorreoAcceso(int id, string? email, CancellationToken cancellationToken)
        {
            var resultado = await _accessService.CambiarCorreoAsync(
                id,
                email ?? string.Empty,
                CurrentUserId(),
                cancellationToken);

            SetTempData(resultado, "Correo de acceso actualizado.");
            return RedirectToAction(nameof(Detalle), new { id });
        }

        // ─────────────── Permisos ───────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> GuardarPermisos(
            int id,
            List<string>? permisos,
            CancellationToken cancellationToken)
        {
            var guardado = await _permissionService.GuardarAsync(
                id,
                permisos ?? new List<string>(),
                CurrentUserId(),
                cancellationToken);

            if (guardado)
            {
                TempData["Mensaje"] = "Permisos actualizados. Aplican de inmediato.";
            }
            else
            {
                TempData["Error"] = "El asociado no existe o no pertenece a tu negocio.";
            }

            return RedirectToAction(nameof(Detalle), new { id });
        }

        // ─────────────── Participación financiera ───────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequirePermission(AppPermissions.AssociatesManage)]
        public async Task<IActionResult> GuardarParticipacion(
            AssociateParticipationFormViewModel form,
            CancellationToken cancellationToken)
        {
            try
            {
                await _associateService.SaveParticipationAsync(form, CurrentUserId(), cancellationToken);
                TempData["Mensaje"] =
                    "Participación actualizada. Los estados de cuenta ya emitidos no cambian.";
            }
            catch (Exception ex) when (ex is AssociateValidationException or InvestorValidationException)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Detalle), new { id = form.AssociateId });
        }

        // ─────────────── Helpers ───────────────

        private async Task<bool> PuedeAdministrarAsync()
        {
            var resultado = await _authorizationService.AuthorizeAsync(
                User,
                AppAuthorizationPolicies.ForPermission(AppPermissions.AssociatesManage));

            return resultado.Succeeded;
        }

        private void AgregarErrorDeNegocio(Exception ex)
        {
            var key = ex switch
            {
                AssociateValidationException associate => associate.ModelStateKey,
                InvestorValidationException investor => investor.ModelStateKey,
                _ => null
            };

            ModelState.AddModelError(key ?? string.Empty, ex.Message);
        }

        private void SetTempData(AssociateAccessResult resultado, string mensajeExito)
        {
            if (resultado.Exitoso)
            {
                TempData["Mensaje"] = mensajeExito;
            }
            else
            {
                TempData["Error"] = string.Join(" ", resultado.Errores);
            }
        }

        private async Task EnviarInvitacionSiCorrespondeAsync(
            AssociateAccessResult resultado,
            CancellationToken cancellationToken)
        {
            if (!resultado.RequiereCorreoInvitacion ||
                string.IsNullOrWhiteSpace(resultado.UserId) ||
                string.IsNullOrWhiteSpace(resultado.EnlaceTokenCodificado) ||
                string.IsNullOrWhiteSpace(resultado.Email))
            {
                return;
            }

            var enlace = Url.Action(
                "ResetPassword",
                "Accounts",
                new { userId = resultado.UserId, token = resultado.EnlaceTokenCodificado },
                Request.Scheme)!;

            try
            {
                var nombreNegocio = await _tenantDisplayNameService
                    .GetCurrentTenantDisplayNameAsync(cancellationToken);

                await _accountEmailService.SendAccessInvitationEmailAsync(
                    resultado.Email,
                    resultado.NombreParaCorreo ?? "Asociado",
                    enlace,
                    nombreNegocio,
                    DescripcionAccesoAsociado,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "No se pudo enviar la invitación de acceso al asociado. UserId {UserId}.",
                    resultado.UserId);

                TempData["Error"] =
                    "El acceso se configuró, pero no se pudo enviar el correo de invitación. " +
                    "Usá \"Reenviar invitación\" más tarde.";
            }
        }

        private string? CurrentUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
