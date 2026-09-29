using LuxuryApp.Models.Asociados;
using System.Security.Claims;
using ClosedXML.Excel;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Exports;
using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Tenant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.Funcionarios
{
    [Authorize]
    [RequirePermission(AppPermissions.EmployeesView)]
    public class FuncionariosController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILiquidacionSemanalService _liquidacionSemanalService;
        private readonly IFuncionarioPortalAccessService _portalAccessService;
        private readonly IFuncionarioPortalPermissionService _portalPermissionService;
        private readonly LuxuryApp.Services.Account.IAccountEmailService _accountEmailService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;
        private readonly IFuncionarioPhotoStorageService _photoStorageService;
        private readonly ITenantProvider _tenantProvider;
        private readonly LuxuryApp.Services.Finanzas.ILegacyFinancialImpactService _impactoHistorico;
        private readonly ILogger<FuncionariosController> _logger;

        public FuncionariosController(
            ApplicationDbContext context,
            ILiquidacionSemanalService liquidacionSemanalService,
            IFuncionarioPortalAccessService portalAccessService,
            IFuncionarioPortalPermissionService portalPermissionService,
            LuxuryApp.Services.Account.IAccountEmailService accountEmailService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITenantDisplayNameService tenantDisplayNameService,
            IFuncionarioPhotoStorageService photoStorageService,
            ITenantProvider tenantProvider,
            ILogger<FuncionariosController> logger,
            LuxuryApp.Services.Finanzas.ILegacyFinancialImpactService impactoHistorico)
        {
            _context = context;
            _liquidacionSemanalService = liquidacionSemanalService;
            _portalAccessService = portalAccessService;
            _portalPermissionService = portalPermissionService;
            _accountEmailService = accountEmailService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _tenantDisplayNameService = tenantDisplayNameService;
            _photoStorageService = photoStorageService;
            _tenantProvider = tenantProvider;
            _logger = logger;
            _impactoHistorico = impactoHistorico;
        }

        public async Task<IActionResult> Index()
        {
            var funcionarios = await _context.Funcionarios
                .AsNoTracking()
                .OrderBy(f => f.Nombre)
                .Select(f => new
                {
                    f.IdFuncionario,
                    f.Nombre,
                    f.Telefono,
                    NombrePuesto = f.Puesto != null ? f.Puesto.NombrePuesto : string.Empty,
                    f.PorcentajeGanancia,
                    f.PorcentajeProducto,
                    f.ColorCalendario,
                    f.FechaIngreso,
                    f.Activo,
                    f.AppUsuarioId
                })
                .ToListAsync();

            // Estado de acceso: una sola consulta para todas las cuentas vinculadas.
            var userIds = funcionarios
                .Where(f => !string.IsNullOrWhiteSpace(f.AppUsuarioId))
                .Select(f => f.AppUsuarioId!)
                .ToList();

            var estadosCuenta = userIds.Count == 0
                ? new Dictionary<string, bool>()
                : await _context.Users
                    .AsNoTracking()
                    .Where(u => userIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.State })
                    .ToDictionaryAsync(u => u.Id, u => u.State);

            var items = funcionarios
                .Select(f => new FuncionarioIndexItemViewModel
                {
                    IdFuncionario = f.IdFuncionario,
                    Nombre = f.Nombre,
                    Telefono = f.Telefono,
                    NombrePuesto = f.NombrePuesto,
                    PorcentajeGanancia = f.PorcentajeGanancia,
                    PorcentajeProducto = f.PorcentajeProducto,
                    ColorCalendario = f.ColorCalendario,
                    FechaIngreso = f.FechaIngreso,
                    Activo = f.Activo,
                    AccesoEstado = ResolverAccesoEstado(f.AppUsuarioId, estadosCuenta)
                })
                .ToList();

            return View(new FuncionariosIndexViewModel
            {
                Funcionarios = items
            });
        }

        private static FuncionarioAccesoEstado ResolverAccesoEstado(
            string? appUsuarioId,
            IReadOnlyDictionary<string, bool> estadosCuenta)
        {
            if (string.IsNullOrWhiteSpace(appUsuarioId) ||
                !estadosCuenta.TryGetValue(appUsuarioId, out var activo))
            {
                return FuncionarioAccesoEstado.SinAcceso;
            }

            return activo
                ? FuncionarioAccesoEstado.AccesoActivo
                : FuncionarioAccesoEstado.AccesoBloqueado;
        }

        [HttpGet]
        public async Task<IActionResult> Create()
        {
            await CargarPuestosAsync();

            var funcionario = new Funcionario
            {
                FechaIngreso = _businessDateTimeProvider.Today(),
                Activo = true,
                ColorCalendario = "#000000"
            };

            return View(funcionario);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind(
                nameof(Funcionario.Nombre) + "," +
                nameof(Funcionario.Telefono) + "," +
                nameof(Funcionario.IdPuesto) + "," +
                nameof(Funcionario.ColorCalendario) + "," +
                nameof(Funcionario.PorcentajeGanancia) + "," +
                nameof(Funcionario.PorcentajeProducto) + "," +
                nameof(Funcionario.TipoRelacionColaborador) + "," +
                nameof(Funcionario.ComisionCalculadaSobre) + "," +
                nameof(Funcionario.ModalidadIvaColaborador) + "," +
                nameof(Funcionario.TarifaIvaFacturaColaborador) + "," +
                nameof(Funcionario.RequiereFacturaAntesDePagar) + "," +
                nameof(Funcionario.FechaIngreso))]
            Funcionario funcionario)
        {
            NormalizeFuncionario(funcionario);
            SincronizarConfigFiscal(funcionario);

            if (!ModelState.IsValid)
            {
                await CargarPuestosAsync(funcionario.IdPuesto);
                return View(funcionario);
            }

            var capacidad = await ValidateActiveFuncionarioCapacityAsync();
            if (!capacidad.IsAllowed)
            {
                TempData["Error"] = capacidad.Message;
                return RedirectToAction(nameof(Index));
            }

            if (await _context.Funcionarios
                    .AsNoTracking()
                    .AnyAsync(f => f.Nombre == funcionario.Nombre))
            {
                ModelState.AddModelError(nameof(Funcionario.Nombre), "Ya existe un funcionario con ese nombre.");
            }

            await ValidatePuestoAsync(funcionario.IdPuesto);

            if (!ModelState.IsValid)
            {
                await CargarPuestosAsync(funcionario.IdPuesto);
                return View(funcionario);
            }

            funcionario.Activo = true;

            try
            {
                _context.Funcionarios.Add(funcionario);
                await _context.SaveChangesAsync();

                TempData["Mensaje"] = "Funcionario creado correctamente.";
                return RedirectToAction(nameof(Index));
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al crear funcionario {Nombre}.", funcionario.Nombre);
                ModelState.AddModelError(string.Empty, "No fue posible guardar el funcionario. Verifica los datos e intentalo de nuevo.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la creacion del funcionario {Nombre}.", funcionario.Nombre);
                ModelState.AddModelError(string.Empty, "No fue posible guardar el funcionario por una validacion de seguridad o consistencia.");
            }

            await CargarPuestosAsync(funcionario.IdPuesto);
            return View(funcionario);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var funcionario = await _context.Funcionarios
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.IdFuncionario == id);

            if (funcionario == null)
            {
                return NotFound();
            }

            await CargarPuestosAsync(funcionario.IdPuesto);
            ViewData["Acceso"] = await _portalAccessService.ObtenerEstadoAsync(id, HttpContext.RequestAborted);
            ViewData["Permisos"] = await _portalPermissionService.ObtenerAsync(id, HttpContext.RequestAborted);
            return View(funcionario);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarPermisos(int id, List<string>? permisos)
        {
            var seleccionados = (permisos ?? new List<string>())
                .Where(FuncionarioPortalPermissions.EsPermisoValido)
                .ToHashSet(StringComparer.Ordinal);

            var valores = FuncionarioPortalPermissions.Todos
                .ToDictionary(p => p, p => seleccionados.Contains(p), StringComparer.Ordinal);

            var ok = await _portalPermissionService.GuardarAsync(id, valores, HttpContext.RequestAborted);
            TempData[ok ? "Mensaje" : "Error"] = ok
                ? "Permisos del portal actualizados."
                : "No fue posible actualizar los permisos. Verifica que el funcionario exista.";

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(6 * 1024 * 1024)] // ~6MB; el servicio valida el máximo real (5MB)
        public async Task<IActionResult> ActualizarFoto(
            int id,
            IFormFile? foto,
            bool mostrarFotoEnReservas,
            string? descripcionPublica)
        {
            var funcionario = await _context.Funcionarios
                .FirstOrDefaultAsync(f => f.IdFuncionario == id);

            if (funcionario == null)
            {
                return NotFound();
            }

            // Descripcion publica (bio) opcional. Texto plano: no se permite HTML.
            var descripcion = descripcionPublica?.Trim();
            if (!string.IsNullOrEmpty(descripcion) &&
                (descripcion.Contains('<', StringComparison.Ordinal) ||
                 descripcion.Contains('>', StringComparison.Ordinal)))
            {
                TempData["Error"] = "La descripción pública no puede contener los caracteres < o >.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            if (!string.IsNullOrEmpty(descripcion) && descripcion.Length > 280)
            {
                descripcion = descripcion[..280];
            }

            funcionario.DescripcionPublica = string.IsNullOrWhiteSpace(descripcion) ? null : descripcion;

            // Si viene un archivo, se valida y guarda de forma segura (magic bytes, GUID, por tenant).
            if (foto is not null && foto.Length > 0)
            {
                var tenantId = _tenantProvider.HasTenant() ? _tenantProvider.GetTenantId() : Guid.Empty;
                var result = await _photoStorageService.SaveAsync(
                    tenantId, foto, funcionario.FotoStoragePath, HttpContext.RequestAborted);

                if (!result.Success)
                {
                    TempData["Error"] = result.Error ?? "No se pudo guardar la foto.";
                    return RedirectToAction(nameof(Edit), new { id });
                }

                funcionario.FotoUrl = result.Url;
                funcionario.FotoStoragePath = result.StoragePath;
                funcionario.FotoActualizadaUtc = DateTime.UtcNow;
            }

            funcionario.MostrarFotoEnReservas = mostrarFotoEnReservas;

            try
            {
                await _context.SaveChangesAsync();
                TempData["Mensaje"] = "Perfil público del profesional actualizado.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al actualizar la foto del funcionario {FuncionarioId}.", id);
                TempData["Error"] = "No fue posible actualizar la foto.";
            }

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarFoto(int id)
        {
            var funcionario = await _context.Funcionarios
                .FirstOrDefaultAsync(f => f.IdFuncionario == id);

            if (funcionario == null)
            {
                return NotFound();
            }

            var storagePath = funcionario.FotoStoragePath;
            funcionario.FotoUrl = null;
            funcionario.FotoStoragePath = null;
            funcionario.FotoActualizadaUtc = DateTime.UtcNow;

            try
            {
                await _context.SaveChangesAsync();
                _photoStorageService.Delete(storagePath); // borra el archivo tras confirmar en BD
                TempData["Mensaje"] = "Foto eliminada. Se usará el avatar con inicial.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al eliminar la foto del funcionario {FuncionarioId}.", id);
                TempData["Error"] = "No fue posible eliminar la foto.";
            }

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            [Bind(
                nameof(Funcionario.IdFuncionario) + "," +
                nameof(Funcionario.Nombre) + "," +
                nameof(Funcionario.Telefono) + "," +
                nameof(Funcionario.IdPuesto) + "," +
                nameof(Funcionario.ColorCalendario) + "," +
                nameof(Funcionario.PorcentajeGanancia) + "," +
                nameof(Funcionario.PorcentajeProducto) + "," +
                nameof(Funcionario.TipoRelacionColaborador) + "," +
                nameof(Funcionario.ComisionCalculadaSobre) + "," +
                nameof(Funcionario.ModalidadIvaColaborador) + "," +
                nameof(Funcionario.TarifaIvaFacturaColaborador) + "," +
                nameof(Funcionario.RequiereFacturaAntesDePagar) + "," +
                nameof(Funcionario.FechaIngreso))]
            Funcionario funcionario,
            bool confirmarImpactoHistorico = false)
        {
            NormalizeFuncionario(funcionario);
            SincronizarConfigFiscal(funcionario);

            if (!ModelState.IsValid)
            {
                await CargarPuestosAsync(funcionario.IdPuesto);
                ViewData["Acceso"] = await _portalAccessService.ObtenerEstadoAsync(funcionario.IdFuncionario, HttpContext.RequestAborted);
                return View(funcionario);
            }

            var funcionarioDb = await _context.Funcionarios
                .FirstOrDefaultAsync(f => f.IdFuncionario == funcionario.IdFuncionario);

            if (funcionarioDb == null)
            {
                return NotFound();
            }

            if (await _context.Funcionarios
                    .AsNoTracking()
                    .AnyAsync(f => f.Nombre == funcionario.Nombre && f.IdFuncionario != funcionario.IdFuncionario))
            {
                ModelState.AddModelError(nameof(Funcionario.Nombre), "Ya existe un funcionario con ese nombre.");
            }

            await ValidatePuestoAsync(funcionario.IdPuesto, funcionarioDb.IdPuesto);

            // La configuración de remuneración TODAVÍA no se congela por transacción (el snapshot
            // de comisión quedó pendiente de aprobación porque obliga a tocar la atribución de
            // pagos). Mientras tanto, cambiar el porcentaje o la modalidad recalcula la liquidación
            // de cualquier periodo, incluidos los ya pagados: hay que avisarlo con claridad.
            if (await RequiereConfirmacionHistoricaAsync(funcionario, funcionarioDb, confirmarImpactoHistorico))
            {
                await CargarPuestosAsync(funcionario.IdPuesto);
                ViewData["Acceso"] = await _portalAccessService.ObtenerEstadoAsync(funcionario.IdFuncionario, HttpContext.RequestAborted);
                return View(funcionario);
            }

            if (!ModelState.IsValid)
            {
                await CargarPuestosAsync(funcionario.IdPuesto);
                ViewData["Acceso"] = await _portalAccessService.ObtenerEstadoAsync(funcionario.IdFuncionario, HttpContext.RequestAborted);
                return View(funcionario);
            }

            funcionarioDb.Nombre = funcionario.Nombre;
            funcionarioDb.Telefono = funcionario.Telefono;
            funcionarioDb.IdPuesto = funcionario.IdPuesto;
            funcionarioDb.ColorCalendario = funcionario.ColorCalendario;
            funcionarioDb.PorcentajeGanancia = funcionario.PorcentajeGanancia;
            funcionarioDb.PorcentajeProducto = funcionario.PorcentajeProducto;
            funcionarioDb.TipoRelacionColaborador = funcionario.TipoRelacionColaborador;
            funcionarioDb.ComisionCalculadaSobre = funcionario.ComisionCalculadaSobre;
            funcionarioDb.ModalidadIvaColaborador = funcionario.ModalidadIvaColaborador;
            funcionarioDb.ColaboradorFacturaIva = funcionario.ColaboradorFacturaIva;
            funcionarioDb.TarifaIvaFacturaColaborador = funcionario.TarifaIvaFacturaColaborador;
            funcionarioDb.RequiereFacturaAntesDePagar = funcionario.RequiereFacturaAntesDePagar;
            // Compatibilidad: el flag histórico se deriva de ComisionCalculadaSobre.
            funcionarioDb.RebajarImpuestosAntesDeComision = funcionario.RebajarImpuestosAntesDeComision;
            funcionarioDb.FechaIngreso = funcionario.FechaIngreso;

            try
            {
                await _context.SaveChangesAsync();

                TempData["Mensaje"] = "Funcionario actualizado correctamente.";
                return RedirectToAction(nameof(Index));
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al actualizar funcionario {FuncionarioId}.", funcionario.IdFuncionario);
                ModelState.AddModelError(string.Empty, "No fue posible actualizar el funcionario.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la edicion del funcionario {FuncionarioId}.", funcionario.IdFuncionario);
                ModelState.AddModelError(string.Empty, "No fue posible actualizar el funcionario por una validacion de seguridad o consistencia.");
            }

            await CargarPuestosAsync(funcionario.IdPuesto);
            ViewData["Acceso"] = await _portalAccessService.ObtenerEstadoAsync(funcionario.IdFuncionario, HttpContext.RequestAborted);
            return View(funcionario);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int idFuncionario)
        {
            var funcionario = await _context.Funcionarios
                .FirstOrDefaultAsync(f => f.IdFuncionario == idFuncionario);

            if (funcionario == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrWhiteSpace(funcionario.AppUsuarioId))
            {
                TempData["Error"] = "No se puede eliminar el funcionario porque tiene una cuenta de acceso al portal. Bloquea su acceso desde 'Acceso al sistema' antes de eliminarlo.";
                return RedirectToAction(nameof(Index));
            }

            var tieneCitas = await _context.Citas.AnyAsync(c => c.FuncionarioId == idFuncionario);
            var tieneCobros = await _context.Cobros.AnyAsync(c => c.FuncionarioId == idFuncionario);
            var tienePagos = await _context.PagosFuncionarios.AnyAsync(p => p.FuncionarioId == idFuncionario);
            var tieneLiquidaciones = await _context.LiquidacionesSemanalesDetalle.AnyAsync(d => d.FuncionarioId == idFuncionario);

            if (tieneCitas || tieneCobros || tienePagos || tieneLiquidaciones)
            {
                TempData["Error"] = "No se puede eliminar el funcionario porque tiene citas, cobros, pagos o liquidaciones asociadas. Puedes dejarlo inactivo si ya no trabaja en el negocio.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                _context.Funcionarios.Remove(funcionario);
                await _context.SaveChangesAsync();
                TempData["Mensaje"] = "Funcionario eliminado correctamente.";
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al eliminar funcionario {FuncionarioId}.", idFuncionario);
                TempData["Error"] = "No fue posible eliminar el funcionario porque tiene relaciones activas.";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Activar(int id) => SetActivoAsync(id, true);

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Desactivar(int id) => SetActivoAsync(id, false);

        // ─────────────────────────── ACCESO AL PORTAL ───────────────────────────

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActivarAcceso(
            int id,
            string? email,
            string? modoCredencial,
            string? contrasenaTemporal)
        {
            var modo = string.Equals(modoCredencial, "temporal", StringComparison.OrdinalIgnoreCase)
                ? FuncionarioAccesoCredencialModo.ContrasenaTemporal
                : FuncionarioAccesoCredencialModo.Invitacion;

            var resultado = await _portalAccessService.ActivarAccesoAsync(
                id,
                email ?? string.Empty,
                modo,
                contrasenaTemporal,
                HttpContext.RequestAborted);

            if (!resultado.Exitoso)
            {
                TempData["Error"] = string.Join(" ", resultado.Errores);
                return RedirectToAction(nameof(Edit), new { id });
            }

            await EnviarInvitacionSiCorrespondeAsync(resultado);

            TempData["Mensaje"] = modo == FuncionarioAccesoCredencialModo.Invitacion
                ? "Acceso habilitado. Se envió una invitación al correo del funcionario para definir su contraseña."
                : "Acceso habilitado con contraseña temporal. Compártela de forma segura con el funcionario.";

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DesactivarAcceso(int id)
        {
            var resultado = await _portalAccessService.DesactivarAccesoAsync(id, HttpContext.RequestAborted);
            SetAccesoTempData(resultado, "Acceso del funcionario bloqueado. Ya no podrá iniciar sesión.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReactivarAcceso(int id)
        {
            var resultado = await _portalAccessService.ReactivarAccesoAsync(id, HttpContext.RequestAborted);
            SetAccesoTempData(resultado, "Acceso del funcionario reactivado.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReenviarInvitacionAcceso(int id)
        {
            var resultado = await _portalAccessService.GenerarEnlaceInvitacionAsync(id, HttpContext.RequestAborted);
            if (!resultado.Exitoso)
            {
                TempData["Error"] = string.Join(" ", resultado.Errores);
                return RedirectToAction(nameof(Edit), new { id });
            }

            await EnviarInvitacionSiCorrespondeAsync(resultado);
            TempData["Mensaje"] = "Se reenvió la invitación al correo del funcionario.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CambiarCorreoAcceso(int id, string? email)
        {
            var resultado = await _portalAccessService.CambiarCorreoAsync(id, email ?? string.Empty, HttpContext.RequestAborted);
            SetAccesoTempData(resultado, "Correo de acceso actualizado.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        private void SetAccesoTempData(FuncionarioAccesoResultado resultado, string mensajeExito)
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

        private async Task EnviarInvitacionSiCorrespondeAsync(FuncionarioAccesoResultado resultado)
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
                var nombreNegocio = await _tenantDisplayNameService.GetCurrentTenantDisplayNameAsync(HttpContext.RequestAborted);
                await _accountEmailService.SendFuncionarioInvitationEmailAsync(
                    resultado.Email,
                    resultado.NombreParaCorreo ?? "Funcionario",
                    enlace,
                    nombreNegocio,
                    HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo enviar la invitación de acceso al funcionario. UserId {UserId}.", resultado.UserId);
                TempData["Error"] = "El acceso se configuró, pero no se pudo enviar el correo de invitación. Usa 'Reenviar invitación' más tarde.";
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetActivos()
        {
            var funcionarios = await _context.Funcionarios
                .AsNoTracking()
                .Where(f => f.Activo)
                .OrderBy(f => f.Nombre)
                .Select(f => new
                {
                    id = f.IdFuncionario,
                    nombre = f.Nombre,
                    colorCalendario = f.ColorCalendario
                })
                .ToListAsync();

            return Json(funcionarios);
        }

        public async Task<IActionResult> PagosSemana(
            DateTime? fecha,
            string? periodo = null,
            DateTime? desde = null,
            DateTime? hasta = null)
        {
            var fechaReferencia = fecha?.Date ?? _businessDateTimeProvider.Today();
            var fechaPagoSugerida = _businessDateTimeProvider.Now();

            // Resolución del periodo (semanal / quincenal / rango personalizado). El servicio opera sobre el rango.
            var periodoTipo = PayrollPeriodCalculator.ParseTipo(periodo);
            PayrollPeriod periodoInfo;

            if (periodoTipo == PayrollPeriodType.Personalizado)
            {
                var (periodoPers, aviso) = PayrollPeriodCalculator.ResolvePersonalizado(
                    desde, hasta, _businessDateTimeProvider.Today());
                periodoInfo = periodoPers;
                if (!string.IsNullOrEmpty(aviso))
                {
                    TempData["Error"] = aviso;
                }
            }
            else
            {
                periodoInfo = PayrollPeriodCalculator.Resolve(periodoTipo, fechaReferencia);
            }

            var resumen = await _liquidacionSemanalService.ObtenerResumenSemanaAsync(
                periodoInfo.Inicio,
                periodoInfo.Fin,
                HttpContext.RequestAborted);

            return View(new PagosSemanaPageViewModel
            {
                InicioSemana = resumen.InicioSemana,
                FinSemana = resumen.FinSemana,
                PeriodoTipo = periodoInfo.Tipo,
                PeriodoTipoLabel = periodoInfo.TipoLabel,
                PeriodoEtiqueta = periodoInfo.Etiqueta,
                PeriodoCtaTexto = periodoInfo.CtaTexto,
                PeriodoReferenciaAnterior = periodoInfo.ReferenciaAnterior,
                PeriodoReferenciaSiguiente = periodoInfo.ReferenciaSiguiente,
                FechaPagoSugerida = fechaPagoSugerida,
                Hoy = _businessDateTimeProvider.Today(),
                MetodosPago = ObtenerMetodosPago(),
                Funcionarios = resumen.Funcionarios,
                TotalGeneradoServicios = resumen.TotalGeneradoServicios,
                TotalGeneradoProductos = resumen.TotalGeneradoProductos,
                TotalGeneradoGeneral = resumen.TotalGeneradoGeneral,
                TotalImpuestosGeneral = resumen.TotalImpuestosGeneral,
                TotalSinImpuestosGeneral = resumen.TotalSinImpuestosGeneral,
                TotalPagadoGeneral = resumen.TotalPagadoGeneral,
                TotalPagadoAplicadoGeneral = resumen.TotalPagadoAplicadoGeneral,
                TotalPendienteGeneral = resumen.TotalPendienteGeneral,
                TotalExcedenteGeneral = resumen.TotalExcedenteGeneral,
                GananciaNegocio = resumen.GananciaNegocio,
                TotalBaseVentaSinIvaGeneral = resumen.TotalBaseVentaSinIvaGeneral,
                TotalIvaVentaIncluidoGeneral = resumen.TotalIvaVentaIncluidoGeneral,
                TotalIvaColaboradorGeneral = resumen.TotalIvaColaboradorGeneral,
                TotalIvaNetoNegocioGeneral = resumen.TotalIvaNetoNegocioGeneral,
                TotalAPagarColaboradoresGeneral = resumen.TotalAPagarColaboradoresGeneral,
                TotalBaseComisionGeneral = resumen.TotalBaseComisionGeneral
            });
        }

        // Diagnóstico TEMPORAL de atribución de pagos por periodo. Devuelve JSON con cada pago
        // (incluido/excluido, fracción y monto aplicado) para auditar la lógica de Pagado/Pendiente.
        [HttpGet]
        public async Task<IActionResult> PagosDiagnostico(
            DateTime? fecha,
            string? periodo = null,
            DateTime? desde = null,
            DateTime? hasta = null)
        {
            var fechaReferencia = fecha?.Date ?? _businessDateTimeProvider.Today();
            var periodoTipo = PayrollPeriodCalculator.ParseTipo(periodo);

            PayrollPeriod periodoInfo = periodoTipo == PayrollPeriodType.Personalizado
                ? PayrollPeriodCalculator.ResolvePersonalizado(desde, hasta, _businessDateTimeProvider.Today()).Periodo
                : PayrollPeriodCalculator.Resolve(periodoTipo, fechaReferencia);

            var resumen = await _liquidacionSemanalService.ObtenerResumenSemanaAsync(
                periodoInfo.Inicio, periodoInfo.Fin, HttpContext.RequestAborted);

            var diagnostico = await _liquidacionSemanalService.ObtenerDiagnosticoPagosAsync(
                periodoInfo.Inicio, periodoInfo.Fin, HttpContext.RequestAborted);

            return Json(new
            {
                periodo = periodoInfo.TipoLabel,
                rango = periodoInfo.Etiqueta,
                inicio = periodoInfo.Inicio.ToString("yyyy-MM-dd"),
                fin = periodoInfo.Fin.ToString("yyyy-MM-dd"),
                totales = new
                {
                    planilla = resumen.TotalAPagarColaboradoresGeneral,
                    pagadoAplicado = resumen.TotalPagadoGeneral,
                    pendiente = resumen.TotalPendienteGeneral,
                    excedente = resumen.TotalExcedenteGeneral
                },
                pagosIncluidos = diagnostico.Where(d => d.Incluido),
                pagosExcluidos = diagnostico.Where(d => !d.Incluido),
                colaboradores = resumen.Funcionarios.Select(f => new
                {
                    f.FuncionarioId,
                    f.Nombre,
                    planilla = f.TotalAPagarColaborador,
                    pagadoAplicado = f.MontoPagado,
                    pendiente = f.MontoPendiente,
                    excedente = f.Excedente
                })
            });
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegistrarPago(
            int funcionarioId,
            decimal monto,
            DateTime inicioSemana,
            DateTime finSemana,
            string? observacion,
            string? metodoPago,
            DateTime? fechaPago,
            Guid? idempotencyKey = null,
            string? periodo = null,
            DateTime? desde = null,
            DateTime? hasta = null)
        {
            var rutaVolver = RutaPagosSemana(inicioSemana, periodo, desde, hasta);

            if (monto <= 0)
            {
                TempData["Error"] = "El monto a pagar debe ser mayor que cero.";
                return RedirectToAction(nameof(PagosSemana), rutaVolver);
            }

            var resumenValidacion = await _liquidacionSemanalService.ObtenerResumenSemanaAsync(
                inicioSemana,
                finSemana,
                HttpContext.RequestAborted);

            var funcionarioResumen = resumenValidacion.Funcionarios
                .FirstOrDefault(f => f.FuncionarioId == funcionarioId);

            var montoPendiente = funcionarioResumen?.MontoPendiente ?? 0m;

            if (monto > montoPendiente)
            {
                TempData["Error"] = montoPendiente <= 0
                    ? "Este colaborador no tiene monto pendiente en el periodo seleccionado."
                    : "El monto a pagar no puede ser mayor al pendiente del colaborador.";
                return RedirectToAction(nameof(PagosSemana), rutaVolver);
            }

            try
            {
                await _liquidacionSemanalService.RegistrarPagoAsync(
                    new RegistrarLiquidacionSemanalCommand
                    {
                        SemanaInicio = inicioSemana,
                        SemanaFin = finSemana,
                        FechaPago = fechaPago,
                        MetodoPago = metodoPago ?? string.Empty,
                        Observacion = observacion,
                        CreadoPor = User.FindFirstValue(CustomClaimTypes.UserId) ?? User.FindFirstValue(ClaimTypes.NameIdentifier),
                        IdempotencyKey = idempotencyKey,
                        Detalles =
                        {
                            new RegistrarLiquidacionSemanalDetalleCommand
                            {
                                FuncionarioId = funcionarioId,
                                MontoPagado = monto
                            }
                        }
                    },
                    HttpContext.RequestAborted);

                TempData["Mensaje"] = "Pago registrado correctamente.";
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Validacion de negocio al registrar pago semanal para funcionario {FuncionarioId}.", funcionarioId);
                TempData["Error"] = ex.Message;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al registrar pago semanal para funcionario {FuncionarioId}.", funcionarioId);
                TempData["Error"] = "No fue posible registrar el pago por un error de persistencia.";
            }

            return RedirectToAction(nameof(PagosSemana), rutaVolver);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PagarTodaLaSemana(
            DateTime inicioSemana,
            DateTime finSemana,
            string? metodoPago,
            DateTime? fechaPago,
            string? observacion,
            Guid? idempotencyKey = null,
            string? periodo = null,
            DateTime? desde = null,
            DateTime? hasta = null)
        {
            var rutaVolver = RutaPagosSemana(inicioSemana, periodo, desde, hasta);

            var resumen = await _liquidacionSemanalService.ObtenerResumenSemanaAsync(
                inicioSemana,
                finSemana,
                HttpContext.RequestAborted);

            var detalles = resumen.Funcionarios
                .Where(f => f.MontoPendiente > 0)
                .Select(f => new RegistrarLiquidacionSemanalDetalleCommand
                {
                    FuncionarioId = f.FuncionarioId,
                    MontoPagado = f.MontoPendiente
                })
                .ToList();

            if (detalles.Count == 0)
            {
                TempData["Mensaje"] = "El periodo seleccionado no tiene pendientes por liquidar.";
                return RedirectToAction(nameof(PagosSemana), rutaVolver);
            }

            try
            {
                await _liquidacionSemanalService.RegistrarPagoAsync(
                    new RegistrarLiquidacionSemanalCommand
                    {
                        SemanaInicio = inicioSemana,
                        SemanaFin = finSemana,
                        FechaPago = fechaPago,
                        MetodoPago = metodoPago ?? string.Empty,
                        Observacion = string.IsNullOrWhiteSpace(observacion)
                            ? "Pago semanal automatico"
                            : observacion,
                        CreadoPor = User.FindFirstValue(CustomClaimTypes.UserId) ?? User.FindFirstValue(ClaimTypes.NameIdentifier),
                        IdempotencyKey = idempotencyKey,
                        Detalles = detalles
                    },
                    HttpContext.RequestAborted);

                TempData["Mensaje"] = "Todos los pagos pendientes del periodo fueron liquidados.";
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Validacion de negocio al liquidar el periodo {InicioSemana:yyyy-MM-dd} - {FinSemana:yyyy-MM-dd}.", inicioSemana, finSemana);
                TempData["Error"] = ex.Message;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al liquidar pagos del periodo {InicioSemana:yyyy-MM-dd} - {FinSemana:yyyy-MM-dd}.", inicioSemana, finSemana);
                TempData["Error"] = "No fue posible registrar la liquidacion del periodo.";
            }

            return RedirectToAction(nameof(PagosSemana), rutaVolver);
        }

        /// <summary>
        /// Revierte por completo un pago de liquidación registrado por error. NO edita nada: la
        /// operación (liquidación + detalles + distribución + egreso) se deshace de forma atómica
        /// y queda constancia en la bitácora. El pendiente del colaborador vuelve a su valor previo
        /// SIN tocar su producción ni su comisión devengada.
        /// </summary>
        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevertirPago(
            int liquidacionId,
            string? motivo,
            DateTime? fecha = null,
            string? periodo = null,
            DateTime? desde = null,
            DateTime? hasta = null)
        {
            var rutaVolver = RutaPagosSemana(
                fecha ?? _businessDateTimeProvider.Today(), periodo, desde, hasta);

            if (string.IsNullOrWhiteSpace(motivo))
            {
                TempData["Error"] = "Debes indicar el motivo de la reversión.";
                return RedirectToAction(nameof(PagosSemana), rutaVolver);
            }

            try
            {
                var resultado = await _liquidacionSemanalService.RevertirPagoAsync(
                    liquidacionId,
                    motivo,
                    User.FindFirstValue(CustomClaimTypes.UserId) ?? User.FindFirstValue(ClaimTypes.NameIdentifier),
                    HttpContext.RequestAborted);

                switch (resultado.Estado)
                {
                    case ReversionPagoEstado.Revertida:
                        var quienes = resultado.Funcionarios.Count > 0
                            ? string.Join(", ", resultado.Funcionarios)
                            : "el equipo";
                        TempData["Mensaje"] =
                            $"Pago revertido: ₡{resultado.MontoRevertido:N0} de {quienes} volvió a quedar pendiente.";
                        break;

                    case ReversionPagoEstado.YaRevertida:
                        TempData["Mensaje"] = "El pago ya había sido revertido.";
                        break;

                    default:
                        TempData["Error"] = "No se encontró ese pago en este negocio.";
                        break;
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Validacion de negocio al revertir la liquidacion {LiquidacionId}.", liquidacionId);
                TempData["Error"] = ex.Message;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al revertir la liquidacion {LiquidacionId}.", liquidacionId);
                TempData["Error"] = "No fue posible revertir el pago por un error de persistencia.";
            }

            return RedirectToAction(nameof(PagosSemana), rutaVolver);
        }

        // Construye los route values para volver al mismo periodo (incluye el rango cuando es personalizado).
        private static object RutaPagosSemana(DateTime inicioSemana, string? periodo, DateTime? desde, DateTime? hasta)
        {
            var esPersonalizado = PayrollPeriodCalculator.ParseTipo(periodo) == PayrollPeriodType.Personalizado;
            return new
            {
                fecha = inicioSemana.ToString("yyyy-MM-dd"),
                periodo,
                desde = esPersonalizado ? (desde ?? inicioSemana).ToString("yyyy-MM-dd") : null,
                hasta = esPersonalizado ? hasta?.ToString("yyyy-MM-dd") : null
            };
        }

        public async Task<IActionResult> ExportarPagosExcel(DateTime inicioSemana, DateTime finSemana, string? periodo = null)
        {
            var periodoTipo = PayrollPeriodCalculator.ParseTipo(periodo);
            var tituloPeriodo = periodoTipo switch
            {
                PayrollPeriodType.Quincenal => "Liquidación quincenal",
                PayrollPeriodType.Personalizado => "Liquidación por rango",
                _ => "Liquidación semanal"
            };

            var resumen = await _liquidacionSemanalService.ObtenerResumenSemanaAsync(
                inicioSemana,
                finSemana,
                HttpContext.RequestAborted);

            var generatedAt = _businessDateTimeProvider.Now();
            const string excelCurrencyFormat = "CRC #,##0.00";

            using var workbook = new XLWorkbook();
            var colorNegro = XLColor.FromHtml("#1C1C1C");
            var colorDorado = XLColor.FromHtml("#C6A55C");
            var colorGris = XLColor.FromHtml("#F5F5F5");

            var nombreNegocio = await _tenantDisplayNameService.GetCurrentTenantDisplayNameAsync(HttpContext.RequestAborted);

            // ─────────── Hoja 1: Resumen semanal ───────────
            var wsResumen = workbook.Worksheets.Add("Resumen");
            wsResumen.Range("A1:B1").Merge();
            wsResumen.Cell("A1").Value = nombreNegocio;
            wsResumen.Cell("A1").Style.Font.FontSize = 18;
            wsResumen.Cell("A1").Style.Font.Bold = true;
            wsResumen.Cell("A1").Style.Font.FontColor = colorDorado;

            wsResumen.Cell("A2").Value = tituloPeriodo;
            wsResumen.Cell("A2").Style.Font.FontSize = 13;
            wsResumen.Cell("A2").Style.Font.Bold = true;

            wsResumen.Cell("A3").Value = $"Periodo: {inicioSemana:dd/MM/yyyy} - {finSemana:dd/MM/yyyy}";

            var resumenRows = new (string Label, decimal Value, bool Destacar)[]
            {
                ("Total cobrado a clientes", resumen.TotalGeneradoGeneral, false),
                ("Base ventas sin IVA", resumen.TotalBaseVentaSinIvaGeneral, false),
                ("IVA de venta incluido", resumen.TotalIvaVentaIncluidoGeneral, false),
                ("Base de comisión", resumen.TotalBaseComisionGeneral, false),
                ("IVA colaborador", resumen.TotalIvaColaboradorGeneral, false),
                ("IVA neto negocio", resumen.TotalIvaNetoNegocioGeneral, false),
                ("Total planilla equipo", resumen.TotalAPagarColaboradoresGeneral, true),
                ("Total pagado", resumen.TotalPagadoGeneral, false),
                ("Aplicado a planilla", resumen.TotalPagadoAplicadoGeneral, false),
                ("Pendiente", resumen.TotalPendienteGeneral, false),
                ("Excedente", resumen.TotalExcedenteGeneral, false),
                ("Ganancia negocio estimada", resumen.GananciaNegocio, true),
            };

            var rr = 5;
            foreach (var (label, value, destacar) in resumenRows)
            {
                wsResumen.Cell(rr, 1).Value = label;
                wsResumen.Cell(rr, 1).Style.Font.Bold = true;
                wsResumen.Cell(rr, 2).Value = value;
                wsResumen.Cell(rr, 2).Style.NumberFormat.Format = excelCurrencyFormat;
                if (destacar)
                {
                    wsResumen.Cell(rr, 2).Style.Font.Bold = true;
                    wsResumen.Cell(rr, 2).Style.Font.FontColor = colorDorado;
                }
                rr++;
            }
            wsResumen.Columns().AdjustToContents();

            // ─────────── Hoja 2: Funcionarios ───────────
            var ws = workbook.Worksheets.Add("Funcionarios");

            var headers = new[]
            {
                "Colaborador", "Relación", "Comisión sobre", "Modalidad IVA colaborador",
                "Total cobrado", "Base sin IVA venta", "IVA de venta",
                "% Servicio", "% Producto",
                "Monto colaborador", "Base colaborador", "IVA colaborador", "Total a pagar",
                "Pagado", "Pendiente", "IVA neto negocio"
            };
            const int cols = 16;
            var columnasCrc = new[] { 5, 6, 7, 10, 11, 12, 13, 14, 15, 16 };

            ws.Range(1, 1, 1, cols).Merge();
            ws.Cell(1, 1).Value = $"{nombreNegocio} — {tituloPeriodo} {inicioSemana:dd/MM/yyyy} al {finSemana:dd/MM/yyyy}";
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontColor = colorDorado;
            ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            const int headerRow = 3;
            for (var i = 0; i < headers.Length; i++)
            {
                ws.Cell(headerRow, i + 1).Value = headers[i];
            }

            var header = ws.Range(headerRow, 1, headerRow, cols);
            header.Style.Fill.BackgroundColor = colorNegro;
            header.Style.Font.FontColor = XLColor.White;
            header.Style.Font.Bold = true;
            header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            var fila = headerRow + 1;
            foreach (var f in resumen.Funcionarios)
            {
                ws.Cell(fila, 1).Value = f.Nombre;
                ws.Cell(fila, 2).Value = FuncionarioLiquidacionDisplay.Relacion(f.TipoRelacionColaborador);
                ws.Cell(fila, 3).Value = FuncionarioLiquidacionDisplay.ComisionSobre(f.ComisionCalculadaSobre);
                ws.Cell(fila, 4).Value = FuncionarioLiquidacionDisplay.Modalidad(f.ModalidadIvaColaborador);
                ws.Cell(fila, 5).Value = f.TotalCobrado;
                ws.Cell(fila, 6).Value = f.BaseVentaSinIva;
                ws.Cell(fila, 7).Value = f.IvaVentaIncluido;
                ws.Cell(fila, 8).Value = f.Porcentaje;
                ws.Cell(fila, 9).Value = f.PorcentajeProducto;
                ws.Cell(fila, 10).Value = f.MontoColaborador;
                ws.Cell(fila, 11).Value = f.BaseColaborador;
                ws.Cell(fila, 12).Value = f.IvaColaborador;
                ws.Cell(fila, 13).Value = f.TotalAPagarColaborador;
                ws.Cell(fila, 14).Value = f.MontoPagado;
                ws.Cell(fila, 15).Value = f.MontoPendiente;
                ws.Cell(fila, 16).Value = f.IvaNetoNegocio;

                foreach (var mc in columnasCrc)
                {
                    ws.Cell(fila, mc).Style.NumberFormat.Format = excelCurrencyFormat;
                }
                fila++;
            }

            if (fila > headerRow + 1)
            {
                var dataRange = ws.Range(headerRow + 1, 1, fila - 1, cols);
                dataRange.AddConditionalFormat()
                    .WhenIsTrue("MOD(ROW(),2)=0")
                    .Fill.SetBackgroundColor(colorGris);

                // Totales destacados de las columnas monetarias clave.
                ws.Cell(fila, 3).Value = "TOTALES";
                ws.Cell(fila, 3).Style.Font.Bold = true;
                foreach (var mc in new[] { 5, 6, 7, 13, 14, 15, 16 })
                {
                    var col = ws.Cell(headerRow + 1, mc).Address.ColumnLetter;
                    ws.Cell(fila, mc).FormulaA1 = $"SUM({col}{headerRow + 1}:{col}{fila - 1})";
                    ws.Cell(fila, mc).Style.NumberFormat.Format = excelCurrencyFormat;
                    ws.Cell(fila, mc).Style.Font.Bold = true;
                    ws.Cell(fila, mc).Style.Font.FontColor = colorDorado;
                }
            }
            ws.Columns().AdjustToContents();

            // ─────────── Hoja 3: Detalle de productos vendidos ───────────
            var wsDet = workbook.Worksheets.Add("Detalle productos");
            wsDet.Cell(1, 1).Value = "Productos vendidos en la semana";
            wsDet.Cell(1, 1).Style.Font.Bold = true;
            wsDet.Cell(1, 1).Style.Font.FontSize = 13;

            const int detHeaderRow = 3;
            var detHeaders = new[] { "Funcionario", "Fecha", "Producto", "Precio", "Comisión funcionario" };
            for (var i = 0; i < detHeaders.Length; i++)
            {
                wsDet.Cell(detHeaderRow, i + 1).Value = detHeaders[i];
            }
            var detHeader = wsDet.Range(detHeaderRow, 1, detHeaderRow, detHeaders.Length);
            detHeader.Style.Fill.BackgroundColor = colorNegro;
            detHeader.Style.Font.FontColor = XLColor.White;
            detHeader.Style.Font.Bold = true;

            var detFila = detHeaderRow + 1;
            foreach (var f in resumen.Funcionarios)
            {
                foreach (var producto in f.ProductosVendidos)
                {
                    wsDet.Cell(detFila, 1).Value = f.Nombre;
                    wsDet.Cell(detFila, 2).Value = producto.Fecha;
                    wsDet.Cell(detFila, 3).Value = producto.NombreProducto;
                    wsDet.Cell(detFila, 4).Value = producto.Precio;
                    wsDet.Cell(detFila, 5).Value = producto.GananciaFuncionario;

                    wsDet.Cell(detFila, 2).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
                    wsDet.Cell(detFila, 4).Style.NumberFormat.Format = excelCurrencyFormat;
                    wsDet.Cell(detFila, 5).Style.NumberFormat.Format = excelCurrencyFormat;
                    detFila++;
                }
            }

            if (detFila == detHeaderRow + 1)
            {
                wsDet.Cell(detFila, 1).Value = "No hubo productos vendidos en la semana.";
            }
            wsDet.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            return File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ExcelReportFileNameBuilder.Build(
                    nombreNegocio,
                    periodoTipo switch
                    {
                        PayrollPeriodType.Quincenal => "Liquidacion Quincenal",
                        PayrollPeriodType.Personalizado => "Liquidacion Rango",
                        _ => "Liquidacion Semanal"
                    },
                    generatedAt));
        }

        private async Task<IActionResult> SetActivoAsync(int id, bool activo)
        {
            var funcionario = await _context.Funcionarios
                .FirstOrDefaultAsync(f => f.IdFuncionario == id);

            if (funcionario == null)
            {
                return NotFound();
            }

            if (funcionario.Activo == activo)
            {
                TempData["Mensaje"] = activo
                    ? "El funcionario ya estaba activo."
                    : "El funcionario ya estaba inactivo.";
                return RedirectToAction(nameof(Index));
            }

            if (activo)
            {
                var capacidad = await ValidateActiveFuncionarioCapacityAsync();
                if (!capacidad.IsAllowed)
                {
                    TempData["Error"] = capacidad.Message;
                    return RedirectToAction(nameof(Index));
                }

                var puestoActivo = await _context.Puestos
                    .AsNoTracking()
                    .AnyAsync(p => p.IdPuesto == funcionario.IdPuesto && p.Activo);

                if (!puestoActivo)
                {
                    TempData["Error"] = "No se puede activar el funcionario porque su puesto actual esta inactivo o ya no existe en este tenant.";
                    return RedirectToAction(nameof(Index));
                }
            }

            funcionario.Activo = activo;

            try
            {
                await _context.SaveChangesAsync();
                TempData["Mensaje"] = activo
                    ? "Funcionario activado."
                    : "Funcionario desactivado.";
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al cambiar el estado del funcionario {FuncionarioId}.", id);
                TempData["Error"] = "No fue posible actualizar el estado del funcionario.";
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo el cambio de estado del funcionario {FuncionarioId}.", id);
                TempData["Error"] = "No fue posible actualizar el estado del funcionario por una validacion de seguridad o consistencia.";
            }

            return RedirectToAction(nameof(Index));
        }

        private async Task CargarPuestosAsync(int? selectedPuestoId = null)
        {
            ViewBag.Puestos = await _context.Puestos
                .AsNoTracking()
                .Where(p => p.Activo || (selectedPuestoId.HasValue && p.IdPuesto == selectedPuestoId.Value))
                .OrderBy(p => p.NombrePuesto)
                .ToListAsync();
        }

        private async Task ValidatePuestoAsync(int puestoId, int? currentPuestoId = null)
        {
            var puesto = await _context.Puestos
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.IdPuesto == puestoId);

            if (puesto == null)
            {
                ModelState.AddModelError(nameof(Funcionario.IdPuesto), "El puesto seleccionado no existe o no pertenece al tenant actual.");
                return;
            }

            if (!puesto.Activo && currentPuestoId != puestoId)
            {
                ModelState.AddModelError(nameof(Funcionario.IdPuesto), "El puesto seleccionado esta inactivo. Selecciona un puesto activo del tenant actual.");
            }
        }

        /// <summary>
        /// Enforcement del limite de funcionarios. Lee el limite EFECTIVO del resolver de acceso
        /// comercial (mismo campo que muestra /Billing/Suscripcion), de modo que un tenant exento
        /// con plan forzado LC_M_03 se valide contra 3 y no contra el MaxFuncionarios de una fila
        /// de Suscripciones vieja.
        /// </summary>
        private async Task<(bool IsAllowed, string Message)> ValidateActiveFuncionarioCapacityAsync()
        {
            if (UserIsPlatformSuperAdmin())
            {
                return (true, string.Empty);
            }

            var access = ResolveCommercialAccess();
            if (access is null)
            {
                return (false, "No fue posible resolver el acceso comercial del tenant para validar el limite de funcionarios.");
            }

            var limit = access.EffectiveEmployeeLimit;
            if (!limit.HasValue)
            {
                // Sin plan efectivo resuelto no se puede validar (fail-closed); con plan efectivo y
                // limite null el plan es ilimitado y se permite.
                if (!access.HasEffectivePlan)
                {
                    return (false, "No fue posible resolver el plan efectivo del tenant para validar el limite de funcionarios.");
                }

                return (true, string.Empty);
            }

            var totalActivos = await _context.Funcionarios
                .AsNoTracking()
                .CountAsync(f => f.Activo);

            if (totalActivos >= limit.Value)
            {
                _logger.LogWarning(
                    "Limite de funcionarios excedido. PlanId {PlanId}. PlanNombre {PlanNombre}. Forzado {Forzado}. MaxFuncionarios {MaxFuncionarios}. TotalActivos {TotalActivos}.",
                    access.EffectivePlanId,
                    access.EffectivePlanName,
                    access.IsForcedByPlatform,
                    limit.Value,
                    totalActivos);

                return (false, $"Tu plan actual permite hasta {limit.Value} funcionarios. Para agregar mas, actualiza tu plan.");
            }

            return (true, string.Empty);
        }

        private TenantCommercialAccessResult? ResolveCommercialAccess()
        {
            if (HttpContext.Items.TryGetValue("TenantCommercialAccess", out var rawAccess) &&
                rawAccess is TenantCommercialAccessResult access)
            {
                return access;
            }

            _logger.LogWarning(
                "No fue posible resolver TenantCommercialAccess para crear funcionario. UserId {UserId}.",
                User.FindFirstValue(CustomClaimTypes.UserId) ?? User.FindFirstValue(ClaimTypes.NameIdentifier));

            return null;
        }

        private bool UserIsPlatformSuperAdmin() =>
            string.Equals(
                User.FindFirstValue(CustomClaimTypes.PlatformSuperAdmin),
                bool.TrueString,
                StringComparison.OrdinalIgnoreCase);

        private static void NormalizeFuncionario(Funcionario funcionario)
        {
            funcionario.Nombre = NormalizeRequiredText(funcionario.Nombre);
            funcionario.Telefono = NormalizeOptionalText(funcionario.Telefono);
        }

        /// <summary>
        /// Mantiene coherente la configuración fiscal del colaborador:
        ///  - El flag histórico <c>RebajarImpuestosAntesDeComision</c> se deriva de
        ///    <c>ComisionCalculadaSobre</c> (BaseSinIva ↔ true).
        ///  - El IVA de factura del colaborador sólo aplica a Independientes; en cualquier otro
        ///    caso se apaga para evitar datos inconsistentes.
        /// </summary>
        private static void SincronizarConfigFiscal(Funcionario funcionario)
        {
            funcionario.RebajarImpuestosAntesDeComision =
                funcionario.ComisionCalculadaSobre == LuxuryApp.Models.Fiscal.ComisionCalculadaSobre.BaseSinIva;

            // La modalidad de IVA solo aplica a Independientes; en cualquier otro caso se apaga.
            if (funcionario.TipoRelacionColaborador != LuxuryApp.Models.Fiscal.TipoRelacionColaborador.Independiente)
            {
                funcionario.ModalidadIvaColaborador = LuxuryApp.Models.Fiscal.ModalidadIvaColaborador.NoFactura;
            }

            // Flag histórico derivado de la modalidad (fuente de verdad).
            funcionario.ColaboradorFacturaIva =
                funcionario.ModalidadIvaColaborador != LuxuryApp.Models.Fiscal.ModalidadIvaColaborador.NoFactura;
        }

        private static string NormalizeRequiredText(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : CollapseWhitespace(value);

        private static string? NormalizeOptionalText(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? null
                : CollapseWhitespace(value);

        private static string CollapseWhitespace(string value) =>
            string.Join(
                " ",
                value.Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        private static List<string> ObtenerMetodosPago() =>
            LiquidacionSemanalDefaults.MetodosPagoPermitidos.ToList();

        /// <summary>
        /// ¿Este guardado cambia la remuneración de un colaborador que ya tiene producción?
        /// Se compara contra la fila real —no contra lo que diga el formulario— y solo se avisa si
        /// algo de la liquidación cambió de verdad: corregirle el teléfono no recalcula nada.
        /// </summary>
        private async Task<bool> RequiereConfirmacionHistoricaAsync(
            Funcionario entrante,
            Funcionario actual,
            bool confirmado)
        {
            if (confirmado)
            {
                return false;
            }

            var cambioRemuneracion =
                actual.PorcentajeGanancia != entrante.PorcentajeGanancia ||
                actual.PorcentajeProducto != entrante.PorcentajeProducto ||
                actual.ComisionCalculadaSobre != entrante.ComisionCalculadaSobre ||
                actual.TipoRelacionColaborador != entrante.TipoRelacionColaborador ||
                actual.ModalidadIvaColaborador != entrante.ModalidadIvaColaborador ||
                actual.TarifaIvaFacturaColaborador != entrante.TarifaIvaFacturaColaborador;

            if (!cambioRemuneracion)
            {
                return false;
            }

            var produccion = await _impactoHistorico.ContarProduccionHistoricaDeColaboradorAsync(
                entrante.IdFuncionario, HttpContext.RequestAborted);

            if (produccion == 0)
            {
                return false;
            }

            ViewData[LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.CampoConfirmacion] =
                LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.Colaborador(produccion);

            ModelState.AddModelError(
                string.Empty,
                LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.Confirmacion);

            return true;
        }
    }
}
