using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Regresión del incidente real de renovación rechazada (plan mensual, cobro rechazado por fondos
    /// insuficientes): 15 minutos después del fallo la reconciliación vio al suscriptor "Active" con el
    /// MISMO expire y cerró la recuperación, limpió la gracia y el tenant terminó viendo "Tu suscripción
    /// finalizó" en plena gracia. Invariante: "Active" del proveedor NUNCA prueba un pago; solo un
    /// pago confirmado posterior al fallo o un expire que avanzó a un período nuevo cierran la recuperación.
    /// Fechas representativas del escenario, sin datos de ningún tenant.
    /// </summary>
    public class PaymentRecoveryFalseRenewalTests
    {
        private const int RecurringPlanId = 7101;
        private const string SubscriberId = "SUB-TEST-1";

        // Período pagado que TiloPay intentó renovar: expire "2026-09-19" (fin del día Costa Rica).
        private static readonly DateTime FailureAtUtc = new(2026, 9, 19, 16, 30, 11, DateTimeKind.Utc);
        private static readonly DateTime LocalPeriodEndUtc = new(2026, 9, 19, 16, 30, 0, DateTimeKind.Utc);
        private static readonly DateTime ProviderPeriodEndUtc = ProviderExpiryDate.ParseCostaRicaEndOfDayUtc("2026-09-19")!.Value;

        // ── Regla pura de evidencia ─────────────────────────────────────────────────

        [Theory]
        [InlineData("Active", "2026-09-19", false)]            // el bug real: Active + mismo expire
        [InlineData("Active", "2026-09-20", false)]            // ruido de fecha/zona horaria
        [InlineData("Active", "2026-10-19", true)]             // período nuevo real
        [InlineData("Pause By Commerce", "2026-10-19", false)] // pausado nunca prueba pago
        [InlineData(null, "2026-10-19", false)]                // status desconocido: fail-closed
        public void Evidence_OnlyAdvancedExpireOfActiveSubscriber_ProvesNewPeriod(string? status, string expireRaw, bool expected)
        {
            var expiresAtUtc = ProviderExpiryDate.ParseCostaRicaEndOfDayUtc(expireRaw);

            Assert.Equal(expected, PaymentRecoveryRenewalEvidence.ProviderShowsNewPaidPeriod(status, expiresAtUtc, FailureAtUtc));
        }

        // ── TEST 1: el fallo abre la gracia y el acceso sigue ─────────────────────────

        [Fact]
        public async Task FailedRenewal_OpensGrace_Morosa_AccessStillAllowed()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc);
            await h.SeedPaidSubscriptionAsync();

            await h.RegisterFailedRenewalAsync();

            var sub = await h.GetSubscriptionAsync();
            var incident = await h.GetSingleIncidentAsync();
            Assert.Equal(EstadoSuscripcion.Morosa, sub.Estado);
            Assert.Equal("GraceActive", sub.PaymentRecoveryStatus);
            Assert.Equal(FailureAtUtc.AddDays(5), incident.GraceEndsAtUtc);
            Assert.Equal(incident.GraceEndsAtUtc, sub.FechaFinGraciaUtc);
            Assert.Equal(PaymentIncidentStatus.Open, incident.Status);
            Assert.True((await h.ResolveAccessAsync()).CanAccessApp);
        }

        // ── TEST 2: EL BUG REAL ──────────────────────────────────────────────────────

        [Fact]
        public async Task Reconciliation_ProviderActiveWithSameExpire_DoesNotResolveRecovery()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc);
            await h.SeedPaidSubscriptionAsync();
            await h.RegisterFailedRenewalAsync();
            var graceBefore = (await h.GetSubscriptionAsync()).FechaFinGraciaUtc;

            // 15 minutos después: TiloPay sigue "Active" con expire 2026-09-19 (no avanzó).
            h.Clock.UtcNow = FailureAtUtc.AddMinutes(15);
            h.Admin.SetSubscriber("Active", "2026-09-19");
            var report = await h.CreateReconciliation().RunAsync();

            Assert.Equal(0, report.RecoveredSubscriptionsHealed);
            var sub = await h.GetSubscriptionAsync();
            Assert.Equal(EstadoSuscripcion.Morosa, sub.Estado);
            Assert.Equal("GraceActive", sub.PaymentRecoveryStatus);
            Assert.Equal(graceBefore, sub.FechaFinGraciaUtc);
            Assert.NotNull(sub.LastPaymentFailedAtUtc);
            var incident = await h.GetSingleIncidentAsync();
            Assert.Equal(PaymentIncidentStatus.Open, incident.Status);
            Assert.Null(incident.ResolvedAtUtc);
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryResolvedByProviderRenewal));

            // Dos días después (período pagado vencido, gracia vigente): sigue con acceso.
            h.Clock.UtcNow = FailureAtUtc.AddDays(2);
            await h.CreateReconciliation().RunAsync();
            var access = await h.ResolveAccessAsync();
            Assert.True(access.CanAccessApp);
            Assert.True(access.IsInGracePeriod);
            Assert.Equal(PaymentIncidentStatus.Open, (await h.GetSingleIncidentAsync()).Status);
        }

        // ── TEST 3: expire avanzado SÍ resuelve ───────────────────────────────────────

        [Fact]
        public async Task Reconciliation_ProviderExpireAdvanced_ResolvesRecovery_AndIsIdempotent()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc);
            await h.SeedPaidSubscriptionAsync();
            await h.RegisterFailedRenewalAsync();

            h.Clock.UtcNow = FailureAtUtc.AddDays(1);
            h.Admin.SetSubscriber("Active", "2026-10-19");
            var report = await h.CreateReconciliation().RunAsync();

            Assert.Equal(1, report.RecoveredSubscriptionsHealed);
            var renewedEndUtc = ProviderExpiryDate.ParseCostaRicaEndOfDayUtc("2026-10-19");
            var sub = await h.GetSubscriptionAsync();
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);
            Assert.Null(sub.PaymentRecoveryStatus);
            Assert.Null(sub.FechaFinGraciaUtc);
            Assert.Null(sub.LastPaymentFailedAtUtc);
            Assert.Equal(renewedEndUtc, sub.FechaFin);
            Assert.Equal(renewedEndUtc, sub.FechaProximoCobroUtc);
            var incident = await h.GetSingleIncidentAsync();
            Assert.Equal(PaymentIncidentStatus.Resolved, incident.Status);
            Assert.NotNull(incident.ResolvedAtUtc);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryResolvedByProviderRenewal));

            // Segunda pasada: nada que sanar, sin auditoría duplicada ni fechas movidas.
            var again = await h.CreateReconciliation().RunAsync();
            Assert.Equal(0, again.RecoveredSubscriptionsHealed);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryResolvedByProviderRenewal));
            Assert.Equal(renewedEndUtc, (await h.GetSubscriptionAsync()).FechaFin);
        }

        [Fact]
        public async Task Reconciliation_ConfirmedPaymentAppliedAfterFailure_IsEvidence_EvenWithStaleProviderExpire()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc);
            await h.SeedPaidSubscriptionAsync();
            await h.RegisterFailedRenewalAsync();
            // El success llegó y se aplicó (pago confirmado + período extendido) pero el cierre del
            // incidente (best-effort, post-commit) no corrió; el listado del proveedor aún no refrescó.
            await h.SeedPaymentAsync(EstadoPagoProveedor.Confirmado, FailureAtUtc.AddHours(2));
            await h.SimulateAppliedSuccessWithoutIncidentCloseAsync(newPeriodEndUtc: FailureAtUtc.AddMonths(1));

            h.Clock.UtcNow = FailureAtUtc.AddHours(3);
            h.Admin.SetSubscriber("Active", "2026-09-19");
            var report = await h.CreateReconciliation().RunAsync();

            Assert.Equal(1, report.RecoveredSubscriptionsHealed);
            Assert.Equal(PaymentIncidentStatus.Resolved, (await h.GetSingleIncidentAsync()).Status);
            var sub = await h.GetSubscriptionAsync();
            Assert.Null(sub.PaymentRecoveryStatus);
            Assert.Equal(FailureAtUtc.AddHours(2), sub.FechaUltimoPagoUtc);
            Assert.Equal(FailureAtUtc.AddMonths(1), sub.FechaFin);   // no se extiende de nuevo
        }

        [Fact]
        public async Task Reconciliation_ConfirmedPaymentNotAppliedToPeriod_DoesNotCloseGrace()
        {
            // Un pago confirmado que NO extendió el período no basta para cerrar la gracia desde la
            // sanación: dejaría la cuenta Activa a horas de vencer y sin gracia.
            using var h = await Harness.CreateAsync(FailureAtUtc);
            await h.SeedPaidSubscriptionAsync();
            await h.RegisterFailedRenewalAsync();
            await h.SeedPaymentAsync(EstadoPagoProveedor.Confirmado, FailureAtUtc.AddHours(2));

            h.Clock.UtcNow = FailureAtUtc.AddHours(3);
            h.Admin.SetSubscriber("Active", "2026-09-19");
            var report = await h.CreateReconciliation().RunAsync();

            Assert.Equal(0, report.RecoveredSubscriptionsHealed);
            Assert.Equal(PaymentIncidentStatus.Open, (await h.GetSingleIncidentAsync()).Status);
            Assert.Equal("GraceActive", (await h.GetSubscriptionAsync()).PaymentRecoveryStatus);
        }

        // ── TEST 5: Pause By Commerce durante la gracia ────────────────────────────────

        [Fact]
        public async Task ProviderPauseByCommerce_DuringGrace_KeepsAccess_AndDoesNotTouchRecovery()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc);
            await h.SeedPaidSubscriptionAsync();
            await h.RegisterFailedRenewalAsync();

            h.Clock.UtcNow = FailureAtUtc.AddDays(2);
            h.Admin.SetSubscriber("Pause By Commerce", "2026-09-19");
            await h.CreateReconciliation().RunAsync();

            var sub = await h.GetSubscriptionAsync();
            Assert.Equal("Pause By Commerce", sub.ProviderStatusRaw);   // se conserva para diagnóstico
            Assert.Equal(EstadoSuscripcion.Morosa, sub.Estado);
            Assert.Equal("GraceActive", sub.PaymentRecoveryStatus);
            Assert.Equal(FailureAtUtc.AddDays(5), sub.FechaFinGraciaUtc);
            Assert.Equal(PaymentIncidentStatus.Open, (await h.GetSingleIncidentAsync()).Status);
            Assert.True((await h.ResolveAccessAsync()).CanAccessApp);

            // UI: la pausa causada por los reintentos NO se presenta como "pausada por soporte" ni tapa la gracia.
            var summaryVm = new BillingSubscriptionSummaryViewModel
            {
                Status = EstadoSuscripcion.Morosa,
                IsRecurringTilopay = true,
                IsRenewalPaused = true,
                IsProviderPauseFromRecovery = true,
                PaymentRecoveryStatus = "GraceActive"
            };
            Assert.True(summaryVm.PaymentInGrace);
            Assert.False(summaryVm.ShowSupportPauseNotice);
        }

        // ── TEST 6: período vencido + gracia vigente ⇒ acceso ─────────────────────────

        [Fact]
        public async Task Resolver_PaidPeriodExpired_GraceActive_AllowsAccess()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc.AddDays(3));
            await h.SeedPaidSubscriptionAsync(
                estado: EstadoSuscripcion.Morosa,
                paymentRecoveryStatus: "GraceActive",
                fechaFinGraciaUtc: FailureAtUtc.AddDays(5));

            var access = await h.ResolveAccessAsync();

            Assert.True(access.CanAccessApp);
            Assert.True(access.IsInGracePeriod);
            Assert.Equal(FailureAtUtc.AddDays(5), access.AccessEndsUtc);
        }

        // ── TEST 7 / 8: gracia vencida según AutoSuspendAfterGrace ────────────────────

        [Fact]
        public async Task GraceExpired_AutoSuspendFalse_MarksGraceExpired_KeepsAccess_Audited()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc, autoSuspend: false);
            await h.SeedPaidSubscriptionAsync();
            await h.RegisterFailedRenewalAsync();

            h.Clock.UtcNow = FailureAtUtc.AddDays(5).AddMinutes(1);
            await h.Recovery().RunGraceExpirationPassAsync();

            var sub = await h.GetSubscriptionAsync();
            Assert.Equal(PaymentIncidentStatus.GraceExpired, (await h.GetSingleIncidentAsync()).Status);
            Assert.Equal("GraceExpired", sub.PaymentRecoveryStatus);
            Assert.Equal(EstadoSuscripcion.Morosa, sub.Estado);
            Assert.True((await h.ResolveAccessAsync()).CanAccessApp);       // política actual: sin corte
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionPaymentGraceExpiredDryRun));
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.SubscriptionSuspendedForNonPayment));
        }

        [Fact]
        public async Task GraceExpired_AutoSuspendTrue_SuspendsOnce_Idempotent()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc, autoSuspend: true);
            await h.SeedPaidSubscriptionAsync();
            await h.RegisterFailedRenewalAsync();

            // Dentro de la gracia: el pase no hace nada.
            h.Clock.UtcNow = FailureAtUtc.AddDays(4);
            await h.Recovery().RunGraceExpirationPassAsync();
            Assert.Equal(EstadoSuscripcion.Morosa, (await h.GetSubscriptionAsync()).Estado);
            Assert.True((await h.ResolveAccessAsync()).CanAccessApp);

            h.Clock.UtcNow = FailureAtUtc.AddDays(5).AddMinutes(1);
            await h.Recovery().RunGraceExpirationPassAsync();
            await h.Recovery().RunGraceExpirationPassAsync();

            var sub = await h.GetSubscriptionAsync();
            Assert.Equal(EstadoSuscripcion.Suspendida, sub.Estado);
            Assert.Equal("Suspended", sub.PaymentRecoveryStatus);
            Assert.Equal(PaymentIncidentStatus.GraceExpired, (await h.GetSingleIncidentAsync()).Status);
            Assert.False((await h.ResolveAccessAsync()).CanAccessApp);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionSuspendedForNonPayment));
        }

        [Fact]
        public async Task GraceExpiredDryRun_ThenAutoSuspendEnabled_SuspendsExactlyOnce()
        {
            // Plan de despliegue: primero dry-run (flag apagado), luego se enciende el flag. Los
            // incidentes que ya estaban en GraceExpired deben suspenderse una sola vez.
            using var h = await Harness.CreateAsync(FailureAtUtc, autoSuspend: false);
            await h.SeedPaidSubscriptionAsync();
            await h.RegisterFailedRenewalAsync();
            h.Clock.UtcNow = FailureAtUtc.AddDays(6);
            await h.Recovery().RunGraceExpirationPassAsync();
            Assert.Equal("GraceExpired", (await h.GetSubscriptionAsync()).PaymentRecoveryStatus);

            h.AutoSuspend = true;
            h.Clock.UtcNow = FailureAtUtc.AddDays(7);
            await h.Recovery().RunGraceExpirationPassAsync();
            await h.Recovery().RunGraceExpirationPassAsync();

            var sub = await h.GetSubscriptionAsync();
            Assert.Equal(EstadoSuscripcion.Suspendida, sub.Estado);
            Assert.Equal("Suspended", sub.PaymentRecoveryStatus);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionSuspendedForNonPayment));
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionPaymentGraceExpired));
        }

        // ── TEST 9: reintentos del mismo ciclo no reinician la gracia ────────────────

        [Fact]
        public async Task SecondFailure_SameCycle_IncrementsCount_KeepsOriginalGrace_NoSecondIncident()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc);
            await h.SeedPaidSubscriptionAsync();
            await h.RegisterFailedRenewalAsync();

            // TiloPay reintenta al día siguiente y vuelve a rechazar.
            h.Clock.UtcNow = FailureAtUtc.AddDays(1);
            await h.RegisterFailedRenewalAsync();

            var incident = await h.GetSingleIncidentAsync();
            var sub = await h.GetSubscriptionAsync();
            Assert.Equal(2, incident.FailureCount);
            Assert.Equal(FailureAtUtc, incident.FailureDetectedAtUtc);
            Assert.Equal(FailureAtUtc.AddDays(5), incident.GraceEndsAtUtc);
            Assert.Equal(FailureAtUtc.AddDays(5), sub.FechaFinGraciaUtc);
            Assert.Equal(EstadoSuscripcion.Morosa, sub.Estado);   // antes caía a Fallida
            Assert.Equal(FailureAtUtc.AddDays(1), sub.LastPaymentFailedAtUtc);
            Assert.True((await h.ResolveAccessAsync()).CanAccessApp);
        }

        [Fact]
        public async Task FailureAfterGraceExpired_ReusesIncident_DoesNotOpenNewGrace()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc, autoSuspend: false);
            await h.SeedPaidSubscriptionAsync();
            await h.RegisterFailedRenewalAsync();
            h.Clock.UtcNow = FailureAtUtc.AddDays(6);
            await h.Recovery().RunGraceExpirationPassAsync();

            // Reintento del día 7: antes abría OTRO incidente con 5 días nuevos (gracia infinita).
            h.Clock.UtcNow = FailureAtUtc.AddDays(7);
            await h.RegisterFailedRenewalAsync();

            var incident = await h.GetSingleIncidentAsync();
            Assert.Equal(PaymentIncidentStatus.GraceExpired, incident.Status);
            Assert.Equal(2, incident.FailureCount);
            var sub = await h.GetSubscriptionAsync();
            Assert.Equal(FailureAtUtc.AddDays(5), sub.FechaFinGraciaUtc);
            Assert.Equal("GraceExpired", sub.PaymentRecoveryStatus);
        }

        // ── TEST 12: sin alerta "Renovación vencida sin webhook" con recuperación conocida ──

        [Fact]
        public async Task OverdueRenewalAlert_NotRaised_WhenFailureIsKnown_ButRaisedWithoutAnyEvidence()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc.AddDays(3));
            // Estado local todavía Activa pero con el fallo registrado (recuperación conocida).
            await h.SeedPaidSubscriptionAsync(
                paymentRecoveryStatus: "GraceActive",
                lastPaymentFailedAtUtc: FailureAtUtc,
                fechaFinGraciaUtc: FailureAtUtc.AddDays(5));
            h.Admin.IsEnabled = false; // solo el pase local de alertas

            var withRecovery = await h.CreateReconciliation().RunAsync();
            Assert.Equal(0, withRecovery.OverdueRenewalsAlerted);

            // Control: sin ningún rastro de fallo sí es "renovación vencida sin webhook".
            await h.ClearRecoveryFieldsAsync();
            var withoutEvidence = await h.CreateReconciliation().RunAsync();
            Assert.Equal(1, withoutEvidence.OverdueRenewalsAlerted);
        }

        // ── TEST 11: UI durante la gracia ─────────────────────────────────────────────

        [Fact]
        public async Task Summary_DuringGrace_ShowsPendingPayment_WithCostaRicaGraceDate()
        {
            // El summary usa la hora real para el fail-safe de la ventana: fechas relativas a ahora.
            var nowUtc = DateTime.UtcNow;
            using var h = await Harness.CreateAsync(nowUtc);
            // Gracia que vence a las 02:00 UTC: en Costa Rica todavía es el día anterior (20:00).
            var graceEndsUtc = nowUtc.Date.AddDays(3).AddHours(2);
            await h.SeedPaidSubscriptionAsync(
                estado: EstadoSuscripcion.Morosa,
                paymentRecoveryStatus: "GraceActive",
                fechaFinGraciaUtc: graceEndsUtc,
                localPeriodEndUtc: nowUtc.AddDays(-1),
                providerPeriodEndUtc: nowUtc.AddDays(-1));

            var summary = await h.BuildSummaryAsync();

            Assert.NotNull(summary);
            Assert.True(summary!.CanAccessApp);
            Assert.True(summary.PaymentInGrace);
            Assert.False(summary.PaymentGraceExpired);
            Assert.False(summary.PaymentSuspended);
            Assert.Equal("En período de gracia", summary.PaymentStateBadgeLabel);
            Assert.Equal(graceEndsUtc.AddDays(-1).ToString("dd/MM/yyyy"), summary.GracePeriodEndsDisplay);
        }

        [Fact]
        public void SubscriptionView_GraceBanner_UsesRenewalCopy_NeverSaysFinished()
        {
            var view = File.ReadAllText(TestProjectPaths.ProjectPath("Views", "Billing", "Suscripcion.cshtml"));

            Assert.Contains("No pudimos procesar tu renovación", view, StringComparison.Ordinal);
            Assert.Contains("continúa activa durante el período de gracia", view, StringComparison.Ordinal);
            Assert.Contains("GracePeriodEndsDisplay", view, StringComparison.Ordinal);
            Assert.DoesNotContain("finalizó", view, StringComparison.Ordinal);
            // La pausa causada por la recuperación no se anuncia como pausa de soporte.
            Assert.Contains("ShowSupportPauseNotice", view, StringComparison.Ordinal);
        }

        [Fact]
        public void PlanVencido_RecoverySuspension_SendsToRegularize_NotToNewCheckout()
        {
            var controller = File.ReadAllText(TestProjectPaths.ProjectPath("Controllers", "Billing", "BillingController.cs"));
            var view = File.ReadAllText(TestProjectPaths.ProjectPath("Views", "Billing", "PlanVencido.cshtml"));

            Assert.Contains("Tu acceso está suspendido por un pago pendiente", controller, StringComparison.Ordinal);
            Assert.Contains("IsPaymentRecoverySuspended", view, StringComparison.Ordinal);
            Assert.Contains("Regularizar pago", view, StringComparison.Ordinal);
        }

        // ── Reparación controlada de un cierre sin evidencia (estado dañado por el bug) ──

        [Fact]
        public async Task UnverifiedResolution_IsListed_AndReopenRestoresOriginalGrace_ThenAppliesPolicy()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc.AddDays(6), autoSuspend: false);
            var incidentId = await h.SeedStateDamagedByFalseHealAsync();

            var listed = await h.Recovery().ListUnverifiedResolutionsAsync();
            var item = Assert.Single(listed);
            Assert.Equal(incidentId, item.IncidentId);

            var result = await h.Recovery().ReopenUnverifiedResolutionAsync(incidentId, "admin-1", "admin@test.local", "cierre sin cobro");

            Assert.True(result.Succeeded, result.Message);
            var incident = await h.GetSingleIncidentAsync();
            // La gracia original (19→24/09) ya venció a los 6 días: el pase normal la marcó vencida.
            Assert.Equal(PaymentIncidentStatus.GraceExpired, incident.Status);
            Assert.Null(incident.ResolvedAtUtc);
            Assert.Equal(FailureAtUtc.AddDays(5), incident.GraceEndsAtUtc);
            var sub = await h.GetSubscriptionAsync();
            Assert.Equal(EstadoSuscripcion.Morosa, sub.Estado);
            Assert.Equal("GraceExpired", sub.PaymentRecoveryStatus);
            Assert.Equal(FailureAtUtc.AddDays(5), sub.FechaFinGraciaUtc);   // original, NO extendida
            Assert.Equal(FailureAtUtc, sub.LastPaymentFailedAtUtc);
            Assert.Equal(LocalPeriodEndUtc.AddMonths(-1), sub.FechaUltimoPagoUtc); // último cobro REAL
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryUnverifiedResolutionReopened));

            // Idempotente y ya no aparece como candidato.
            var again = await h.Recovery().ReopenUnverifiedResolutionAsync(incidentId, "admin-1", "admin@test.local", "otra vez");
            Assert.True(again.Succeeded);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryUnverifiedResolutionReopened));
            Assert.Empty(await h.Recovery().ListUnverifiedResolutionsAsync());
        }

        [Fact]
        public async Task UnverifiedResolution_Reopen_WithAutoSuspend_SuspendsImmediately()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc.AddDays(6), autoSuspend: true);
            var incidentId = await h.SeedStateDamagedByFalseHealAsync();

            var result = await h.Recovery().ReopenUnverifiedResolutionAsync(incidentId, "admin-1", "admin@test.local", "cierre sin cobro");

            Assert.True(result.Succeeded, result.Message);
            var sub = await h.GetSubscriptionAsync();
            Assert.Equal(EstadoSuscripcion.Suspendida, sub.Estado);
            Assert.Equal("Suspended", sub.PaymentRecoveryStatus);
            Assert.False((await h.ResolveAccessAsync()).CanAccessApp);
        }

        [Fact]
        public async Task UnverifiedResolution_WithConfirmedPaymentAfterFailure_IsNotReopened()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc.AddDays(6));
            var incidentId = await h.SeedStateDamagedByFalseHealAsync();
            await h.SeedPaymentAsync(EstadoPagoProveedor.Confirmado, FailureAtUtc.AddDays(2));

            Assert.Empty(await h.Recovery().ListUnverifiedResolutionsAsync());
            var result = await h.Recovery().ReopenUnverifiedResolutionAsync(incidentId, "admin-1", "admin@test.local", "x");

            Assert.False(result.Succeeded);
            Assert.Equal(PaymentIncidentStatus.Resolved, (await h.GetSingleIncidentAsync()).Status);
            Assert.Equal(EstadoSuscripcion.Activa, (await h.GetSubscriptionAsync()).Estado);
        }

        [Fact]
        public async Task ManuallyResolvedIncident_IsNeverListedOrReopened()
        {
            using var h = await Harness.CreateAsync(FailureAtUtc.AddDays(6));
            var incidentId = await h.SeedStateDamagedByFalseHealAsync(withHealAudit: false);

            Assert.Empty(await h.Recovery().ListUnverifiedResolutionsAsync());
            var result = await h.Recovery().ReopenUnverifiedResolutionAsync(incidentId, "admin-1", "admin@test.local", "x");
            Assert.False(result.Succeeded);
        }

        // ── Infraestructura ──────────────────────────────────────────────────────────

        private sealed class MutableClock : IBusinessDateTimeProvider
        {
            public DateTime UtcNow { get; set; }

            private static readonly TimeSpan CostaRica = TimeSpan.FromHours(-6);

            public DateTimeOffset NowOffset() =>
                new DateTimeOffset(DateTime.SpecifyKind(UtcNow, DateTimeKind.Utc)).ToOffset(CostaRica);

            public DateTime Now() => NowOffset().DateTime;

            public DateTime Today() => Now().Date;
        }

        private sealed class FakeAdmin : ITilopayRepeatAdminService
        {
            private List<TilopaySubscriber> _subscribers = new();

            public bool IsEnabled { get; set; } = true;

            public void SetSubscriber(string status, string expireRaw) =>
                _subscribers = new List<TilopaySubscriber>
                {
                    new()
                    {
                        SubscriberId = SubscriberId,
                        Status = status,
                        ExpiresRaw = expireRaw,
                        ExpiresAtUtc = ProviderExpiryDate.ParseCostaRicaEndOfDayUtc(expireRaw)
                    }
                };

            public Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(int tilopayPlanId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<TilopaySubscriber>>(tilopayPlanId == RecurringPlanId ? _subscribers.ToList() : new List<TilopaySubscriber>());

            public Task<SubscriberResolutionResult> ResolveSubscriberAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(SubscriberResolutionResult.NotFound());
            public Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public Task<TilopayAdminOperationResult> PauseSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("El bug no se arregla tocando al suscriptor en TiloPay.");
            public Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("El bug no se arregla tocando al suscriptor en TiloPay.");
            public Task<TilopayAdminOperationResult> DeleteSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("El bug no se arregla tocando al suscriptor en TiloPay.");
            public Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(string subscriberId, TilopaySubscriberStatus status, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("El bug no se arregla tocando al suscriptor en TiloPay.");
        }

        private sealed class Harness : IDisposable
        {
            private readonly IDisposable _connection;

            public ApplicationDbContext Db { get; }
            public MutableClock Clock { get; } = new();
            public FakeAdmin Admin { get; } = new();
            public bool AutoSuspend { get; set; }
            public Guid TenantId { get; } = Guid.NewGuid();
            public Guid PlanId { get; } = Guid.NewGuid();
            public Guid SubscriptionId { get; } = Guid.NewGuid();

            private Harness(ApplicationDbContext db, IDisposable connection)
            {
                Db = db;
                _connection = connection;
            }

            public static async Task<Harness> CreateAsync(DateTime nowUtc, bool autoSuspend = false)
            {
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
                var h = new Harness(context, connection) { AutoSuspend = autoSuspend };
                h.Clock.UtcNow = nowUtc;

                context.Tenants.Add(new Tenant { Id = h.TenantId, Nombre = "Tenant Renovación", Activo = true });
                context.Planes.Add(new Plan
                {
                    Id = h.PlanId,
                    Codigo = "LC_M_01",
                    Nombre = "LC_M_01",
                    PrecioMensual = 8000m,
                    MonthlyEquivalentAmount = 8000m,
                    BillingCycle = BillingCycle.Monthly,
                    Moneda = "CRC",
                    MaxFuncionarios = 1,
                    Activo = true
                });
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();
                return h;
            }

            private IOptions<BillingPaymentRecoveryOptions> RecoveryOptions() =>
                Options.Create(new BillingPaymentRecoveryOptions
                {
                    Enabled = true,
                    GraceDays = 5,
                    AutoSuspendAfterGrace = AutoSuspend,
                    SendEmailNotifications = false
                });

            public SuscripcionService Subscriptions(IMemoryCache? cache = null)
            {
                cache ??= new MemoryCache(new MemoryCacheOptions());
                return new SuscripcionService(
                    Db, cache, new TenantCommercialAccessCache(cache), Clock,
                    Options.Create(CalculatorCatalog.BuildRepeatOptions()),
                    NullLogger<SuscripcionService>.Instance,
                    RecoveryOptions());
            }

            public PaymentRecoveryService Recovery() =>
                new(Db, new TenantExecutionContextAccessor(), Clock, RecoveryOptions(),
                    NullLogger<PaymentRecoveryService>.Instance, new FakeTenantOwnerResolver());

            public BillingReconciliationService CreateReconciliation()
            {
                Db.ChangeTracker.Clear();
                var options = Options.Create(new BillingReconciliationOptions());
                var accessor = new TenantExecutionContextAccessor();
                return new BillingReconciliationService(
                    Db,
                    Subscriptions(),
                    accessor,
                    Clock,
                    Options.Create(CalculatorCatalog.BuildRepeatOptions()),
                    options,
                    NullLogger<BillingReconciliationService>.Instance,
                    adminOptions: Options.Create(new OpcionesTilopayRepeatAdmin()),
                    // La sincronización de expire corre ANTES de la sanación, como en producción.
                    providerExpirySyncService: new ProviderExpirySyncService(
                        Db, Admin, accessor, Clock, options, NullLogger<ProviderExpirySyncService>.Instance),
                    adminService: Admin);
            }

            public async Task<TenantCommercialAccessResult> ResolveAccessAsync()
            {
                Db.ChangeTracker.Clear();
                var cache = new MemoryCache(new MemoryCacheOptions());
                var resolver = new TenantCommercialAccessResolver(
                    Db, cache, new TenantCommercialAccessCache(cache), Subscriptions(cache), Clock);
                return await resolver.ResolveAsync(TenantId);
            }

            public async Task<BillingSubscriptionSummaryViewModel?> BuildSummaryAsync()
            {
                Db.ChangeTracker.Clear();
                var cache = new MemoryCache(new MemoryCacheOptions());
                var subscriptions = Subscriptions(cache);
                var resolver = new TenantCommercialAccessResolver(
                    Db, cache, new TenantCommercialAccessCache(cache), subscriptions, Clock);
                return await new SubscriptionSummaryService(Db, subscriptions, new NoWhatsAppSettings(), resolver)
                    .BuildAsync(TenantId);
            }

            /// <summary>Suscripción mensual pagada hasta el 19/09 (local) / expire 2026-09-19 (proveedor).</summary>
            public async Task SeedPaidSubscriptionAsync(
                EstadoSuscripcion estado = EstadoSuscripcion.Activa,
                string? paymentRecoveryStatus = null,
                DateTime? lastPaymentFailedAtUtc = null,
                DateTime? fechaFinGraciaUtc = null,
                DateTime? localPeriodEndUtc = null,
                DateTime? providerPeriodEndUtc = null)
            {
                var localEnd = localPeriodEndUtc ?? LocalPeriodEndUtc;
                Db.Suscripciones.Add(new Suscripcion
                {
                    Id = SubscriptionId,
                    TenantId = TenantId,
                    PlanId = PlanId,
                    CodigoPlan = "LC_M_01",
                    Estado = estado,
                    Proveedor = PaymentProviderType.Tilopay,
                    TilopayRecurringPlanId = RecurringPlanId,
                    ProviderSubscriptionId = SubscriberId,
                    ProviderStatusRaw = "Active",
                    ProviderExpiresAtUtc = providerPeriodEndUtc ?? ProviderPeriodEndUtc,
                    ProviderExpiryRaw = "2026-09-19",
                    PaymentRecoveryStatus = paymentRecoveryStatus,
                    LastPaymentFailedAtUtc = lastPaymentFailedAtUtc,
                    FechaFinGraciaUtc = fechaFinGraciaUtc,
                    FechaInicio = localEnd.AddMonths(-1),
                    FechaFin = localEnd,
                    FechaProximoCobroUtc = localEnd,
                    FechaUltimoPagoUtc = localEnd.AddMonths(-1),
                    FechaUltimaActualizacionUtc = localEnd.AddMonths(-1)
                });
                Db.PagosSuscripcion.Add(NewPayment(EstadoPagoProveedor.Confirmado, localEnd.AddMonths(-1)));
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            /// <summary>
            /// Estado que dejó la sanación falsa: incidente Resolved (+auditoría de sanación en el mismo
            /// instante), suscripción Activa con la recuperación limpiada y "último pago" falso, proveedor
            /// pausado con el MISMO expire del período rechazado. Sin pagos posteriores al fallo.
            /// </summary>
            public async Task<Guid> SeedStateDamagedByFalseHealAsync(bool withHealAudit = true)
            {
                var healedAtUtc = FailureAtUtc.AddMinutes(15);
                Db.Suscripciones.Add(new Suscripcion
                {
                    Id = SubscriptionId,
                    TenantId = TenantId,
                    PlanId = PlanId,
                    CodigoPlan = "LC_M_01",
                    Estado = EstadoSuscripcion.Activa,
                    Proveedor = PaymentProviderType.Tilopay,
                    TilopayRecurringPlanId = RecurringPlanId,
                    ProviderSubscriptionId = SubscriberId,
                    ProviderStatusRaw = "Pause By Commerce",
                    ProviderExpiresAtUtc = ProviderPeriodEndUtc,
                    ProviderExpiryRaw = "2026-09-19",
                    FechaInicio = LocalPeriodEndUtc.AddMonths(-1),
                    FechaFin = ProviderPeriodEndUtc,
                    FechaProximoCobroUtc = ProviderPeriodEndUtc,
                    FechaUltimoPagoUtc = healedAtUtc,
                    FechaUltimaActualizacionUtc = healedAtUtc
                });
                Db.PagosSuscripcion.Add(NewPayment(EstadoPagoProveedor.Confirmado, LocalPeriodEndUtc.AddMonths(-1)));
                Db.PagosSuscripcion.Add(NewPayment(EstadoPagoProveedor.Fallido, FailureAtUtc));
                var incidentId = Guid.NewGuid();
                Db.SubscriptionPaymentIncidents.Add(new SubscriptionPaymentIncident
                {
                    Id = incidentId,
                    TenantId = TenantId,
                    SuscripcionId = SubscriptionId,
                    Scope = PaymentIncidentScope.BasePlan,
                    PlanCode = "LC_M_01",
                    TilopayRecurringPlanId = RecurringPlanId,
                    ProviderSubscriptionId = SubscriberId,
                    Status = PaymentIncidentStatus.Resolved,
                    FailureDetectedAtUtc = FailureAtUtc,
                    GraceEndsAtUtc = FailureAtUtc.AddDays(5),
                    ResolvedAtUtc = healedAtUtc,
                    FailureCount = 1,
                    CreatedAtUtc = FailureAtUtc,
                    UpdatedAtUtc = healedAtUtc
                });
                if (withHealAudit)
                {
                    Db.PlatformAuditLogs.Add(new PlatformAuditLog
                    {
                        Id = Guid.NewGuid(),
                        ActorUserId = "system",
                        ActorEmail = "system",
                        Action = PlatformAuditActions.PaymentRecoveryResolvedByProviderRenewal,
                        EntityType = PlatformAuditEntityTypes.Subscription,
                        EntityId = SubscriptionId.ToString(),
                        TenantId = TenantId,
                        Reason = "Recuperación sanada: proveedor Active, expire 2026-09-19.",
                        CreatedAtUtc = healedAtUtc
                    });
                }
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
                return incidentId;
            }

            /// <summary>Webhook repeat_payment_failed: mismo orden que SaaSPaymentService (estado → incidente).</summary>
            public async Task RegisterFailedRenewalAsync()
            {
                Db.ChangeTracker.Clear();
                var failed = NewPayment(EstadoPagoProveedor.Fallido, Clock.UtcNow);
                Db.PagosSuscripcion.Add(failed);
                await Db.SaveChangesAsync();

                await Subscriptions().RegistrarPagoFallidoAsync(
                    TenantId, PlanId, failed.Id, PaymentProviderType.Tilopay,
                    failed.ReferenciaInterna, $"TX-FAIL-{Guid.NewGuid():N}"[..16], 8000m, "CRC",
                    "Pago recurrente no aprobado. Evento repeat_payment_failed.");
                Db.ChangeTracker.Clear();

                await Recovery().RegisterFailedPaymentAsync(TenantId, RecurringPlanId, SubscriberId, "51", "Not sufficient funds");
                Db.ChangeTracker.Clear();
            }

            public async Task SeedPaymentAsync(EstadoPagoProveedor estado, DateTime atUtc)
            {
                Db.PagosSuscripcion.Add(NewPayment(estado, atUtc));
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public async Task SimulateAppliedSuccessWithoutIncidentCloseAsync(DateTime newPeriodEndUtc)
            {
                var sub = await Db.Suscripciones.IgnoreQueryFilters().SingleAsync(s => s.Id == SubscriptionId);
                sub.Estado = EstadoSuscripcion.Activa;
                sub.FechaFin = newPeriodEndUtc;
                sub.FechaProximoCobroUtc = newPeriodEndUtc;
                sub.FechaFinGraciaUtc = null;
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public async Task ClearRecoveryFieldsAsync()
            {
                var sub = await Db.Suscripciones.IgnoreQueryFilters().SingleAsync(s => s.Id == SubscriptionId);
                sub.PaymentRecoveryStatus = null;
                sub.LastPaymentFailedAtUtc = null;
                sub.FechaFinGraciaUtc = null;
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            private PagoSuscripcion NewPayment(EstadoPagoProveedor estado, DateTime atUtc) => new()
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                PlanId = PlanId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = estado,
                TilopayRecurringPlanId = RecurringPlanId,
                ProviderSubscriberId = SubscriberId,
                Monto = 8000m,
                Moneda = "CRC",
                ReferenciaInterna = $"ref-{Guid.NewGuid():N}",
                FechaCreacionUtc = atUtc,
                FechaActualizacionUtc = atUtc,
                FechaConfirmacionUtc = estado == EstadoPagoProveedor.Confirmado ? atUtc : null
            };

            public async Task<Suscripcion> GetSubscriptionAsync()
            {
                Db.ChangeTracker.Clear();
                return await Db.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == SubscriptionId);
            }

            public async Task<SubscriptionPaymentIncident> GetSingleIncidentAsync()
            {
                Db.ChangeTracker.Clear();
                return await Db.SubscriptionPaymentIncidents.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.TenantId == TenantId);
            }

            public Task<int> CountAuditAsync(string action) =>
                Db.PlatformAuditLogs.CountAsync(l => l.Action == action && l.TenantId == TenantId);

            public void Dispose()
            {
                Db.Dispose();
                _connection.Dispose();
            }
        }

        /// <summary>Sin add-on de WhatsApp: el summary no consulta settings.</summary>
        private sealed class NoWhatsAppSettings : ITenantWhatsAppSettingsService
        {
            public Task<TenantWhatsAppSettingsSnapshot> GetSettingsForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("No debería llamarse sin add-on activo.");
            public Task<TenantWhatsAppSettingsSnapshot> EnsureDefaultSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();
            public Task<bool> IsWhatsAppEnabledForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);
            public Task<TenantWhatsAppSendDecision> CanSendNotificationAsync(Guid tenantId, string notificationType, long? reservedMessageLogId = null, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();
            public Task<int> GetTodayUsageAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                Task.FromResult(0);
            public Task<bool> HasActiveWhatsAppAddonAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);
            public Task UpdateSettingsAsync(Guid tenantId, TenantWhatsAppSettingsUpdateDto dto, string? updatedByUserId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();
        }
    }
}
