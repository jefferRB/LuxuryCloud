using LuxuryApp.Models.Asociados;
using System.Security.Claims;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Inversionistas;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Inversionistas
{
    /// <summary>
    /// Módulo de inversionistas: acuerdos de participación, estados de cuenta, pagos y envíos.
    ///
    /// <para>
    /// Solo administradores del tenant. Un inversionista NO es usuario del sistema y en esta fase
    /// no existe portal para él: solo recibe el correo con su estado de cuenta.
    /// </para>
    ///
    /// <para>
    /// El controlador no calcula dinero: todo el cálculo vive en
    /// <c>IPeriodProfitCalculationService</c> y <see cref="IInvestorStatementService"/>.
    /// </para>
    /// </summary>
    [Authorize]
    [RequirePermission(AppPermissions.AssociatesView)]
    public class InversionistasController : Controller
    {
        private readonly IInvestorService _investorService;
        private readonly IInvestorStatementService _statementService;
        private readonly IInvestorCycleService _cycleService;
        private readonly IInvestorStatementEmailService _emailService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public InversionistasController(
            IInvestorService investorService,
            IInvestorStatementService statementService,
            IInvestorCycleService cycleService,
            IInvestorStatementEmailService emailService,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _investorService = investorService;
            _statementService = statementService;
            _cycleService = cycleService;
            _emailService = emailService;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        // ─────────────── Compatibilidad con las rutas anteriores ───────────────
        // La gestión de la PERSONA (alta, edición, estado) se mudó a /Asociados: allí conviven su
        // identidad, su acceso, sus permisos y su participación. Acá queda lo que es propio del
        // dinero del inversionista: política de cálculo, estados de cuenta, pagos y envíos.
        // Los enlaces y favoritos viejos siguen funcionando en vez de dar 404.

        public IActionResult Index() => RedirectToActionPermanent("Index", "Asociados");

        [HttpGet]
        public IActionResult Crear() => RedirectToActionPermanent("Crear", "Asociados");

        /// <summary>
        /// Editar un inversionista ahora es editar a su asociado. Si el perfil todavía no está
        /// enlazado (dato anterior a la migración), se cae al listado en vez de romper.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Editar(int id, CancellationToken cancellationToken)
        {
            var associateId = await _investorService.GetAssociateIdAsync(id, cancellationToken);

            return associateId.HasValue
                ? RedirectToActionPermanent("Detalle", "Asociados", new { id = associateId.Value })
                : RedirectToActionPermanent("Index", "Asociados");
        }

        // ─────────────── Política de cálculo ───────────────

        [HttpGet]
        public async Task<IActionResult> Politica(CancellationToken cancellationToken)
        {
            var vm = await _investorService.BuildPolicyFormAsync(cancellationToken);
            return View(vm);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Politica(InvestorPolicyViewModel form, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                var recargada = await _investorService.BuildPolicyFormAsync(cancellationToken);
                form.Categorias = recargada.Categorias;
                return View(form);
            }

            await _investorService.SavePolicyAsync(form, CurrentUserId(), cancellationToken);
            TempData["Mensaje"] = "Configuración de la ganancia distribuible guardada.";
            return RedirectToAction(nameof(Politica));
        }

        // ─────────────── Estados de cuenta ───────────────

        [HttpGet]
        public async Task<IActionResult> Estados(
            int? inversionistaId,
            InvestorStatementStatus? estado,
            DateTime? desde,
            DateTime? hasta,
            CancellationToken cancellationToken)
        {
            var filtro = new InvestorStatementFilter
            {
                InvestorId = inversionistaId,
                Estado = estado,
                Desde = desde.HasValue ? DateOnly.FromDateTime(desde.Value.Date) : null,
                Hasta = hasta.HasValue ? DateOnly.FromDateTime(hasta.Value.Date) : null
            };

            var vm = await _statementService.BuildStatementsPageAsync(filtro, cancellationToken);
            return View(vm);
        }

        /// <summary>
        /// CICLO EN CURSO: el período abierto, calculado hasta hoy. No es un corte y no genera
        /// nada; es el flujo principal para responder "¿cuánto lleva acumulado?".
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> CicloActual(int id, CancellationToken cancellationToken)
        {
            // El resumen trae el ciclo Y el último corte emitido: así la pantalla puede ofrecer
            // "Ver último corte" e "Historial" sin que el usuario escriba ninguna fecha.
            var resumen = await _cycleService.BuildSummaryAsync(id, cancellationToken);
            return resumen is null ? NotFound() : View(resumen);
        }

        /// <summary>
        /// Consulta avanzada: recalcula CUALQUIER período a partir de una fecha. Se conserva para
        /// revisar historia o generar un corte viejo a mano, pero ya no es el flujo principal.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> VistaPrevia(int id, DateTime? referencia, CancellationToken cancellationToken)
        {
            try
            {
                var vm = await _statementService.PreviewAsync(
                    id,
                    referencia.HasValue ? DateOnly.FromDateTime(referencia.Value.Date) : null,
                    cancellationToken);

                return View(vm);
            }
            catch (InvestorValidationException ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Generar(int id, DateTime referencia, CancellationToken cancellationToken)
        {
            try
            {
                var statementId = await _statementService.GenerateDraftAsync(
                    id,
                    DateOnly.FromDateTime(referencia.Date),
                    CurrentUserId(),
                    cancellationToken);

                TempData["Mensaje"] = "Borrador del estado de cuenta generado.";
                return RedirectToAction(nameof(Estado), new { id = statementId });
            }
            catch (InvestorValidationException ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(VistaPrevia), new { id, referencia });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Estado(int id, CancellationToken cancellationToken)
        {
            var vm = await _statementService.BuildDetailAsync(id, cancellationToken);
            return vm is null ? NotFound() : View(vm);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Recalcular(int id, CancellationToken cancellationToken) =>
            ExecuteAsync(
                () => _statementService.RecalculateAsync(id, CurrentUserId(), cancellationToken),
                "Estado de cuenta recalculado con los datos actuales.",
                id);

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Finalizar(int id, CancellationToken cancellationToken) =>
            ExecuteAsync(
                () => _statementService.FinalizeAsync(id, CurrentUserId(), cancellationToken),
                "Estado de cuenta finalizado. Sus valores quedaron congelados.",
                id);

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Anular(int id, string motivo, CancellationToken cancellationToken) =>
            ExecuteAsync(
                () => _statementService.VoidAsync(id, motivo, CurrentUserId(), cancellationToken),
                "Estado de cuenta anulado.",
                id);

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Reabrir(int id, string motivo, CancellationToken cancellationToken) =>
            ExecuteAsync(
                () => _statementService.ReopenAsync(id, motivo, CurrentUserId(), cancellationToken),
                "Estado de cuenta reabierto como borrador.",
                id);

        // ─────────────── Ajustes ───────────────

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> AgregarAjuste(
            InvestorAdjustmentFormViewModel form,
            CancellationToken cancellationToken) =>
            ExecuteAsync(
                () => _statementService.AddAdjustmentAsync(form, CurrentUserId(), CurrentUserEmail(), cancellationToken),
                "Ajuste registrado.",
                form.StatementId);

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuitarAjuste(int id, int statementId, CancellationToken cancellationToken)
        {
            try
            {
                await _statementService.RemoveAdjustmentAsync(id, CurrentUserId(), cancellationToken);
                TempData["Mensaje"] = "Ajuste eliminado.";
            }
            catch (InvestorValidationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Estado), new { id = statementId });
        }

        // ─────────────── Pagos ───────────────

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> RegistrarPago(
            InvestorPaymentFormViewModel form,
            CancellationToken cancellationToken) =>
            ExecuteAsync(
                () => _statementService.RegisterPaymentAsync(form, CurrentUserId(), CurrentUserEmail(), cancellationToken),
                "Pago registrado.",
                form.StatementId);

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevertirPago(
            int id,
            int statementId,
            string motivo,
            CancellationToken cancellationToken)
        {
            try
            {
                await _statementService.ReversePaymentAsync(
                    id,
                    motivo,
                    CurrentUserId(),
                    CurrentUserEmail(),
                    cancellationToken);

                TempData["Mensaje"] = "Pago corregido. Queda registrado el movimiento compensatorio.";
            }
            catch (InvestorValidationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Estado), new { id = statementId });
        }

        // ─────────────── Envíos y PDF ───────────────

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Enviar(int id, CancellationToken cancellationToken) =>
            SendAsync(() => _emailService.SendAsync(id, CurrentUserId(), cancellationToken), id);

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Reenviar(int id, CancellationToken cancellationToken) =>
            SendAsync(() => _emailService.ResendAsync(id, CurrentUserId(), cancellationToken), id);

        [HttpPost]
        [RequirePermission(AppPermissions.AssociatesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> EnviarPrueba(int id, string correo, CancellationToken cancellationToken) =>
            SendAsync(() => _emailService.SendTestAsync(id, correo, CurrentUserId(), cancellationToken), id);

        [HttpGet]
        public async Task<IActionResult> DescargarPdf(int id, CancellationToken cancellationToken)
        {
            var pdf = await _emailService.BuildPdfAsync(id, cancellationToken);
            if (pdf is null)
            {
                return NotFound();
            }

            return File(pdf.Value.Content, "application/pdf", pdf.Value.FileName);
        }

        // ─────────────── Helpers ───────────────

        private async Task<IActionResult> ExecuteAsync(Func<Task> action, string mensajeExito, int statementId)
        {
            try
            {
                await action();
                TempData["Mensaje"] = mensajeExito;
            }
            catch (InvestorValidationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Estado), new { id = statementId });
        }

        private async Task<IActionResult> SendAsync(
            Func<Task<InvestorStatementSendResult>> action,
            int statementId)
        {
            var resultado = await action();

            if (resultado.Success)
            {
                TempData["Mensaje"] = resultado.Message;
            }
            else
            {
                TempData["Error"] = resultado.Message;
            }

            return RedirectToAction(nameof(Estado), new { id = statementId });
        }

        private string? CurrentUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

        private string? CurrentUserEmail() =>
            User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name;
    }
}
