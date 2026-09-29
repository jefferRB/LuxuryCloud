using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.BusinessTime;
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
    /// Reintento de cancelación del suscriptor VIEJO tras un cambio de plan. Es el punto exacto
    /// donde un fallo silencioso deja al cliente con DOS suscripciones cobrando en TiloPay.
    ///
    /// Reproduce la regresión de un upgrade LC_M_02 → LC_M_03 con suscriptores distintos: el
    /// presupuesto de reintentos se contaba por tenant y por acción de auditoría, así que 12
    /// "intentos" que nunca llamaron a TiloPay (AutoCancel apagado, IDs sin reparar) bloqueaban
    /// 24h el primer intento REAL. Estos tests fijan la regla: solo un intento contra el
    /// proveedor gasta presupuesto, y un cambio de estado (reparación, encender AutoCancel)
    /// siempre habilita un intento inmediato.
    /// </summary>
    public class PlanChangeCancellationRetryTests
    {
        private const string OldSubscriberId = "900770";  // LC_M_02, plan repeat 7102
        private const string NewSubscriberId = "900370";  // LC_M_03, plan repeat 7103
        private const int OldRecurringPlanId = 7102;
        private const int NewRecurringPlanId = 7103;

        // ── 1. Skips con AutoCancel=false NO consumen presupuesto ──

        [Fact]
        public async Task Retry_TwelveSkipsWithAutoCancelDisabled_DoNotBlockFirstRealAttemptWhenEnabled()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            // Auditar cada skip (no solo el primero) para que el caso sea el del reporte: 12 filas.
            h.ReconOptions.AlertCooldownHours = 1;
            h.AdminOptions.AutoCancelOldSubscriberOnUpgrade = false;

            for (var pass = 0; pass < 12; pass++)
            {
                await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();
                h.Clock.Advance(TimeSpan.FromHours(2));
            }

            Assert.Empty(h.Admin.DeletedSubscriberIds); // Jamás se llamó a TiloPay.
            Assert.Equal(12, await h.CountAuditAsync(
                PlatformAuditActions.PlanChangeOldSubscriberCancellationSkippedAutoCancelDisabled));
            Assert.Equal(0, await h.CountAuditAsync(
                PlatformAuditActions.PlanChangeOldSubscriberCancellationRetried));

            // El presupuesto sigue intacto: un skip no es un intento.
            var beforeEnable = await h.GetIntentAsync(intentId);
            Assert.Equal(0, beforeEnable.OldCancellationAttemptCount);
            Assert.Null(beforeEnable.OldCancellationNextRetryUtc);

            // Al encender AutoCancel el intento debe ocurrir YA, sin esperar al día siguiente.
            h.AdminOptions.AutoCancelOldSubscriberOnUpgrade = true;
            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Equal(new[] { OldSubscriberId }, h.Admin.DeletedSubscriberIds);
            Assert.Equal(1, report.OldSubscriberCancellationsRetried);

            var intent = await h.GetIntentAsync(intentId);
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
        }

        // ── 2. Reintentos previos a la reparación no bloquean el primer intento post-repair ──

        [Fact]
        public async Task Retry_TwelveRealAttemptsBeforeRepair_DoNotBlockFirstAttemptAfterRepair()
        {
            using var h = await Harness.CreateAsync();

            // Estado roto exacto del bug: el pago confirmado sabe el suscriptor nuevo, pero el
            // intent quedó sin él y la suscripción seguía apuntando al viejo.
            var intentId = await h.SeedAppliedUpgradeAsync(
                h.TenantId,
                newSubscriberOnIntent: null,
                subscriptionSubscriberId: OldSubscriberId);

            // 12 intentos REALES ya gastados y backoff al máximo, todos previos a la reparación.
            await h.ExhaustBudgetAsync(intentId, attempts: 12, nextRetryUtc: h.NowUtc.AddHours(24));

            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            // La reparación rellenó el suscriptor nuevo y reinició el presupuesto.
            Assert.Equal(1, report.PlanChangesRepaired);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeInconsistentStateRepaired));

            // Y el intento real ocurrió en el MISMO pase, no 24h después.
            Assert.Equal(new[] { OldSubscriberId }, h.Admin.DeletedSubscriberIds);
            Assert.Equal(1, report.OldSubscriberCancellationsRetried);

            var intent = await h.GetIntentAsync(intentId);
            Assert.Equal(NewSubscriberId, intent.NewProviderSubscriptionId);
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
            Assert.Equal(1, intent.OldCancellationAttemptCount); // Contador reiniciado, este es el #1.
        }

        // ── 3. El caso de producción: auditorías viejas por tenant no bloquean ──

        [Fact]
        public async Task Retry_EligibleIntent_AttemptsCancellationDespitePreRepairTenantAudits()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            // Las 12 auditorías que había en producción: por TenantId y sin EntityId, escritas por
            // el diseño viejo. No deben significar nada para el presupuesto del intent.
            for (var i = 0; i < 12; i++)
            {
                h.Db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = PlatformAuditActions.PlanChangeOldSubscriberCancellationRetried,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    TenantId = h.TenantId,
                    Reason = "Reintento del diseño anterior (sin EntityId).",
                    CreatedAtUtc = h.NowUtc.AddMinutes(-200 + (i * 15))
                });
            }
            await h.Db.SaveChangesAsync();

            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Equal(1, report.OldSubscriberCancellationsRetried);
            Assert.Equal(new[] { OldSubscriberId }, h.Admin.DeletedSubscriberIds);

            // La verificación se hizo contra el plan VIEJO (getSuscriptorRepeat(7102)).
            Assert.Contains(OldRecurringPlanId, h.Admin.GetSuscriptorCalls);

            var intent = await h.GetIntentAsync(intentId);
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
        }

        // ── 4. El viejo sigue Activo tras la baja: queda pendiente y health marca riesgo ──

        [Fact]
        public async Task Retry_OldSubscriberStillActiveAfterCancel_StaysPendingAndHealthFlagsRisk()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            // TiloPay responde 200 pero NO da de baja: el caso peligroso.
            h.Admin.DeleteRemovesSubscriber = false;

            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Equal(1, report.OldSubscriberCancellationsRetried);
            Assert.Equal(0, report.OldSubscriberCancellationsCompleted);

            var intent = await h.GetIntentAsync(intentId);
            Assert.Equal(ProviderCancellationState.PendingManualCancellation, intent.OldProviderCancellation);
            Assert.Equal(1, await h.CountAuditAsync(
                PlatformAuditActions.PlanChangeOldSubscriberCancellationVerificationFailed));

            var health = await h.Health.BuildAsync();
            Assert.Equal(1, health.OldCancellationPendingCount);
            Assert.Equal(1, health.OldCancellationVerifiedStillActiveCount);
            Assert.Equal(1, health.OldCancellationMaxAttemptCount);
            Assert.True(health.ProviderCancellationsFailedLast7d >= 1);
        }

        // ── 5. El viejo ausente o inactivo cierra el intent ──

        [Fact]
        public async Task Retry_OldSubscriberAbsentInProvider_MarksCancelled()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            // Ya no existe en TiloPay (alguien lo borró antes): delete falla, pero la verificación
            // manda. Idempotencia: el resultado correcto es Cancelled, no un error eterno.
            h.Admin.SubscribersByPlan[OldRecurringPlanId].Clear();
            h.Admin.DeleteResult = TilopayAdminOperationResult.Fail("no existe");
            h.Admin.EditResult = TilopayAdminOperationResult.Fail("no existe");

            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            var intent = await h.GetIntentAsync(intentId);
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
            Assert.Equal(1, report.OldSubscriberCancellationsCompleted);
            Assert.Null(intent.OldCancellationNextRetryUtc); // Nada que reintentar.

            var health = await h.Health.BuildAsync();
            Assert.Equal(0, health.OldCancellationPendingCount);
        }

        [Fact]
        public async Task Retry_OldSubscriberInactiveInProvider_MarksCancelled()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            // Presente pero status 4 (Eliminado): ya no puede cobrar ⇒ no hay doble cobro.
            h.Admin.SubscribersByPlan[OldRecurringPlanId] = new List<TilopaySubscriber>
            {
                new() { SubscriberId = OldSubscriberId, Status = "4" }
            };
            h.Admin.DeleteRemovesSubscriber = false;

            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            var intent = await h.GetIntentAsync(intentId);
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
        }

        // ── 6. El backoff REAL sí frena, y deja dicho cuándo vuelve ──

        [Fact]
        public async Task Retry_AfterRealAttempt_BackoffBlocksNextPassAndAuditsNextEligibleUtc()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            // La baja falla de verdad: el intent queda pendiente y entra en backoff.
            h.Admin.DeleteRemovesSubscriber = false;
            h.Admin.DeleteResult = TilopayAdminOperationResult.Fail("TiloPay 500");
            h.Admin.EditResult = TilopayAdminOperationResult.Fail("TiloPay 500");

            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();
            Assert.Single(h.Admin.DeletedSubscriberIds);

            var afterFirst = await h.GetIntentAsync(intentId);
            Assert.Equal(1, afterFirst.OldCancellationAttemptCount);
            Assert.Equal(h.NowUtc.AddMinutes(5), afterFirst.OldCancellationNextRetryUtc);

            // Pase inmediato: NO debe volver a llamar a TiloPay.
            var blockedReport = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();
            Assert.Single(h.Admin.DeletedSubscriberIds);
            Assert.Equal(0, blockedReport.OldSubscriberCancellationsRetried);
            Assert.Equal(1, blockedReport.OldCancellationSkippedBackoff);

            var backoffAudit = await h.Db.PlatformAuditLogs.SingleAsync(log =>
                log.Action == PlatformAuditActions.PlanChangeOldSubscriberCancellationSkippedBackoff);
            Assert.Equal(intentId.ToString(), backoffAudit.EntityId);
            Assert.Equal(h.TenantId, backoffAudit.TenantId);
            Assert.Contains("nextEligibleUtc", backoffAudit.AfterJson);
            Assert.Contains("attemptCount", backoffAudit.AfterJson);

            // Pasada la ventana, vuelve a intentar y el backoff escala a 15 min.
            h.Clock.Advance(TimeSpan.FromMinutes(6));
            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Equal(2, h.Admin.DeletedSubscriberIds.Count);
            var afterSecond = await h.GetIntentAsync(intentId);
            Assert.Equal(2, afterSecond.OldCancellationAttemptCount);
            Assert.Equal(h.NowUtc.AddMinutes(15), afterSecond.OldCancellationNextRetryUtc);
        }

        [Fact]
        public async Task Retry_DailyCapPerIntent_BlocksOnlyAfterRealAttempts()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            h.ReconOptions.OldCancellationRetryMaxAttemptsPerIntentPerDay = 2;
            h.Admin.DeleteRemovesSubscriber = false;
            h.Admin.DeleteResult = TilopayAdminOperationResult.Fail("TiloPay 500");
            h.Admin.EditResult = TilopayAdminOperationResult.Fail("TiloPay 500");

            // Dos intentos reales, saltando el backoff con el reloj.
            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();
            h.Clock.Advance(TimeSpan.FromMinutes(10));
            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();
            Assert.Equal(2, h.Admin.DeletedSubscriberIds.Count);

            // El tercero cae en el tope diario, aunque el backoff ya haya vencido.
            h.Clock.Advance(TimeSpan.FromHours(2));
            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Equal(2, h.Admin.DeletedSubscriberIds.Count);
            Assert.Equal(1, report.OldCancellationSkippedBackoff);
        }

        // ── 7. Aislamiento entre tenants ──

        [Fact]
        public async Task Retry_DoesNotMixTenants()
        {
            using var h = await Harness.CreateAsync();
            var otherTenantId = Guid.NewGuid();

            var intentA = await h.SeedAppliedUpgradeAsync(h.TenantId);
            var intentB = await h.SeedAppliedUpgradeAsync(
                otherTenantId,
                oldSubscriberId: "111111",
                newSubscriber: "222222");

            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            // Cada tenant canceló SU propio suscriptor viejo, ninguno el del otro.
            Assert.Equal(2, report.OldSubscriberCancellationsRetried);
            Assert.Equal(new[] { OldSubscriberId, "111111" }.OrderBy(x => x), h.Admin.DeletedSubscriberIds.OrderBy(x => x));

            Assert.Equal(ProviderCancellationState.Cancelled, (await h.GetIntentAsync(intentA)).OldProviderCancellation);
            Assert.Equal(ProviderCancellationState.Cancelled, (await h.GetIntentAsync(intentB)).OldProviderCancellation);

            var auditA = await h.Db.PlatformAuditLogs.SingleAsync(log =>
                log.Action == PlatformAuditActions.PlanChangeOldSubscriberCancellationRetried &&
                log.EntityId == intentA.ToString());
            Assert.Equal(h.TenantId, auditA.TenantId);

            var auditB = await h.Db.PlatformAuditLogs.SingleAsync(log =>
                log.Action == PlatformAuditActions.PlanChangeOldSubscriberCancellationRetried &&
                log.EntityId == intentB.ToString());
            Assert.Equal(otherTenantId, auditB.TenantId);
        }

        [Fact]
        public async Task Retry_TenantInBackoff_DoesNotBlockAnotherTenant()
        {
            using var h = await Harness.CreateAsync();
            var otherTenantId = Guid.NewGuid();

            var intentA = await h.SeedAppliedUpgradeAsync(h.TenantId);
            var intentB = await h.SeedAppliedUpgradeAsync(
                otherTenantId,
                oldSubscriberId: "111111",
                newSubscriber: "222222");

            // El tenant A ya agotó su presupuesto; el B nunca intentó nada.
            await h.ExhaustBudgetAsync(intentA, attempts: 12, nextRetryUtc: h.NowUtc.AddHours(24));

            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Equal(new[] { "111111" }, h.Admin.DeletedSubscriberIds);
            Assert.Equal(1, report.OldSubscriberCancellationsRetried);
            Assert.Equal(1, report.OldCancellationSkippedBackoff);
            Assert.Equal(ProviderCancellationState.Cancelled, (await h.GetIntentAsync(intentB)).OldProviderCancellation);
            Assert.Equal(ProviderCancellationState.PendingManualCancellation, (await h.GetIntentAsync(intentA)).OldProviderCancellation);
        }

        // ── 8. La auditoría nunca filtra datos sensibles ──

        [Fact]
        public async Task Retry_Audits_MaskSubscriberIdsAndSecrets()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            h.Admin.DeleteRemovesSubscriber = false;
            h.Admin.DeleteResult = TilopayAdminOperationResult.Fail("TiloPay 500");
            h.Admin.EditResult = TilopayAdminOperationResult.Fail("TiloPay 500");

            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();  // intento real
            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();  // skip por backoff

            h.AdminOptions.AutoCancelOldSubscriberOnUpgrade = false;
            h.Clock.Advance(TimeSpan.FromHours(2));
            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();  // skip por AutoCancel

            var audits = await h.Db.PlatformAuditLogs.ToListAsync();
            Assert.NotEmpty(audits);

            foreach (var audit in audits)
            {
                var payload = $"{audit.Reason} {audit.AfterJson} {audit.BeforeJson} {audit.EntityId}";

                // Nunca el id_suscriptor completo: solo el sufijo enmascarado.
                Assert.DoesNotContain(OldSubscriberId, payload);
                Assert.DoesNotContain(NewSubscriberId, payload);
                // Ni credenciales, ni correo del cliente.
                Assert.DoesNotContain(Harness.ClientEmail, payload);
                Assert.DoesNotContain(Harness.ApiKey, payload);
                Assert.DoesNotContain(Harness.ApiPassword, payload);
            }

            // Y el sufijo sí está, que es lo que soporte necesita para identificar el suscriptor.
            var attemptAudit = await h.Db.PlatformAuditLogs.FirstAsync(log =>
                log.Action == PlatformAuditActions.PlanChangeOldSubscriberCancellationRetried);
            Assert.Contains("***0770", attemptAudit.Reason);
        }

        // ── Retry forzado por soporte ──

        [Fact]
        public async Task ForceRetry_IgnoresBackoffAndAuditsForcedRetry()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            // Intent castigado por el backoff: sin forzar, no se tocaría hasta dentro de 24h.
            await h.ExhaustBudgetAsync(intentId, attempts: 12, nextRetryUtc: h.NowUtc.AddHours(24));

            var outcome = await h.Reconciliation.ForceOldSubscriberCancellationRetryAsync(
                intentId, "user-1", "soporte@luxurycloud.cr");

            Assert.Equal(PlanChangeCancellationRetryStatus.Cancelled, outcome.Status);
            Assert.Equal(new[] { OldSubscriberId }, h.Admin.DeletedSubscriberIds);
            Assert.Equal(1, await h.CountAuditAsync(
                PlatformAuditActions.PlanChangeOldSubscriberCancellationForcedRetry));

            var intent = await h.GetIntentAsync(intentId);
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
        }

        [Fact]
        public async Task ForceRetry_WithAutoCancelDisabled_DoesNotCallProvider()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);
            h.AdminOptions.AutoCancelOldSubscriberOnUpgrade = false;

            var outcome = await h.Reconciliation.ForceOldSubscriberCancellationRetryAsync(
                intentId, "user-1", "soporte@luxurycloud.cr");

            // Forzar salta el backoff, NUNCA la elegibilidad.
            Assert.Equal(PlanChangeCancellationRetryStatus.SkippedAutoCancelDisabled, outcome.Status);
            Assert.Empty(h.Admin.DeletedSubscriberIds);
        }

        // ── Guardas de elegibilidad ──

        [Fact]
        public async Task Retry_WithoutNewSubscriberAnywhere_SkipsWithoutSpendingBudget()
        {
            using var h = await Harness.CreateAsync();

            // Ni el intent ni el pago conocen el suscriptor nuevo: cancelar "el viejo" a ciegas
            // podría matar la suscripción que está pagando.
            var intentId = await h.SeedAppliedUpgradeAsync(
                h.TenantId,
                newSubscriberOnIntent: null,
                paymentSubscriberId: null);

            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Empty(h.Admin.DeletedSubscriberIds);
            Assert.Equal(1, report.OldCancellationSkippedNotEligible);

            var intent = await h.GetIntentAsync(intentId);
            Assert.Equal(0, intent.OldCancellationAttemptCount);
            Assert.Equal(1, await h.CountAuditAsync(
                PlatformAuditActions.PlanChangeOldSubscriberCancellationSkippedNotEligible));
        }

        [Fact]
        public async Task Retry_WithoutOldRecurringPlanId_SkipsBecauseCancellationCouldNotBeVerified()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            // Dato legacy: sin el plan viejo, getSuscriptorRepeat no puede confirmar la baja. En
            // este módulo un 200 sin verificar no basta, así que se salta y queda para soporte.
            await h.ClearOldRecurringPlanIdAsync(intentId);

            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Empty(h.Admin.DeletedSubscriberIds);
            Assert.Equal(1, report.OldCancellationSkippedNotEligible);
            Assert.Equal(0, (await h.GetIntentAsync(intentId)).OldCancellationAttemptCount);

            // Y sigue visible como riesgo: no se abandona en silencio.
            var health = await h.Health.BuildAsync();
            Assert.Equal(1, health.OldCancellationPendingCount);
            Assert.Equal(1, health.OldCancellationSkippedNotEligibleCount);
        }

        [Fact]
        public async Task Retry_WhenOldAndNewSubscriberAreTheSame_DoesNotCancelAndRefundsBudget()
        {
            using var h = await Harness.CreateAsync();

            // Mismo id ⇒ no hay doble cobro y cancelarlo mataría la suscripción viva.
            var intentId = await h.SeedAppliedUpgradeAsync(
                h.TenantId,
                newSubscriberOnIntent: OldSubscriberId,
                subscriptionSubscriberId: OldSubscriberId,
                paymentSubscriberId: OldSubscriberId);

            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Empty(h.Admin.DeletedSubscriberIds);
            var intent = await h.GetIntentAsync(intentId);
            Assert.Equal(0, intent.OldCancellationAttemptCount); // Presupuesto devuelto.
        }

        [Fact]
        public async Task Health_PendingItem_ExposesMaskedDetailForSupport()
        {
            using var h = await Harness.CreateAsync();
            var intentId = await h.SeedAppliedUpgradeAsync(h.TenantId);

            h.Admin.DeleteRemovesSubscriber = false;
            await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            var health = await h.Health.BuildAsync();
            var item = Assert.Single(health.OldCancellationPendingItems);

            Assert.Equal(intentId, item.IntentId);
            Assert.Equal(h.TenantId, item.TenantId);
            Assert.Equal(OldRecurringPlanId, item.OldRecurringPlanId);
            Assert.True(item.VerifiedStillActive);
            Assert.Equal(1, item.AttemptCount);

            // Sufijos, nunca el id completo: la vista la abre un humano.
            Assert.Equal("***0770", item.OldSubscriberSuffix);
            Assert.Equal("***0370", item.NewSubscriberSuffix);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 5)]
        [InlineData(2, 15)]
        [InlineData(3, 30)]
        [InlineData(4, 60)]
        [InlineData(5, 360)]
        [InlineData(8, 360)]
        [InlineData(9, 1440)]
        [InlineData(50, 1440)]
        public void Backoff_FollowsTheLadderAndNeverStops(int attemptCount, int expectedMinutes)
        {
            Assert.Equal(
                TimeSpan.FromMinutes(expectedMinutes),
                PlanChangeCancellationBackoff.DelayAfterAttempt(attemptCount));
        }

        // ── Infraestructura ──

        private sealed class MutableClock : IBusinessDateTimeProvider
        {
            private DateTimeOffset _now;

            public MutableClock(DateTime utcNow) => _now = new DateTimeOffset(utcNow, TimeSpan.Zero);

            public DateTime Now() => _now.DateTime;
            public DateTime Today() => Now().Date;
            public DateTimeOffset NowOffset() => _now;
            public void Advance(TimeSpan by) => _now = _now.Add(by);
        }

        /// <summary>Fake de TiloPay Repeat con suscriptores POR PLAN, como getSuscriptorRepeat real.</summary>
        private sealed class FakeAdmin : ITilopayRepeatAdminService
        {
            public bool IsEnabled { get; set; } = true;
            public Dictionary<int, List<TilopaySubscriber>> SubscribersByPlan { get; } = new();
            public List<string> DeletedSubscriberIds { get; } = new();
            public List<int> GetSuscriptorCalls { get; } = new();
            public TilopayAdminOperationResult DeleteResult { get; set; } = TilopayAdminOperationResult.Ok("ok");
            public TilopayAdminOperationResult EditResult { get; set; } = TilopayAdminOperationResult.Ok("edited");

            /// <summary>False simula el caso peligroso: TiloPay responde 200 pero no da de baja.</summary>
            public bool DeleteRemovesSubscriber { get; set; } = true;

            public Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(int tilopayPlanId, CancellationToken cancellationToken = default)
            {
                GetSuscriptorCalls.Add(tilopayPlanId);
                return Task.FromResult<IReadOnlyList<TilopaySubscriber>>(
                    SubscribersByPlan.TryGetValue(tilopayPlanId, out var list)
                        ? list.ToList()
                        : new List<TilopaySubscriber>());
            }

            public Task<TilopayAdminOperationResult> DeleteSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default)
            {
                DeletedSubscriberIds.Add(subscriberId);

                if (DeleteResult.Succeeded && DeleteRemovesSubscriber)
                {
                    foreach (var list in SubscribersByPlan.Values)
                    {
                        list.RemoveAll(s => string.Equals(s.SubscriberId, subscriberId, StringComparison.OrdinalIgnoreCase));
                    }
                }

                return Task.FromResult(DeleteResult);
            }

            public Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(string subscriberId, TilopaySubscriberStatus status, CancellationToken cancellationToken = default)
            {
                if (EditResult.Succeeded && DeleteRemovesSubscriber)
                {
                    foreach (var list in SubscribersByPlan.Values)
                    {
                        list.RemoveAll(s => string.Equals(s.SubscriberId, subscriberId, StringComparison.OrdinalIgnoreCase));
                    }
                }

                return Task.FromResult(EditResult);
            }

            public Task<SubscriberResolutionResult> ResolveSubscriberAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(SubscriberResolutionResult.NotFound());

            public Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default)
            {
                GetSuscriptorCalls.Add(tilopayPlanId);
                var matches = SubscribersByPlan.TryGetValue(tilopayPlanId, out var list)
                    ? list.Where(s => string.Equals(s.Email, email, StringComparison.OrdinalIgnoreCase)).ToList()
                    : new List<TilopaySubscriber>();

                return Task.FromResult(TargetSubscriberAssessment.FromMatches(matches, tilopayPlanId));
            }

            public Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("ok", "https://tp.cr/l/x"));

            public Task<TilopayAdminOperationResult> PauseSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("paused"));

            public Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("reactivated"));
        }

        private sealed class Harness : IDisposable
        {
            public const string ClientEmail = "buyer2@example.com";
            public const string ApiKey = "tilopay-api-key-super-secreta";
            public const string ApiPassword = "tilopay-password-super-secreta";

            private readonly IDisposable _connection;

            /// <summary>Los pagos llevan índice único por (Proveedor, ProviderTransactionId): cada seed necesita su tx.</summary>
            private int _seedCount;

            public ApplicationDbContext Db { get; private init; } = null!;
            public FakeAdmin Admin { get; } = new();
            public OpcionesTilopayRepeatAdmin AdminOptions { get; } = new()
            {
                Enabled = true,
                BlockDuplicateCheckout = true,
                AutoCancelOldSubscriberOnUpgrade = true
            };
            public BillingReconciliationOptions ReconOptions { get; } = new();
            public MutableClock Clock { get; private set; } = null!;
            public BillingReconciliationService Reconciliation { get; private set; } = null!;
            public BillingHealthService Health { get; private set; } = null!;
            public Guid TenantId { get; } = Guid.NewGuid();

            public DateTime NowUtc => Clock.NowOffset().UtcDateTime;

            private Harness(IDisposable connection) => _connection = connection;

            public static Task<Harness> CreateAsync()
            {
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
                var h = new Harness(connection) { Db = context };

                // BillingHealthService usa DateTime.UtcNow directo, así que el reloj de la suite
                // arranca en "ahora" para que las ventanas de 24h/7d del health sean reales.
                h.Clock = new MutableClock(DateTime.UtcNow);

                var repeatOptions = CalculatorCatalog.BuildRepeatOptions();
                var tenantAccessor = new TenantExecutionContextAccessor();
                var cache = new MemoryCache(new MemoryCacheOptions());
                var subscriptionService = new SuscripcionService(
                    context,
                    cache,
                    new TenantCommercialAccessCache(cache),
                    h.Clock,
                    Options.Create(repeatOptions),
                    NullLogger<SuscripcionService>.Instance);

                // Options.Create envuelve la MISMA instancia: mutar AdminOptions/ReconOptions en un
                // test cambia la configuración vista por los servicios, como un reload en producción.
                var providerManager = new ProviderSubscriptionManager(
                    context,
                    h.Admin,
                    tenantAccessor,
                    h.Clock,
                    Options.Create(h.AdminOptions),
                    NullLogger<ProviderSubscriptionManager>.Instance);

                h.Reconciliation = new BillingReconciliationService(
                    context,
                    subscriptionService,
                    tenantAccessor,
                    h.Clock,
                    Options.Create(repeatOptions),
                    Options.Create(h.ReconOptions),
                    NullLogger<BillingReconciliationService>.Instance,
                    subscriberResolutionService: null,
                    adminOptions: Options.Create(h.AdminOptions),
                    providerSubscriptionManager: providerManager);

                h.Health = new BillingHealthService(context, subscriptionService);

                return Task.FromResult(h);
            }

            /// <summary>
            /// Deja el estado de un upgrade YA aplicado con la cancelación del viejo pendiente,
            /// que es donde vive el riesgo de doble cobro.
            /// </summary>
            public async Task<Guid> SeedAppliedUpgradeAsync(
                Guid tenantId,
                string oldSubscriberId = OldSubscriberId,
                string newSubscriber = NewSubscriberId,
                string? newSubscriberOnIntent = NewSubscriberId,
                string? subscriptionSubscriberId = null,
                string? paymentSubscriberId = NewSubscriberId,
                bool distinctNewSubscriber = false)
            {
                if (distinctNewSubscriber)
                {
                    newSubscriberOnIntent = newSubscriber;
                }

                var transactionId = $"538938{++_seedCount}";

                if (!Db.Tenants.Local.Any(t => t.Id == tenantId) &&
                    !await Db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
                {
                    Db.Tenants.Add(new Tenant { Id = tenantId, Nombre = $"Tenant {tenantId:N}"[..14], Activo = true });
                }

                var oldPlan = NewPlan("LC_M_02", 2, 15000m);
                var newPlan = NewPlan("LC_M_03", 3, 20000m);
                Db.Planes.AddRange(oldPlan, newPlan);

                var paymentId = Guid.NewGuid();
                Db.PagosSuscripcion.Add(new PagoSuscripcion
                {
                    Id = paymentId,
                    TenantId = tenantId,
                    PlanId = newPlan.Id,
                    Proveedor = PaymentProviderType.Tilopay,
                    Estado = EstadoPagoProveedor.Confirmado,
                    ReferenciaInterna = $"LXA-{Guid.NewGuid():N}"[..20],
                    ProviderReference = Guid.NewGuid().ToString("N"),
                    ProviderTransactionId = transactionId,
                    ProviderSubscriberId = paymentSubscriberId is null
                        ? null
                        : (paymentSubscriberId == NewSubscriberId ? newSubscriber : paymentSubscriberId),
                    TilopayRecurringPlanId = NewRecurringPlanId,
                    ClienteEmail = ClientEmail,
                    Monto = 20000m,
                    Moneda = "CRC",
                    FechaCreacionUtc = NowUtc.AddMinutes(-30),
                    FechaConfirmacionUtc = NowUtc.AddMinutes(-25),
                    FechaActualizacionUtc = NowUtc.AddMinutes(-25)
                });

                // Suscripción ya en el plan destino (el cambio se aplicó).
                Db.Suscripciones.Add(new Suscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PlanId = newPlan.Id,
                    CodigoPlan = "LC_M_03",
                    Estado = EstadoSuscripcion.Activa,
                    Proveedor = PaymentProviderType.Tilopay,
                    TilopayRecurringPlanId = NewRecurringPlanId,
                    ProviderSubscriptionId = subscriptionSubscriberId ?? newSubscriber,
                    ProviderTransactionId = transactionId,
                    FechaInicio = NowUtc.AddMinutes(-25),
                    FechaFin = NowUtc.AddMinutes(-25).AddMonths(1),
                    FechaProximoCobroUtc = NowUtc.AddMinutes(-25).AddMonths(1)
                });

                var intentId = Guid.NewGuid();
                Db.PlanChangeIntents.Add(new PlanChangeIntent
                {
                    Id = intentId,
                    TenantId = tenantId,
                    FromPlanId = oldPlan.Id,
                    FromPlanCode = "LC_M_02",
                    FromWorkerCount = 2,
                    FromTilopayRecurringPlanId = OldRecurringPlanId,
                    FromProviderSubscriptionId = oldSubscriberId,
                    ToPlanId = newPlan.Id,
                    ToPlanCode = "LC_M_03",
                    ToWorkerCount = 3,
                    ToBillingCycle = BillingCycle.Monthly,
                    ToTilopayRecurringPlanId = NewRecurringPlanId,
                    Estado = PlanChangeIntentState.Applied,
                    OldProviderCancellation = ProviderCancellationState.PendingManualCancellation,
                    PagoSuscripcionId = paymentId,
                    NewProviderSubscriptionId = newSubscriberOnIntent is null
                        ? null
                        : (newSubscriberOnIntent == NewSubscriberId ? newSubscriber : newSubscriberOnIntent),
                    CreatedAtUtc = NowUtc.AddMinutes(-40),
                    AppliedAtUtc = NowUtc.AddMinutes(-25)
                });

                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();

                // Estado en TiloPay: viejo y nuevo ACTIVOS a la vez (el doble cobro real).
                Admin.SubscribersByPlan[OldRecurringPlanId] = new List<TilopaySubscriber>
                {
                    new() { SubscriberId = oldSubscriberId, Email = ClientEmail, Status = "Active" }
                };
                Admin.SubscribersByPlan[NewRecurringPlanId] = new List<TilopaySubscriber>
                {
                    new() { SubscriberId = newSubscriber, Email = ClientEmail, Status = "Active" }
                };

                return intentId;
            }

            /// <summary>Simula un intent que ya gastó su presupuesto de intentos reales.</summary>
            public async Task ExhaustBudgetAsync(Guid intentId, int attempts, DateTime nextRetryUtc)
            {
                var intent = await Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
                intent.OldCancellationAttemptCount = attempts;
                intent.OldCancellationLastAttemptUtc = NowUtc.AddMinutes(-10);
                intent.OldCancellationNextRetryUtc = nextRetryUtc;

                for (var i = 0; i < attempts; i++)
                {
                    Db.PlatformAuditLogs.Add(new PlatformAuditLog
                    {
                        Id = Guid.NewGuid(),
                        ActorUserId = "system",
                        ActorEmail = "system",
                        Action = PlatformAuditActions.PlanChangeOldSubscriberCancellationRetried,
                        EntityType = PlatformAuditEntityTypes.Subscription,
                        EntityId = intentId.ToString(),
                        TenantId = intent.TenantId,
                        Reason = "Intento real previo.",
                        CreatedAtUtc = NowUtc.AddHours(-3).AddMinutes(i)
                    });
                }

                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            /// <summary>Simula un intent legacy sin plan viejo (imposible de verificar).</summary>
            public async Task ClearOldRecurringPlanIdAsync(Guid intentId)
            {
                var intent = await Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
                intent.FromTilopayRecurringPlanId = null;
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public async Task<PlanChangeIntent> GetIntentAsync(Guid intentId)
            {
                Db.ChangeTracker.Clear();
                return await Db.PlanChangeIntents.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == intentId);
            }

            public Task<int> CountAuditAsync(string action) =>
                Db.PlatformAuditLogs.CountAsync(log => log.Action == action);

            private Plan NewPlan(string code, int workers, decimal price) => new()
            {
                Id = Guid.NewGuid(),
                Codigo = $"{code}-{Guid.NewGuid():N}"[..12],
                Nombre = code,
                PrecioMensual = price,
                MonthlyEquivalentAmount = price,
                BillingCycle = BillingCycle.Monthly,
                Moneda = "CRC",
                MaxFuncionarios = workers,
                Activo = true
            };

            public void Dispose()
            {
                Db.Dispose();
                _connection.Dispose();
            }
        }
    }
}
