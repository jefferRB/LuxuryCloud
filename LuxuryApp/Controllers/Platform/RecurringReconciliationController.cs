using System.Security.Claims;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.Platform
{
    [Authorize(Policy = PlatformAuthorizationPolicies.PlatformSuperAdmin)]
    [Route("Platform/RecurringCheckouts")]
    public sealed class RecurringReconciliationController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly SaaSPaymentService _paymentService;
        private readonly UserManager<AppUsuario> _userManager;
        private readonly ILogger<RecurringReconciliationController> _logger;

        public RecurringReconciliationController(
            ApplicationDbContext context,
            SaaSPaymentService paymentService,
            UserManager<AppUsuario> userManager,
            ILogger<RecurringReconciliationController> logger)
        {
            _context = context;
            _paymentService = paymentService;
            _userManager = userManager;
            _logger = logger;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(Guid? paymentId = null, CancellationToken cancellationToken = default)
        {
            if (!HasPlatformSuperAdminClaim())
            {
                return Forbid();
            }

            var model = await BuildPageModelAsync(paymentId, approvalForm: null, cancellationToken);
            return View(model);
        }

        [HttpPost("approve")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(
            [Bind(Prefix = nameof(PlatformRecurringReconciliationPageViewModel.ApprovalForm))]
            PlatformRecurringApprovalFormViewModel approvalForm,
            CancellationToken cancellationToken = default)
        {
            if (!HasPlatformSuperAdminClaim())
            {
                return Forbid();
            }

            if (!ModelState.IsValid)
            {
                var invalidModel = await BuildPageModelAsync(approvalForm.PaymentId, approvalForm, cancellationToken);
                return View("Index", invalidModel);
            }

            var selectedPayment = await _context.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(payment => payment.Plan)
                .FirstOrDefaultAsync(payment => payment.Id == approvalForm.PaymentId, cancellationToken);

            if (selectedPayment is null)
            {
                return NotFound();
            }

            var actor = await _userManager.GetUserAsync(User);

            try
            {
                var result = await _paymentService.ApproveRecurringPaymentAsync(
                    new RecurringPaymentApprovalRequest
                    {
                        PaymentId = approvalForm.PaymentId,
                        ProviderTransactionId = approvalForm.ProviderTransactionId,
                        ProviderSubscriberId = approvalForm.ProviderSubscriberId,
                        ApprovedAmount = approvalForm.ApprovedAmount,
                        Currency = approvalForm.Currency,
                        Observation = approvalForm.Observation,
                        Source = "manual",
                        ActorUserId = actor?.Id ?? User.FindFirstValue(ClaimTypes.NameIdentifier),
                        ActorEmail = actor?.Email ?? User.Identity?.Name,
                        ProviderReference = selectedPayment.ProviderReference ?? selectedPayment.CorrelationToken ?? selectedPayment.ReferenciaInterna
                    },
                    cancellationToken);

                TempData["RecurringReconciliationSuccess"] =
                    $"Pago recurrente aprobado. Estado suscripcion: {result.SubscriptionStatus}. Fecha fin: {result.CurrentPeriodEndUtc:yyyy-MM-dd HH:mm} UTC.";

                _logger.LogInformation(
                    "Conciliacion manual recurrente completada. PaymentId {PaymentId}. TenantId {TenantId}. PlanCode {PlanCode}. ProviderTransactionId {ProviderTransactionId}. ActorUserId {ActorUserId}.",
                    result.PaymentId,
                    result.TenantId,
                    result.PlanCode,
                    result.ProviderTransactionId,
                    actor?.Id ?? User.FindFirstValue(ClaimTypes.NameIdentifier));

                return RedirectToAction(nameof(Index), new { paymentId = approvalForm.PaymentId });
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                var errorModel = await BuildPageModelAsync(approvalForm.PaymentId, approvalForm, cancellationToken);
                return View("Index", errorModel);
            }
        }

        /// <summary>
        /// La policy PlatformSuperAdmin de la clase ya bloquea a cualquier otro usuario;
        /// este check dentro de la acción es defensa en profundidad, no una segunda vía
        /// de acceso (la rama Development+Administrador que existía aquí era inalcanzable).
        /// </summary>
        private bool HasPlatformSuperAdminClaim() =>
            User.HasClaim(CustomClaimTypes.PlatformSuperAdmin, bool.TrueString);

        private async Task<PlatformRecurringReconciliationPageViewModel> BuildPageModelAsync(
            Guid? selectedPaymentId,
            PlatformRecurringApprovalFormViewModel? approvalForm,
            CancellationToken cancellationToken)
        {
            var payments = await _context.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(payment => payment.Plan)
                .Where(payment =>
                    payment.Proveedor == PaymentProviderType.Tilopay &&
                    payment.TilopayRecurringPlanId.HasValue &&
                    (payment.Estado == EstadoPagoProveedor.Pendiente ||
                     payment.Estado == EstadoPagoProveedor.ManualReview))
                .OrderByDescending(payment => payment.FechaCreacionUtc)
                .Take(50)
                .ToListAsync(cancellationToken);

            PagoSuscripcion? selectedPayment = null;
            if (selectedPaymentId.HasValue)
            {
                selectedPayment = await _context.PagosSuscripcion
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Include(payment => payment.Plan)
                    .FirstOrDefaultAsync(payment => payment.Id == selectedPaymentId.Value, cancellationToken);
            }

            selectedPayment ??= payments.FirstOrDefault();

            var visiblePayments = payments;
            if (selectedPayment is not null && visiblePayments.All(payment => payment.Id != selectedPayment.Id))
            {
                visiblePayments = new[] { selectedPayment }
                    .Concat(visiblePayments)
                    .ToList();
            }

            var tenantIds = visiblePayments
                .Select(payment => payment.TenantId)
                .Distinct()
                .ToArray();

            var tenants = await _context.Tenants
                .AsNoTracking()
                .Where(tenant => tenantIds.Contains(tenant.Id))
                .ToDictionaryAsync(tenant => tenant.Id, tenant => tenant.Nombre, cancellationToken);

            var users = await _context.Users
                .AsNoTracking()
                .Where(user => tenantIds.Contains(user.TenantId))
                .Select(user => new TenantUserLookup(user.TenantId, user.Id, user.Email))
                .ToListAsync(cancellationToken);

            var usersByTenant = users
                .GroupBy(user => user.TenantId)
                .ToDictionary(group => group.Key, group => group.OrderBy(user => user.Email).ToArray());

            var items = visiblePayments
                .Select(payment => BuildItem(payment, tenants, usersByTenant))
                .ToArray();

            var selectedItem = selectedPayment is null
                ? null
                : items.FirstOrDefault(item => item.PaymentId == selectedPayment.Id);

            var form = approvalForm ?? new PlatformRecurringApprovalFormViewModel
            {
                PaymentId = selectedItem?.PaymentId ?? Guid.Empty,
                ApprovedAmount = selectedItem?.ExpectedAmount ?? 0m,
                Currency = selectedItem?.Currency ?? "CRC",
                Observation = selectedItem is null
                    ? string.Empty
                    : "Pago aprobado manualmente con evidencia del dashboard sandbox Tilopay."
            };

            return new PlatformRecurringReconciliationPageViewModel
            {
                Items = items,
                SelectedItem = selectedItem,
                ApprovalForm = form
            };
        }

        private static PlatformRecurringReconciliationItemViewModel BuildItem(
            PagoSuscripcion payment,
            IReadOnlyDictionary<Guid, string> tenants,
            IReadOnlyDictionary<Guid, TenantUserLookup[]> usersByTenant)
        {
            usersByTenant.TryGetValue(payment.TenantId, out var tenantUsers);
            var exactUser = tenantUsers?.FirstOrDefault(user =>
                string.Equals(user.Email, payment.ClienteEmail, StringComparison.OrdinalIgnoreCase));
            var fallbackUser = exactUser ?? tenantUsers?.FirstOrDefault();

            return new PlatformRecurringReconciliationItemViewModel
            {
                PaymentId = payment.Id,
                TenantId = payment.TenantId,
                TenantName = tenants.TryGetValue(payment.TenantId, out var tenantName)
                    ? tenantName
                    : payment.TenantId.ToString(),
                UserId = exactUser?.UserId ?? fallbackUser?.UserId,
                UserEmail = payment.ClienteEmail ?? exactUser?.Email ?? fallbackUser?.Email,
                PlanName = payment.Plan?.Nombre ?? "Sin plan",
                PlanCode = payment.Plan?.Codigo ?? payment.Plan?.Nombre ?? "Sin codigo",
                TilopayRecurringPlanId = payment.TilopayRecurringPlanId,
                ExpectedAmount = payment.Monto,
                Currency = string.IsNullOrWhiteSpace(payment.Moneda) ? "CRC" : payment.Moneda,
                CorrelationToken = payment.CorrelationToken ?? payment.ProviderReference ?? payment.ReferenciaInterna,
                CreatedUtc = payment.FechaCreacionUtc,
                Status = payment.Estado,
                ProviderResultMessage = payment.ProviderResultMessage,
                ProviderTransactionId = payment.ProviderTransactionId,
                ProviderSubscriberId = payment.ProviderSubscriberId,
                IsAddon = payment.Plan?.Codigo is { } planCode && PlanCodes.WhatsAppAddons.Contains(planCode, StringComparer.OrdinalIgnoreCase)
            };
        }

        private sealed record TenantUserLookup(Guid TenantId, string UserId, string? Email);
    }
}
