using LuxuryApp.Controllers;
using LuxuryApp.Controllers.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// UI/controller de la Fase 2 (cancelar/pausar/reactivar). No re-testea la lógica del
    /// ProviderSubscriptionManager (ya cubierta): verifica visibilidad del botón, autorización,
    /// CSRF y la confirmación fuerte de la cancelación inmediata.
    /// </summary>
    public class SubscriptionLifecycleUiTests
    {
        // ── Visibilidad del botón "Cancelar suscripción" (viewmodel) ──

        [Fact]
        public void CanRequestCancellation_ActiveRecurring_IsTrue()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = false
            };
            Assert.True(vm.CanRequestCancellation);
        }

        [Fact]
        public void CanRequestCancellation_MorosaRecurring_IsTrue()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Morosa,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = false
            };
            Assert.True(vm.CanRequestCancellation);
        }

        [Fact]
        public void CanRequestCancellation_AlreadyCancelAtPeriodEnd_IsFalse()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = true
            };
            Assert.False(vm.CanRequestCancellation);
        }

        [Fact]
        public void CanRequestCancellation_NotRecurring_IsFalse()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = false
            };
            Assert.False(vm.CanRequestCancellation);
        }

        [Fact]
        public void CanRequestCancellation_Suspended_IsFalse()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Suspendida,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = false
            };
            Assert.False(vm.CanRequestCancellation);
        }

        // ── Botón "Reactivar renovación" (Caso B) ──

        [Fact]
        public void CanReactivateRenewal_CancelledStillActive_IsTrue()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = true
            };
            Assert.True(vm.CanReactivateRenewal);
            Assert.False(vm.CanRequestCancellation); // no se ofrece cancelar de nuevo
        }

        [Fact]
        public void CanReactivateRenewal_CancelledButExpired_IsFalse()
        {
            // Estado efectivo Suspendida = período vencido (Caso C): se ofrece suscribirse de nuevo, no reactivar.
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Suspendida,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = true
            };
            Assert.False(vm.CanReactivateRenewal);
        }

        [Fact]
        public void CanReactivateRenewal_NotCancelled_IsFalse()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = false
            };
            Assert.False(vm.CanReactivateRenewal);
        }

        // ── Renovación pausada por soporte: no se ofrece cancelar en línea ──

        [Fact]
        public void CanRequestCancellation_WhenRenewalPaused_IsFalse()
        {
            // Pausada por soporte/plataforma: el cliente NO cancela en línea (mezcla estados).
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = false,
                IsRenewalPaused = true
            };
            Assert.False(vm.CanRequestCancellation);
        }

        [Fact]
        public void CanRequestCancellation_WhenNotPaused_IsTrue()
        {
            var vm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Activa,
                IsRecurringTilopay = true,
                CancelAtPeriodEnd = false,
                IsRenewalPaused = false
            };
            Assert.True(vm.CanRequestCancellation);
        }

        [Fact]
        public void SyncProviderStatus_RequiresPostAndAntiForgery()
        {
            var method = typeof(PlatformProviderSubscriptionController).GetMethod(
                nameof(PlatformProviderSubscriptionController.SyncProviderStatus));

            Assert.NotNull(method);
            Assert.NotEmpty(method!.GetCustomAttributes(typeof(HttpPostAttribute), true));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }

        // ── Autorización + CSRF (los requisitos del spec, verificados por atributos) ──

        [Fact]
        public void BillingController_RequiresTenantAdminRole()
        {
            var authorize = (AuthorizeAttribute?)typeof(BillingController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), true)
                .FirstOrDefault();

            Assert.NotNull(authorize);
            Assert.Equal("Administrador", authorize!.Roles);
        }

        [Fact]
        public void CancelarRenovacion_RequiresPostAndAntiForgery()
        {
            var method = typeof(BillingController).GetMethod(nameof(BillingController.CancelarRenovacion));

            Assert.NotNull(method);
            Assert.NotEmpty(method!.GetCustomAttributes(typeof(HttpPostAttribute), true));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }

        [Fact]
        public void ReactivarRenovacion_RequiresPostAndAntiForgery()
        {
            var method = typeof(BillingController).GetMethod(nameof(BillingController.ReactivarRenovacion));

            Assert.NotNull(method);
            Assert.NotEmpty(method!.GetCustomAttributes(typeof(HttpPostAttribute), true));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }

        [Fact]
        public void PlatformProviderSubscriptionController_RequiresSuperAdmin()
        {
            var authorize = (AuthorizeAttribute?)typeof(PlatformProviderSubscriptionController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), true)
                .FirstOrDefault();

            Assert.NotNull(authorize);
            Assert.Equal(PlatformAuthorizationPolicies.PlatformSuperAdmin, authorize!.Policy);
        }

        [Theory]
        [InlineData("CancelRenovacion")]
        [InlineData("Cancel")]
        [InlineData("Pause")]
        [InlineData("Reactivate")]
        [InlineData("ReactivateRenovacion")]
        [InlineData("SyncProviderStatus")]
        public void PlatformLifecycleActions_RequireAntiForgery(string methodName)
        {
            var method = typeof(PlatformProviderSubscriptionController).GetMethod(methodName);

            Assert.NotNull(method);
            Assert.NotEmpty(method!.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }

        // ── Cancelación inmediata: requiere escribir CANCELAR (defensa server-side) ──

        [Fact]
        public async Task ImmediateCancel_WithoutConfirmWord_IsRejectedWithoutActing()
        {
            var recording = new RecordingProviderSubscriptionManager();
            var controller = new PlatformProviderSubscriptionController(
                recording, null!, null!, new DisabledTilopayRepeatAdminService())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
            controller.TempData = new TempDataDictionary(controller.HttpContext, new NullTempDataProvider());

            var result = await controller.Cancel(Guid.NewGuid(), confirm: "por favor", CancellationToken.None);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Manage", redirect.ActionName);
            Assert.True(controller.TempData.ContainsKey("PlatformError"));
            Assert.Equal(0, recording.CancelCalls); // no se ejecutó la cancelación
        }

        private sealed class NullTempDataProvider : ITempDataProvider
        {
            public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
            public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
        }

        private sealed class RecordingProviderSubscriptionManager : LuxuryApp.Services.Billing.IProviderSubscriptionManager
        {
            public int CancelCalls;

            public bool IsEnabled => true;

            public Task<LuxuryApp.Services.Billing.ProviderCancellationAttemptResult> TryCancelOldSubscriberForUpgradeAsync(
                Guid tenantId, Guid? intentId = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(LuxuryApp.Services.Billing.ProviderCancellationAttemptResult.NotCalled("no-op"));

            public Task<LuxuryApp.Services.Billing.ProviderSubscriptionActionResult> RequestCancellationAtPeriodEndAsync(
                Guid tenantId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default) =>
                Task.FromResult(LuxuryApp.Services.Billing.ProviderSubscriptionActionResult.Ok("ok"));

            public Task<LuxuryApp.Services.Billing.ProviderSubscriptionActionResult> CancelAsync(
                Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default)
            {
                CancelCalls++;
                return Task.FromResult(LuxuryApp.Services.Billing.ProviderSubscriptionActionResult.Ok("cancelled"));
            }

            public Task<LuxuryApp.Services.Billing.ProviderSubscriptionActionResult> PauseAsync(
                Guid tenantId, string actorUserId, string actorEmail, bool immediate = false, CancellationToken cancellationToken = default) =>
                Task.FromResult(LuxuryApp.Services.Billing.ProviderSubscriptionActionResult.Ok("paused"));

            public Task<LuxuryApp.Services.Billing.ProviderSubscriptionActionResult> ReactivateAsync(
                Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default) =>
                Task.FromResult(LuxuryApp.Services.Billing.ProviderSubscriptionActionResult.Ok("reactivated"));

            public int ReactivateRenewalCalls;

            public Task<LuxuryApp.Services.Billing.ProviderSubscriptionActionResult> ReactivateRenewalAsync(
                Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default)
            {
                ReactivateRenewalCalls++;
                return Task.FromResult(LuxuryApp.Services.Billing.ProviderSubscriptionActionResult.Ok("renewal reactivated"));
            }

            public int SyncProviderStatusCalls;

            public Task<LuxuryApp.Services.Billing.ProviderSubscriptionActionResult> SyncProviderStatusAsync(
                Guid tenantId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default)
            {
                SyncProviderStatusCalls++;
                return Task.FromResult(LuxuryApp.Services.Billing.ProviderSubscriptionActionResult.Ok("synced"));
            }
        }
    }
}
