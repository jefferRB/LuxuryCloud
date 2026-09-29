using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Ciclo de vida seguro de la suscripción recurrente: cancelar (renovación e inmediata), pausar
    /// y reactivar. La regla de oro es la misma que en el cambio de plan: un HTTP 200 NUNCA basta,
    /// SIEMPRE se verifica contra getSuscriptorRepeat, y lo que no se puede confirmar va a revisión
    /// manual sin tocar el acceso. Todo aislado por tenant y con HTTP fuera de transacción.
    /// </summary>
    public class SubscriptionLifecycleTests
    {
        private const int RecurringPlanId = 7102;
        private const string SubscriberId = "900117";

        // ── Cancelación de renovación (cliente) ──────────────────────────────────────

        [Fact]
        public async Task RequestCancellation_VerifiesDelete_KeepsAccessUntilEffectiveEnd()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            var result = await h.Manager.RequestCancellationAtPeriodEndAsync(
                h.TenantId, "user-1", "user@tenant.cr", "ya no lo uso");

            Assert.True(result.Succeeded);
            Assert.True(h.Admin.DeleteCalls >= 1);

            var sub = await h.GetSubscriptionAsync(id);
            Assert.True(sub.CancelAtPeriodEnd);
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);          // acceso NO se corta
            Assert.True(h.SubscriptionService.CanAccessApp(sub));         // sigue con acceso
            Assert.NotNull(sub.ProviderCancelledAtUtc);                   // baja verificada
            Assert.NotNull(sub.CancellationEffectiveAtUtc);
            Assert.Equal("user-1", sub.CancellationRequestedByUserId);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionProviderCancellationVerified));
            // Al SOLICITAR se registra "programada", NO "finalizada" (eso ocurre al vencer el período).
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionCancellationScheduledAtPeriodEnd));
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.SubscriptionCancellationAtPeriodEndFinalized));
        }

        [Fact]
        public async Task RequestCancellation_Provider200ButStillActive_DoesNotMarkCancelled_ManualReview()
        {
            using var h = await Harness.CreateAsync();
            // TiloPay responde éxito pero NO cambia el estado: el suscriptor sigue Activo.
            h.Admin.DeleteEffective = false;
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            var result = await h.Manager.RequestCancellationAtPeriodEndAsync(
                h.TenantId, "user-1", "user@tenant.cr", null);

            Assert.False(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.False(sub.CancelAtPeriodEnd);                          // NO se marca cancelada
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);
            Assert.Null(sub.ProviderCancelledAtUtc);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionCancellationFailedManualReview));
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.SubscriptionProviderCancellationVerified));
        }

        [Fact]
        public async Task RequestCancellation_AlreadyDelete_IsIdempotent_WithoutReDeleting()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Delete");

            var result = await h.Manager.RequestCancellationAtPeriodEndAsync(
                h.TenantId, "user-1", "user@tenant.cr", null);

            Assert.True(result.Succeeded);
            Assert.Equal(0, h.Admin.DeleteCalls);                         // ya inactivo: no re-llama delete
            var sub = await h.GetSubscriptionAsync(id);
            Assert.True(sub.CancelAtPeriodEnd);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionCancellationAlreadyProviderInactive));
        }

        [Fact]
        public async Task RequestCancellation_DoubleClick_DoesNotDeleteTwice()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            await h.Manager.RequestCancellationAtPeriodEndAsync(h.TenantId, "user-1", "user@tenant.cr", null);
            var firstRequestedAt = (await h.GetSubscriptionAsync(id)).CancellationRequestedAtUtc;

            // Segundo click: el suscriptor ya quedó Delete, así que NO se vuelve a llamar delete.
            await h.Manager.RequestCancellationAtPeriodEndAsync(h.TenantId, "user-1", "user@tenant.cr", null);

            Assert.Equal(1, h.Admin.DeleteCalls);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.True(sub.CancelAtPeriodEnd);
            Assert.Equal(firstRequestedAt, sub.CancellationRequestedAtUtc); // se conserva la primera solicitud
        }

        [Fact]
        public async Task RequestCancellation_DeleteFails_UsesEditFallback_ThenVerifies()
        {
            using var h = await Harness.CreateAsync();
            h.Admin.DeleteSucceeds = false;   // delete falla → fallback editSuscriptorRepeat status=4
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            var result = await h.Manager.RequestCancellationAtPeriodEndAsync(
                h.TenantId, "user-1", "user@tenant.cr", null);

            Assert.True(result.Succeeded);
            Assert.True(h.Admin.DeleteCalls >= 1);
            Assert.True(h.Admin.EditCalls >= 1);                          // se usó el fallback
            var sub = await h.GetSubscriptionAsync(id);
            Assert.True(sub.CancelAtPeriodEnd);
        }

        // ── Cancelación inmediata (solo plataforma) ──────────────────────────────────

        [Fact]
        public async Task ImmediateCancel_VerifiesDelete_CutsAccessNow()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            var result = await h.Manager.CancelAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.Equal(EstadoSuscripcion.Cancelada, sub.Estado);       // acceso cortado de inmediato
            Assert.False(h.SubscriptionService.CanAccessApp(sub));
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionImmediateCancellationRequested));
        }

        // ── Reconciliación: cierre del período ───────────────────────────────────────

        [Fact]
        public async Task Reconciliation_FinalizesCancelAtPeriodEnd_WhenPeriodEnded_WithoutCallingProvider()
        {
            using var h = await Harness.CreateAsync();
            // Renovación ya cancelada y verificada; el período pagado terminó ayer.
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(-1),
                providerStatus: "Delete",
                cancelAtPeriodEnd: true,
                providerCancelledAtUtc: h.NowUtc.AddDays(-10),
                cancellationEffectiveAtUtc: h.NowUtc.AddDays(-1));

            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            var sub = await h.GetSubscriptionAsync(id);
            Assert.Equal(EstadoSuscripcion.Cancelada, sub.Estado);       // cerrada localmente
            Assert.False(h.SubscriptionService.CanAccessApp(sub));
            Assert.Equal(0, h.Admin.DeleteCalls);                        // sin volver a llamar TiloPay
        }

        [Fact]
        public async Task Reconciliation_DoesNotFinalize_BeforeEffectiveDate()
        {
            using var h = await Harness.CreateAsync();
            // Cancelada pero el período pagado sigue vigente (vence en 20 días).
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                providerStatus: "Delete",
                cancelAtPeriodEnd: true,
                providerCancelledAtUtc: h.NowUtc.AddDays(-1),
                cancellationEffectiveAtUtc: h.NowUtc.AddDays(20));

            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            var sub = await h.GetSubscriptionAsync(id);
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);          // acceso NO se corta antes de tiempo
            Assert.True(h.SubscriptionService.CanAccessApp(sub));
        }

        [Fact]
        public async Task Reconciliation_DetectsCancelAtPeriodEndButProviderActive_CriticalMismatch()
        {
            using var h = await Harness.CreateAsync();
            // Local cree que canceló la renovación, pero el suscriptor sigue Activo en TiloPay:
            // podría seguir cobrando. Período aún vigente para que NO lo finalice antes de detectar.
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                providerStatus: "Active",
                cancelAtPeriodEnd: true,
                providerCancelledAtUtc: h.NowUtc.AddDays(-1),
                cancellationEffectiveAtUtc: h.NowUtc.AddDays(20));

            var report = await h.Reconciliation.RunAsync();

            Assert.True(report.ProviderStatusMismatchesAlerted >= 1);
            Assert.True(await h.CountAuditAsync(PlatformAuditActions.SubscriptionProviderStatusMismatch) >= 1);
        }

        // ── Pausa (solo plataforma) ──────────────────────────────────────────────────

        [Fact]
        public async Task Pause_VerifiesPaused_KeepsAccess()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            var result = await h.Manager.PauseAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            Assert.True(h.Admin.PauseCalls >= 1);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.NotNull(sub.ProviderPausedAtUtc);
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);          // acceso se mantiene
            Assert.True(h.SubscriptionService.CanAccessApp(sub));
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionProviderPauseVerified));
        }

        [Fact]
        public async Task Pause_AlreadyPaused_IsIdempotent()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "3");

            var result = await h.Manager.PauseAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            Assert.Equal(0, h.Admin.PauseCalls);                         // ya pausado: no re-llama
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionPauseAlreadyProviderPaused));
        }

        [Fact]
        public async Task Pause_Immediate_SuspendsAccess()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            var result = await h.Manager.PauseAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr", immediate: true);

            Assert.True(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.Equal(EstadoSuscripcion.Suspendida, sub.Estado);
            Assert.False(h.SubscriptionService.CanAccessApp(sub));
        }

        [Fact]
        public async Task Pause_Provider200ButNotPaused_ManualReview()
        {
            using var h = await Harness.CreateAsync();
            h.Admin.PauseEffective = false;   // 200 pero el estado no cambia a Pausado
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            var result = await h.Manager.PauseAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.False(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.Null(sub.ProviderPausedAtUtc);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionPauseFailedManualReview));
        }

        // ── Reactivación (solo plataforma) ───────────────────────────────────────────

        [Fact]
        public async Task Reactivate_Paused_VerifiesActive_ClearsPauseFields()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                providerStatus: "3",
                providerPausedAtUtc: h.NowUtc.AddDays(-2));

            var result = await h.Manager.ReactivateAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            Assert.True(h.Admin.ReactivateCalls >= 1);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.Null(sub.ProviderPausedAtUtc);
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionProviderReactivateVerified));
        }

        [Fact]
        public async Task Reactivate_Deleted_BlocksManualReview_WithoutReactivating()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Delete");

            var result = await h.Manager.ReactivateAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.False(result.Succeeded);
            Assert.Equal(0, h.Admin.ReactivateCalls);                    // un eliminado NO se reactiva a ciegas
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionReactivateFailedManualReview));
        }

        [Fact]
        public async Task Reactivate_AlreadyActive_IsIdempotent()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            var result = await h.Manager.ReactivateAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            Assert.Equal(0, h.Admin.ReactivateCalls);                    // ya activo: no re-llama
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionReactivateAlreadyProviderActive));
        }

        // ── Sincronización manual del estado del proveedor (SuperAdmin) ──────────────

        [Fact]
        public async Task SyncProviderStatus_PauseByCommerce_MarksPaused_PersistsRaw_KeepsAccess()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");
            // El comercio pausó el suscriptor por fuera: TiloPay ahora reporta "Pause By Commerce".
            h.Admin.SetSubscriber(RecurringPlanId, SubscriberId, "Pause By Commerce");

            var result = await h.Manager.SyncProviderStatusAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.Equal("Pause By Commerce", sub.ProviderStatusRaw);
            Assert.NotNull(sub.ProviderStatusLastSyncedUtc);
            Assert.NotNull(sub.ProviderPausedAtUtc);
            // Sync es SOLO lectura de estado: NO cambia el acceso ni el Estado local.
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);
            Assert.True(h.SubscriptionService.CanAccessApp(sub));
            Assert.Equal(0, h.Admin.PauseCalls);   // no opera el suscriptor, solo lo consulta
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionProviderStatusSynced));
        }

        [Fact]
        public async Task SyncProviderStatus_BackToActive_ClearsPausedFlag()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                providerStatus: "3",
                providerPausedAtUtc: h.NowUtc.AddDays(-2));
            // El suscriptor volvió a Activo en TiloPay (reactivado por fuera).
            h.Admin.SetSubscriber(RecurringPlanId, SubscriberId, "Active");

            var result = await h.Manager.SyncProviderStatusAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.Null(sub.ProviderPausedAtUtc);          // se limpia la bandera de pausa
            Assert.Equal("Active", sub.ProviderStatusRaw);
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);
        }

        [Fact]
        public async Task SyncProviderStatus_Deleted_MarksCancelled_WithoutCuttingAccess()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");
            h.Admin.SetSubscriber(RecurringPlanId, SubscriberId, "Delete");

            var result = await h.Manager.SyncProviderStatusAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.Equal("Delete", sub.ProviderStatusRaw);
            Assert.NotNull(sub.ProviderCancelledAtUtc);
            Assert.Null(sub.ProviderPausedAtUtc);
            // El snapshot NO corta acceso por sí solo (el corte real lo maneja la reconciliación/worker).
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionProviderStatusSynced));
        }

        [Fact]
        public async Task SyncProviderStatus_NoSubscriberId_Fails_WithoutTouchingProvider()
        {
            using var h = await Harness.CreateAsync();
            // Suscripción SIN id_suscriptor: no hay nada que sincronizar.
            var otherTenant = Guid.NewGuid();
            h.Db.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant { Id = otherTenant, Nombre = "Sin suscriptor", Activo = true });
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();

            var result = await h.Manager.SyncProviderStatusAsync(otherTenant, "super-admin", "admin@luxurycloud.cr");

            Assert.False(result.Succeeded);
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.SubscriptionProviderStatusSynced));
        }

        // ── Aislamiento por tenant ───────────────────────────────────────────────────

        [Fact]
        public async Task Cancel_OnlyAffectsOwnTenant()
        {
            using var h = await Harness.CreateAsync();
            var idA = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active", subscriberId: "A1");
            var otherTenant = Guid.NewGuid();
            var idB = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active", subscriberId: "B1", tenantId: otherTenant);

            await h.Manager.RequestCancellationAtPeriodEndAsync(h.TenantId, "user-a", "a@tenant.cr", null);

            var subB = await h.GetSubscriptionAsync(idB);
            Assert.False(subB.CancelAtPeriodEnd);                        // el otro tenant no se toca
            Assert.Null(subB.ProviderCancelledAtUtc);
            var subA = await h.GetSubscriptionAsync(idA);
            Assert.True(subA.CancelAtPeriodEnd);
        }

        // ── Health ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Health_ReportsLifecycleCounters()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                providerStatus: "Delete",
                cancelAtPeriodEnd: true,
                providerCancelledAtUtc: h.NowUtc.AddDays(-1),
                subscriberId: "C1");
            await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                providerStatus: "3",
                providerPausedAtUtc: h.NowUtc.AddDays(-1),
                subscriberId: "P1",
                tenantId: Guid.NewGuid());

            h.Db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.SubscriptionPauseFailedManualReview,
                EntityType = PlatformAuditEntityTypes.Subscription,
                CreatedAtUtc = h.NowUtc.AddHours(-2)
            });
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();

            var snapshot = await h.Health.BuildAsync();

            Assert.True(snapshot.SubscriptionsCancelAtPeriodEnd >= 1);
            Assert.True(snapshot.ProviderPausedSubscriptions >= 1);
            Assert.True(snapshot.PauseFailedLast7d >= 1);
        }

        // ── Hardening: "Pause By Commerce", raw en ManualReview, estado efectivo vencido ──

        [Fact]
        public async Task Pause_PauseByCommerce_VerifiesPaused_SavesRaw_NoManualReview()
        {
            using var h = await Harness.CreateAsync();
            h.Admin.PausedStatusValue = "Pause By Commerce";   // el valor real que devuelve TiloPay
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            var result = await h.Manager.PauseAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.NotNull(sub.ProviderPausedAtUtc);
            Assert.Equal("Pause By Commerce", sub.ProviderStatusRaw);
            Assert.NotNull(sub.ProviderStatusLastSyncedUtc);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionProviderPauseVerified));
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.SubscriptionPauseFailedManualReview));
        }

        [Fact]
        public async Task Pause_UnknownStatus_ManualReview_ButStillSavesRawAndSyncedAt()
        {
            using var h = await Harness.CreateAsync();
            h.Admin.PausedStatusValue = "Some Weird Status";   // status que NO sabemos clasificar
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), providerStatus: "Active");

            var result = await h.Manager.PauseAsync(h.TenantId, "super-admin", "admin@luxurycloud.cr");

            Assert.False(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.Null(sub.ProviderPausedAtUtc);                          // no se marca pausada
            Assert.Equal("Some Weird Status", sub.ProviderStatusRaw);      // pero SÍ se guarda el raw
            Assert.NotNull(sub.ProviderStatusLastSyncedUtc);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionPauseFailedManualReview));
        }

        [Fact]
        public async Task EffectiveStatus_CancelAtPeriodEndExpired_DeniesAccess_EvenIfEstadoActiva()
        {
            using var h = await Harness.CreateAsync();
            // Estado local sigue Activa y FechaFin en el futuro, PERO la cancelación ya venció ayer.
            var sub = new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                FechaFin = h.NowUtc.AddDays(20),
                CancelAtPeriodEnd = true,
                CancellationEffectiveAtUtc = h.NowUtc.AddDays(-1)
            };

            var effective = h.SubscriptionService.GetEffectiveStatus(sub);

            Assert.NotEqual(EstadoSuscripcion.Activa, effective);          // no concede acceso por Estado=1
            Assert.False(h.SubscriptionService.CanAccessApp(sub));
        }

        // ── Reactivación de RENOVACIÓN (cliente), distinta de reactivar pausa ─────────

        [Fact]
        public async Task ReactivateRenewal_WhileValid_VerifiesActive_ClearsCancellation()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                providerStatus: "Delete",           // lo dimos de baja al cancelar
                cancelAtPeriodEnd: true,
                providerCancelledAtUtc: h.NowUtc.AddDays(-1),
                cancellationEffectiveAtUtc: h.NowUtc.AddDays(20));

            var result = await h.Manager.ReactivateRenewalAsync(h.TenantId, "user-1", "user@tenant.cr");

            Assert.True(result.Succeeded);
            Assert.True(h.Admin.ReactivateCalls >= 1);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.False(sub.CancelAtPeriodEnd);                          // cancelación limpiada
            Assert.Null(sub.ProviderCancelledAtUtc);
            Assert.Null(sub.CancellationEffectiveAtUtc);
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionRenewalReactivationVerified));
        }

        [Fact]
        public async Task ReactivateRenewal_Expired_Rejected_WithoutReactivating()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(-1),
                providerStatus: "Delete",
                cancelAtPeriodEnd: true,
                providerCancelledAtUtc: h.NowUtc.AddDays(-10),
                cancellationEffectiveAtUtc: h.NowUtc.AddDays(-1));   // ya venció

            var result = await h.Manager.ReactivateRenewalAsync(h.TenantId, "user-1", "user@tenant.cr");

            Assert.False(result.Succeeded);
            Assert.Equal(0, h.Admin.ReactivateCalls);                    // no reactiva un período vencido
            var sub = await h.GetSubscriptionAsync(id);
            Assert.True(sub.CancelAtPeriodEnd);                          // se mantiene la cancelación
        }

        [Fact]
        public async Task ReactivateRenewal_ProviderNotVerifiedActive_ManualReview_DoesNotClear()
        {
            using var h = await Harness.CreateAsync();
            h.Admin.ReactivateEffective = false;   // 200 pero sigue Delete
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                providerStatus: "Delete",
                cancelAtPeriodEnd: true,
                providerCancelledAtUtc: h.NowUtc.AddDays(-1),
                cancellationEffectiveAtUtc: h.NowUtc.AddDays(20));

            var result = await h.Manager.ReactivateRenewalAsync(h.TenantId, "user-1", "user@tenant.cr");

            Assert.False(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.True(sub.CancelAtPeriodEnd);                          // NO se limpia sin verificación
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionRenewalReactivationFailedManualReview));
        }

        // ── Worker liviano de finalización: local, sin HTTP, idempotente ─────────────

        [Fact]
        public async Task LifecycleFinalization_ClosesExpiredCancellation_WithoutHttp_AndIsIdempotent()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(-1),
                providerStatus: "Delete",
                cancelAtPeriodEnd: true,
                providerCancelledAtUtc: h.NowUtc.AddDays(-10),
                cancellationEffectiveAtUtc: h.NowUtc.AddDays(-1));

            await h.Reconciliation.RunLifecycleFinalizationAsync();

            var sub = await h.GetSubscriptionAsync(id);
            Assert.Equal(EstadoSuscripcion.Cancelada, sub.Estado);
            Assert.Equal(0, h.Admin.DeleteCalls);                        // cierre local: cero HTTP a TiloPay
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionCancellationAtPeriodEndFinalized));

            // Repetir es idempotente: no vuelve a finalizar ni audita de nuevo.
            await h.Reconciliation.RunLifecycleFinalizationAsync();
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionCancellationAtPeriodEndFinalized));
        }

        // ── Fake del API admin de TiloPay que MUTA el estado al operar ────────────────

        /// <summary>
        /// Fake controlable: registra cuántas veces se llamó cada operación y, por defecto, cambia
        /// el estado del suscriptor (delete→"Delete", pause→"3", reactivate→"Active") para que la
        /// verificación posterior lo vea. Los flags *Effective=false simulan "HTTP 200 pero el
        /// estado no cambió" (el caso peligroso), y *Succeeds=false fuerza el fallback editSuscriptorRepeat.
        /// </summary>
        private sealed class LifecycleFakeAdmin : ITilopayRepeatAdminService
        {
            public bool IsEnabled { get; set; } = true;
            public Dictionary<int, List<TilopaySubscriber>> SubscribersByPlan { get; } = new();

            public int DeleteCalls;
            public int PauseCalls;
            public int ReactivateCalls;
            public int EditCalls;

            public bool DeleteEffective { get; set; } = true;
            public bool PauseEffective { get; set; } = true;
            public bool ReactivateEffective { get; set; } = true;

            /// <summary>Status que deja el proveedor al pausar. Default "3"; los tests lo cambian a "Pause By Commerce" o a un valor desconocido.</summary>
            public string PausedStatusValue { get; set; } = "3";
            public bool DeleteSucceeds { get; set; } = true;
            public bool PauseSucceeds { get; set; } = true;
            public bool ReactivateSucceeds { get; set; } = true;

            public void SetSubscriber(int plan, string id, string status)
            {
                if (!SubscribersByPlan.TryGetValue(plan, out var list))
                {
                    list = new List<TilopaySubscriber>();
                    SubscribersByPlan[plan] = list;
                }

                list.RemoveAll(s => string.Equals(s.SubscriberId, id, StringComparison.OrdinalIgnoreCase));
                list.Add(new TilopaySubscriber { SubscriberId = id, Status = status });
            }

            private void SetStatus(string id, string status)
            {
                foreach (var list in SubscribersByPlan.Values)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (string.Equals(list[i].SubscriberId, id, StringComparison.OrdinalIgnoreCase))
                        {
                            list[i] = list[i] with { Status = status };
                        }
                    }
                }
            }

            public Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(int tilopayPlanId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<TilopaySubscriber>>(
                    SubscribersByPlan.TryGetValue(tilopayPlanId, out var list) ? list.ToList() : new List<TilopaySubscriber>());

            public Task<TilopayAdminOperationResult> DeleteSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default)
            {
                DeleteCalls++;
                if (!DeleteSucceeds)
                {
                    return Task.FromResult(TilopayAdminOperationResult.Fail("delete falló"));
                }

                if (DeleteEffective)
                {
                    SetStatus(subscriberId, "Delete");
                }

                return Task.FromResult(TilopayAdminOperationResult.Ok("ok"));
            }

            public Task<TilopayAdminOperationResult> PauseSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default)
            {
                PauseCalls++;
                if (!PauseSucceeds)
                {
                    return Task.FromResult(TilopayAdminOperationResult.Fail("pause falló"));
                }

                if (PauseEffective)
                {
                    SetStatus(subscriberId, PausedStatusValue);
                }

                return Task.FromResult(TilopayAdminOperationResult.Ok("paused"));
            }

            public Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default)
            {
                ReactivateCalls++;
                if (!ReactivateSucceeds)
                {
                    return Task.FromResult(TilopayAdminOperationResult.Fail("reactivate falló"));
                }

                if (ReactivateEffective)
                {
                    SetStatus(subscriberId, "Active");
                }

                return Task.FromResult(TilopayAdminOperationResult.Ok("reactivated"));
            }

            public Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(string subscriberId, TilopaySubscriberStatus status, CancellationToken cancellationToken = default)
            {
                EditCalls++;
                // El fallback SÍ aplica el estado pedido (representa un editSuscriptorRepeat exitoso).
                var raw = status switch
                {
                    TilopaySubscriberStatus.Active => "1",
                    TilopaySubscriberStatus.Paused => "3",
                    TilopaySubscriberStatus.Deleted => "4",
                    _ => "1"
                };
                SetStatus(subscriberId, raw);
                return Task.FromResult(TilopayAdminOperationResult.Ok("edited"));
            }

            public Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(TargetSubscriberAssessment.FromMatches(
                    SubscribersByPlan.TryGetValue(tilopayPlanId, out var list) ? list : new List<TilopaySubscriber>(), tilopayPlanId));

            public Task<SubscriberResolutionResult> ResolveSubscriberAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(SubscriberResolutionResult.NotFound());

            public Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("ok", "https://tp.cr/l/x"));
        }

        private sealed class Harness : IDisposable
        {
            private readonly IDisposable _connection;
            private int _seedCount;

            public ApplicationDbContext Db { get; private init; } = null!;
            public LifecycleFakeAdmin Admin { get; } = new();
            public ProviderSubscriptionManager Manager { get; private set; } = null!;
            public BillingReconciliationService Reconciliation { get; private set; } = null!;
            public BillingHealthService Health { get; private set; } = null!;
            public SuscripcionService SubscriptionService { get; private set; } = null!;
            public Guid TenantId { get; } = Guid.NewGuid();
            public Guid PlanId { get; private set; }
            public DateTime NowUtc { get; } = DateTime.UtcNow;

            private Harness(IDisposable connection) => _connection = connection;

            public static async Task<Harness> CreateAsync()
            {
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
                var h = new Harness(connection) { Db = context };

                var repeatOptions = CalculatorCatalog.BuildRepeatOptions();
                var tenantAccessor = new TenantExecutionContextAccessor();
                var cache = new MemoryCache(new MemoryCacheOptions());
                var accessCache = new TenantCommercialAccessCache(cache);
                var clock = new FixedBusinessDateTimeProvider(
                    DateTime.SpecifyKind(h.NowUtc, DateTimeKind.Unspecified));
                var adminOptions = Options.Create(new OpcionesTilopayRepeatAdmin { Enabled = true });

                h.SubscriptionService = new SuscripcionService(
                    context, cache, accessCache, clock,
                    Options.Create(repeatOptions), NullLogger<SuscripcionService>.Instance);

                h.Manager = new ProviderSubscriptionManager(
                    context, h.Admin, tenantAccessor, clock, adminOptions,
                    NullLogger<ProviderSubscriptionManager>.Instance, accessCache);

                h.Reconciliation = new BillingReconciliationService(
                    context,
                    h.SubscriptionService,
                    tenantAccessor,
                    clock,
                    Options.Create(repeatOptions),
                    Options.Create(new BillingReconciliationOptions()),
                    NullLogger<BillingReconciliationService>.Instance,
                    subscriberResolutionService: null,
                    adminOptions: adminOptions,
                    providerSubscriptionManager: h.Manager,
                    planChangeLateApplicationService: null,
                    providerExpirySyncService: null,
                    adminService: h.Admin,
                    accessCache: accessCache);

                h.Health = new BillingHealthService(context, h.SubscriptionService);

                context.Tenants.Add(new Tenant { Id = h.TenantId, Nombre = "Tenant Lifecycle", Activo = true });
                var plan = new Plan
                {
                    Id = Guid.NewGuid(),
                    Codigo = "LC_M_02",
                    Nombre = "LC_M_02",
                    PrecioMensual = 15000m,
                    MonthlyEquivalentAmount = 15000m,
                    BillingCycle = BillingCycle.Monthly,
                    Moneda = "CRC",
                    MaxFuncionarios = 2,
                    Activo = true
                };
                context.Planes.Add(plan);
                h.PlanId = plan.Id;
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();

                return h;
            }

            public async Task<Guid> SeedSubscriptionAsync(
                DateTime localEndUtc,
                string providerStatus,
                bool cancelAtPeriodEnd = false,
                DateTime? providerCancelledAtUtc = null,
                DateTime? cancellationEffectiveAtUtc = null,
                DateTime? providerPausedAtUtc = null,
                string subscriberId = SubscriberId,
                Guid? tenantId = null)
            {
                var owner = tenantId ?? TenantId;
                if (owner != TenantId && !await Db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == owner))
                {
                    Db.Tenants.Add(new Tenant { Id = owner, Nombre = $"Tenant {++_seedCount}", Activo = true });
                }

                var id = Guid.NewGuid();
                Db.Suscripciones.Add(new Suscripcion
                {
                    Id = id,
                    TenantId = owner,
                    PlanId = PlanId,
                    CodigoPlan = "LC_M_02",
                    Estado = EstadoSuscripcion.Activa,
                    Proveedor = PaymentProviderType.Tilopay,
                    TilopayRecurringPlanId = RecurringPlanId,
                    ProviderSubscriptionId = subscriberId,
                    FechaInicio = localEndUtc.AddMonths(-1),
                    FechaFin = localEndUtc,
                    FechaProximoCobroUtc = localEndUtc,
                    CancelAtPeriodEnd = cancelAtPeriodEnd,
                    ProviderCancelledAtUtc = providerCancelledAtUtc,
                    CancellationEffectiveAtUtc = cancellationEffectiveAtUtc,
                    CancellationRequestedAtUtc = cancelAtPeriodEnd ? localEndUtc.AddMonths(-1) : null,
                    ProviderPausedAtUtc = providerPausedAtUtc
                });

                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();

                Admin.SetSubscriber(RecurringPlanId, subscriberId, providerStatus);
                return id;
            }

            public async Task<Suscripcion> GetSubscriptionAsync(Guid id)
            {
                Db.ChangeTracker.Clear();
                return await Db.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == id);
            }

            public Task<int> CountAuditAsync(string action) =>
                Db.PlatformAuditLogs.CountAsync(l => l.Action == action);

            public void Dispose()
            {
                Db.Dispose();
                _connection.Dispose();
            }
        }
    }
}
