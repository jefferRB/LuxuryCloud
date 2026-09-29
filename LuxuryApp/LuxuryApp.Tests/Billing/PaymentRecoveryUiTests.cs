using LuxuryApp.Controllers;
using LuxuryApp.Controllers.Platform;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Identity;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// UI/controller de la Fase 3 (recuperación de pago). No re-testea la lógica de servicios (ya
    /// cubierta en PaymentRecoveryTests): verifica el viewmodel (banners/botón), autorización y CSRF,
    /// la confirmación fuerte del cierre manual y la presencia de los elementos en las vistas.
    /// </summary>
    public class PaymentRecoveryUiTests
    {
        // ── Viewmodel: banners y botón "Actualizar método de pago" ──

        [Fact]
        public void PaymentInGrace_True_WhenGraceActive()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Morosa,
                IsRecurringTilopay = true,
                PaymentRecoveryStatus = "GraceActive"
            };
            Assert.True(vm.PaymentInGrace);
            Assert.False(vm.PaymentGraceExpired);
            Assert.False(vm.PaymentSuspended);
            Assert.True(vm.HasPaymentRecoveryBanner);
        }

        [Fact]
        public void PaymentGraceExpired_True_WhenGraceExpired()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Morosa,
                IsRecurringTilopay = true,
                PaymentRecoveryStatus = "GraceExpired"
            };
            Assert.True(vm.PaymentGraceExpired);
            Assert.False(vm.PaymentInGrace);
        }

        [Fact]
        public void PaymentInGrace_False_WhenGraceWindowEnded_FailSafe()
        {
            // El backend aún dice "GraceActive" pero la fecha de gracia ya venció: la UI NO debe
            // mostrar "en gracia"; debe tratarlo como pago pendiente / gracia vencida.
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Morosa,
                IsRecurringTilopay = true,
                PaymentRecoveryStatus = "GraceActive",
                PaymentGraceWindowEnded = true
            };
            Assert.False(vm.PaymentInGrace);
            Assert.True(vm.PaymentGraceExpired);
        }

        [Fact]
        public void PaymentInGrace_True_WhenGraceActive_AndWindowNotEnded()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Morosa,
                IsRecurringTilopay = true,
                PaymentRecoveryStatus = "GraceActive",
                PaymentGraceWindowEnded = false
            };
            Assert.True(vm.PaymentInGrace);
            Assert.False(vm.PaymentGraceExpired);
        }

        [Fact]
        public void PaymentSuspended_True_WhenSuspended()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Suspendida,
                IsRecurringTilopay = true,
                PaymentRecoveryStatus = "Suspended"
            };
            Assert.True(vm.PaymentSuspended);
            Assert.True(vm.HasPaymentRecoveryBanner);
        }

        [Fact]
        public void Recovery_DoesNotMixWithPause_OrCancellation()
        {
            // Pausa manda: no se muestra el banner de recuperación aunque haya estado de gracia.
            var paused = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true,
                IsRenewalPaused = true,
                PaymentRecoveryStatus = "GraceActive"
            };
            Assert.False(paused.PaymentInGrace);
            Assert.False(paused.HasPaymentRecoveryBanner);

            var cancelled = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = true,
                PaymentRecoveryStatus = "GraceActive"
            };
            Assert.False(cancelled.PaymentInGrace);
        }

        [Fact]
        public void CanUpdatePaymentMethod_True_WhenRecurringActiveOrMorosa()
        {
            Assert.True(new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true
            }.CanUpdatePaymentMethod);

            Assert.True(new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Morosa,
                IsRecurringTilopay = true
            }.CanUpdatePaymentMethod);

            // Suspendida por impago: se ofrece para reactivar.
            Assert.True(new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Suspendida,
                IsRecurringTilopay = true,
                PaymentRecoveryStatus = "Suspended"
            }.CanUpdatePaymentMethod);
        }

        [Fact]
        public void CanUpdatePaymentMethod_False_WhenCancelAtPeriodEnd_OrPaused_OrNotRecurring()
        {
            Assert.False(new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = true // renovación cancelada / provider Delete no recuperable
            }.CanUpdatePaymentMethod);

            Assert.False(new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true,
                IsRenewalPaused = true
            }.CanUpdatePaymentMethod);

            Assert.False(new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = false
            }.CanUpdatePaymentMethod);
        }

        // ── Badge de estado: GraceExpired NO debe decir "En gracia" ──

        [Fact]
        public void PaymentStateBadge_GraceActive_ShowsEnPeriodoDeGracia()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Morosa,
                IsRecurringTilopay = true,
                PaymentRecoveryStatus = "GraceActive"
            };
            Assert.Equal("En período de gracia", vm.PaymentStateBadgeLabel);
        }

        [Fact]
        public void PaymentStateBadge_GraceExpired_ShowsPagoPendiente_NotEnGracia()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Morosa,
                IsRecurringTilopay = true,
                PaymentRecoveryStatus = "GraceExpired"
            };
            Assert.Equal("Pago pendiente", vm.PaymentStateBadgeLabel);
            Assert.DoesNotContain("gracia", vm.PaymentStateBadgeLabel!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PaymentStateBadge_GraceExpiredByFailSafe_ShowsPagoPendiente()
        {
            // Backend aún "GraceActive" pero la ventana ya venció: badge debe ser "Pago pendiente".
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Morosa,
                IsRecurringTilopay = true,
                PaymentRecoveryStatus = "GraceActive",
                PaymentGraceWindowEnded = true
            };
            Assert.Equal("Pago pendiente", vm.PaymentStateBadgeLabel);
        }

        [Fact]
        public void PaymentStateBadge_Suspended_ShowsSuspendidaPorPago()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Suspendida,
                IsRecurringTilopay = true,
                PaymentRecoveryStatus = "Suspended"
            };
            Assert.Equal("Suspendida por pago pendiente", vm.PaymentStateBadgeLabel);
        }

        [Fact]
        public void PaymentStateBadge_Normal_IsNull()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true
            };
            Assert.Null(vm.PaymentStateBadgeLabel);
        }

        // ── Autorización + CSRF ──

        [Fact]
        public void PlatformPaymentRecoveryController_RequiresSuperAdmin()
        {
            var authorize = (AuthorizeAttribute?)typeof(PlatformPaymentRecoveryController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), true)
                .FirstOrDefault();

            Assert.NotNull(authorize);
            Assert.Equal(PlatformAuthorizationPolicies.PlatformSuperAdmin, authorize!.Policy);
        }

        [Theory]
        [InlineData("GenerateUpdateUrl")]
        [InlineData("Resolve")]
        [InlineData("Ignore")]
        public void PlatformPaymentRecoveryActions_RequirePostAndAntiForgery(string methodName)
        {
            var method = typeof(PlatformPaymentRecoveryController).GetMethod(methodName);

            Assert.NotNull(method);
            Assert.NotEmpty(method!.GetCustomAttributes(typeof(HttpPostAttribute), true));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }

        [Fact]
        public void BillingActualizarTarjeta_RequiresPostAndAntiForgery()
        {
            var method = typeof(BillingController).GetMethod(nameof(BillingController.ActualizarTarjeta));

            Assert.NotNull(method);
            Assert.NotEmpty(method!.GetCustomAttributes(typeof(HttpPostAttribute), true));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }

        // ── Cierre manual: requiere escribir RESOLVER (defensa server-side) ──

        [Fact]
        public async Task Resolve_WithoutConfirmWord_IsRejectedWithoutActing()
        {
            var recording = new RecordingRecoveryService();
            var controller = new PlatformPaymentRecoveryController(recording, new StubMethodUpdateService(), null!)
            {
                ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
            controller.TempData = new TempDataDictionary(controller.HttpContext, new NullTempDataProvider());

            var result = await controller.Resolve(Guid.NewGuid(), confirm: "por favor", CancellationToken.None);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirect.ActionName);
            Assert.True(controller.TempData.ContainsKey("PlatformError"));
            Assert.Equal(0, recording.ResolveCalls);
        }

        // ── Presencia en las vistas ──

        [Fact]
        public void BillingSuscripcion_HasRecoveryBannersAndUpdateButton()
        {
            var view = ReadView("Views", "Billing", "Suscripcion.cshtml");
            Assert.Contains("PaymentInGrace", view, StringComparison.Ordinal);
            Assert.Contains("PaymentGraceExpired", view, StringComparison.Ordinal);
            Assert.Contains("PaymentSuspended", view, StringComparison.Ordinal);
            // En recuperación el botón es "Regularizar pago" (url_renew puede cobrar), no "actualizar tarjeta".
            Assert.Contains("Regularizar pago", view, StringComparison.Ordinal);
            Assert.Contains("ActualizarTarjeta", view, StringComparison.Ordinal);
            // Cuenta activa/vigente: NO se ofrece cambiar tarjeta en línea; se sugiere soporte.
            Assert.Contains("ShouldContactSupportToChangeCard", view, StringComparison.Ordinal);
        }

        [Fact]
        public void BillingSuscripcion_GraceExpired_ShowsGraciaVencida_AndBadgeLabel()
        {
            var view = ReadView("Views", "Billing", "Suscripcion.cshtml");
            // El badge principal usa PaymentStateBadgeLabel (no el genérico StatusLabel "En gracia").
            Assert.Contains("PaymentStateBadgeLabel", view, StringComparison.Ordinal);
            // Meta de gracia vencida.
            Assert.Contains("Gracia vencida el", view, StringComparison.Ordinal);
            // Sigue existiendo el caso de gracia vigente.
            Assert.Contains("Gracia hasta", view, StringComparison.Ordinal);
        }

        [Fact]
        public void BillingHealth_HasRecoverySection_AndLink()
        {
            var view = ReadView("Views", "PlatformBillingHealth", "Index.cshtml");
            Assert.Contains("Recuperación de pago", view, StringComparison.Ordinal);
            Assert.Contains("PlatformPaymentRecovery", view, StringComparison.Ordinal);
            Assert.Contains("SuspendedForNonPayment", view, StringComparison.Ordinal);
            Assert.Contains("GraceExpiredNotSuspended", view, StringComparison.Ordinal);
        }

        [Fact]
        public void PlatformPaymentRecoveryView_HasIncidentTableAndActions()
        {
            var view = ReadView("Views", "PlatformPaymentRecovery", "Index.cshtml");
            Assert.Contains("GenerateUpdateUrl", view, StringComparison.Ordinal);
            Assert.Contains("Resolve", view, StringComparison.Ordinal);
            Assert.Contains("Ignore", view, StringComparison.Ordinal);
            Assert.Contains("RESOLVER", view, StringComparison.Ordinal);
        }

        [Fact]
        public void MissionControl_HasPaymentRecoveryLink()
        {
            var view = ReadView("Views", "Platform", "Index.cshtml");
            Assert.Contains("PlatformPaymentRecovery", view, StringComparison.Ordinal);
            Assert.Contains("Recuperación de pago", view, StringComparison.Ordinal);
        }

        [Fact]
        public void PlatformTenants_HasPaymentRecoveryLink()
        {
            var view = ReadView("Views", "Platform", "Tenants.cshtml");
            Assert.Contains("PlatformPaymentRecovery", view, StringComparison.Ordinal);
            Assert.Contains("Recuperación de pago", view, StringComparison.Ordinal);
        }

        // ── Helpers ──

        private static string ReadView(params string[] relativeParts)
        {
            var path = TestProjectPaths.ProjectPath(relativeParts);
            Assert.True(File.Exists(path), $"No se encontró la vista: {path}");
            return File.ReadAllText(path);
        }

        private sealed class NullTempDataProvider : ITempDataProvider
        {
            public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
            public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
        }

        private sealed class StubMethodUpdateService : IPaymentMethodUpdateService
        {
            public bool IsEnabled => true;
            public Task<PaymentMethodUpdateResult> GenerateUpdateUrlAsync(Guid tenantId, string? email, string actorUserId, string actorEmail, CancellationToken cancellationToken = default) =>
                Task.FromResult(PaymentMethodUpdateResult.Ok("https://app.tilopay.com/x"));
            public Task<PaymentMethodUpdateResult> GenerateUpdateUrlForTenantAsync(Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default) =>
                Task.FromResult(PaymentMethodUpdateResult.Ok("https://app.tilopay.com/x"));
            public Task<PaymentMethodUpdateResult> GenerateUpdateUrlForIncidentAsync(Guid incidentId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default) =>
                Task.FromResult(PaymentMethodUpdateResult.Ok("https://app.tilopay.com/x"));
        }

        private sealed class RecordingRecoveryService : IPaymentRecoveryService
        {
            public int ResolveCalls;

            public Task RegisterFailedPaymentAsync(Guid tenantId, int? failedRecurringPlanId, string? providerSubscriberId, string? resultCode, string? resultMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ResolveOnSuccessAsync(Guid tenantId, int? paidRecurringPlanId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RegisterFailedAddonPaymentAsync(Guid tenantId, int? failedRecurringPlanId, string? providerSubscriberId, string? resultCode, string? resultMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ResolveAddonOnSuccessAsync(Guid tenantId, int? paidRecurringPlanId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<int> RunGraceExpirationPassAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
            public Task<IReadOnlyList<PaymentRecoveryConsoleItem>> ListConsoleIncidentsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PaymentRecoveryConsoleItem>>(Array.Empty<PaymentRecoveryConsoleItem>());

            public Task<PaymentRecoveryActionResult> ResolveManuallyAsync(Guid incidentId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default)
            {
                ResolveCalls++;
                return Task.FromResult(PaymentRecoveryActionResult.Ok("ok"));
            }

            public Task<PaymentRecoveryActionResult> IgnoreAsync(Guid incidentId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default) =>
                Task.FromResult(PaymentRecoveryActionResult.Ok("ok"));
            public Task<IReadOnlyList<UnverifiedRecoveryResolutionItem>> ListUnverifiedResolutionsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<UnverifiedRecoveryResolutionItem>>(Array.Empty<UnverifiedRecoveryResolutionItem>());
            public Task<PaymentRecoveryActionResult> ReopenUnverifiedResolutionAsync(Guid incidentId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default) =>
                Task.FromResult(PaymentRecoveryActionResult.Ok("ok"));
        }
    }
}
