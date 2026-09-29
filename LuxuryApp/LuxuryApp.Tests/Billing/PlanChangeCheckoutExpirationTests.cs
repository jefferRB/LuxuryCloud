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
    /// Checkout de cambio de plan ABANDONADO: el cliente abrió el link y nunca pagó.
    ///
    /// Regresión (cambio de plan hacia LC_M_04 abandonado): no hay transacción, ni
    /// suscriptor, ni confirmación, y la suscripción real sigue intacta en LC_M_03 — cero riesgo de
    /// dinero. Pero el intent quedaba Pending para siempre: ensuciaba el health y, peor, ocupaba el
    /// cupo de "un cambio abierto por tenant". La expiración genérica de pendientes (7 días) cerraba
    /// el PAGO pero nunca el INTENT.
    ///
    /// La línea que estos tests defienden: se expira SOLO lo que no tiene ninguna señal de dinero.
    /// Ante cualquier señal, el caso va a reparación o revisión, nunca a la basura.
    /// </summary>
    public class PlanChangeCheckoutExpirationTests
    {
        private const string Email = "buyer2@example.com";
        private const int FromRecurringPlanId = 7103; // LC_M_03 (plan real actual)
        private const int ToRecurringPlanId = 7104;   // LC_M_04 (destino abandonado)

        // ── 1. El escenario base: abandonado tras la ventana => expira ──

        [Fact]
        public async Task Run_AbandonedPlanChangeCheckout_ExpiresIntentAndPayment()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, paymentId) = await h.SeedAbandonedCheckoutAsync(ageHours: 30);

            var report = await h.Reconciliation.RunAsync();

            Assert.Equal(1, report.PlanChangeCheckoutsExpired);

            var intent = await h.GetIntentAsync(intentId);
            Assert.Equal(PlanChangeIntentState.Expired, intent.Estado);
            Assert.Contains("abandonado", intent.Notes ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var payment = await h.GetPaymentAsync(paymentId);
            Assert.Equal(EstadoPagoProveedor.Expirado, payment.Estado);
            Assert.Equal("EXPIRED_PLAN_CHANGE_CHECKOUT", payment.ProviderResultCode);

            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangePendingCheckoutExpired));
        }

        [Fact]
        public async Task Run_AbandonedCheckout_NeverTouchesSubscriptionOrProvider()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedAbandonedCheckoutAsync(ageHours: 30);

            var before = await h.GetSubscriptionAsync();
            var subscriptionSnapshot = new
            {
                before.PlanId,
                before.CodigoPlan,
                before.TilopayRecurringPlanId,
                before.ProviderSubscriptionId,
                before.ProviderTransactionId,
                before.Estado,
                before.FechaInicio,
                before.FechaFin
            };
            var paymentsBefore = await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync();

            await h.Reconciliation.RunAsync();

            // La suscripción real no se mueve ni un campo.
            var after = await h.GetSubscriptionAsync();
            Assert.Equal(subscriptionSnapshot.PlanId, after.PlanId);
            Assert.Equal(subscriptionSnapshot.CodigoPlan, after.CodigoPlan);
            Assert.Equal(subscriptionSnapshot.TilopayRecurringPlanId, after.TilopayRecurringPlanId);
            Assert.Equal(subscriptionSnapshot.ProviderSubscriptionId, after.ProviderSubscriptionId);
            Assert.Equal(subscriptionSnapshot.ProviderTransactionId, after.ProviderTransactionId);
            Assert.Equal(subscriptionSnapshot.Estado, after.Estado);
            Assert.Equal(subscriptionSnapshot.FechaInicio, after.FechaInicio);
            Assert.Equal(subscriptionSnapshot.FechaFin, after.FechaFin);

            // TiloPay ni se entera: sin bajas, sin consultas de suscriptor, sin cancelaciones.
            Assert.Empty(h.Admin.DeletedSubscriberIds);
            Assert.Empty(h.Admin.GetSuscriptorCalls);
            Assert.Equal(0, h.Admin.EditStatusCalls);

            // Y no se crea ningún pago nuevo.
            Assert.Equal(paymentsBefore, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
        }

        [Fact]
        public async Task Run_AbandonedCheckout_FreesTheTenantSlotForANewChange()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedAbandonedCheckoutAsync(ageHours: 30);

            await h.Reconciliation.RunAsync();

            // El índice único es "un Pending por tenant": tras expirar, el cliente puede reintentar.
            var planChangeService = new PlanChangeService(h.Db, NullLogger<PlanChangeService>.Instance);
            var result = await planChangeService.CreateOrReuseAsync(h.BuildRequest(toRecurringPlanId: 7105, toPlanCode: "LC_M_05"));

            Assert.True(result.Succeeded, result.Error);
        }

        // ── 2. Dentro de la ventana no se toca ──

        [Fact]
        public async Task Run_RecentAbandonedCheckout_IsNotExpiredYet()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, _) = await h.SeedAbandonedCheckoutAsync(ageHours: 2);

            var report = await h.Reconciliation.RunAsync();

            Assert.Equal(0, report.PlanChangeCheckoutsExpired);
            Assert.Equal(PlanChangeIntentState.Pending, (await h.GetIntentAsync(intentId)).Estado);
        }

        [Fact]
        public async Task Run_ExpirationWindowIsConfigurable()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, _) = await h.SeedAbandonedCheckoutAsync(ageHours: 5);

            h.ReconOptions.PlanChangePendingCheckoutExpirationHours = 4;

            await h.Reconciliation.RunAsync();

            Assert.Equal(PlanChangeIntentState.Expired, (await h.GetIntentAsync(intentId)).Estado);
        }

        // ── 3. Cualquier señal de dinero veta la expiración ──

        [Fact]
        public async Task Run_PaymentWithProviderTransactionId_IsNeverExpiredAsAbandoned()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, paymentId) = await h.SeedAbandonedCheckoutAsync(
                ageHours: 72,
                providerTransactionId: "5397431");

            var report = await h.Reconciliation.RunAsync();

            Assert.Equal(0, report.PlanChangeCheckoutsExpired);
            Assert.Equal(PlanChangeIntentState.Pending, (await h.GetIntentAsync(intentId)).Estado);
            Assert.Equal(EstadoPagoProveedor.Pendiente, (await h.GetPaymentAsync(paymentId)).Estado);
        }

        [Fact]
        public async Task Run_PaymentWithProviderSubscriberId_IsNeverExpiredAsAbandoned()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, _) = await h.SeedAbandonedCheckoutAsync(ageHours: 72, providerSubscriberId: "900117");

            await h.Reconciliation.RunAsync();

            Assert.Equal(PlanChangeIntentState.Pending, (await h.GetIntentAsync(intentId)).Estado);
        }

        [Fact]
        public async Task Run_ConfirmedPayment_IsNeverExpiredAsAbandoned()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, paymentId) = await h.SeedAbandonedCheckoutAsync(
                ageHours: 72,
                estado: EstadoPagoProveedor.Confirmado,
                confirmedAtUtc: DateTime.UtcNow.AddHours(-72));

            await h.Reconciliation.RunAsync();

            // Hay dinero cobrado: esto es trabajo de la aplicación tardía/repair, no de la basura.
            Assert.NotEqual(PlanChangeIntentState.Expired, (await h.GetIntentAsync(intentId)).Estado);
            Assert.Equal(EstadoPagoProveedor.Confirmado, (await h.GetPaymentAsync(paymentId)).Estado);
        }

        [Fact]
        public async Task Run_ManualReviewPayment_IsNeverExpiredAsAbandoned()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, _) = await h.SeedAbandonedCheckoutAsync(ageHours: 72, estado: EstadoPagoProveedor.ManualReview);

            await h.Reconciliation.RunAsync();

            Assert.Equal(PlanChangeIntentState.Pending, (await h.GetIntentAsync(intentId)).Estado);
        }

        [Fact]
        public async Task Run_PaymentWithProviderWebhookEvent_IsNeverExpiredAsAbandoned()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, paymentId) = await h.SeedAbandonedCheckoutAsync(ageHours: 72);

            // TiloPay tocó este intento aunque no dejó tx ni suscriptor: no se descarta a ciegas.
            h.Db.EventosPago.Add(new EventoPago
            {
                Id = Guid.NewGuid(),
                Proveedor = PaymentProviderType.Tilopay,
                TenantId = h.TenantId,
                PagoSuscripcionId = paymentId,
                ProveedorEventId = $"evt-{Guid.NewGuid():N}",
                Tipo = "tilopay.repeat.notification",
                Procesado = false,
                EstadoProcesamiento = "Recibido",
                FechaRecepcionUtc = DateTime.UtcNow.AddHours(-70)
            });
            await h.Db.SaveChangesAsync();

            await h.Reconciliation.RunAsync();

            Assert.Equal(PlanChangeIntentState.Pending, (await h.GetIntentAsync(intentId)).Estado);
        }

        // ── 4. La cola del bug original: pago ya expirado, intent huérfano ──

        [Fact]
        public async Task Run_PaymentAlreadyExpiredByGenericCleanup_StillExpiresTheOrphanIntent()
        {
            using var h = await Harness.CreateAsync();

            // La limpieza genérica de pendientes (7 días) expira el PAGO pero nunca el INTENT: así
            // quedaba el Pending eterno que ensuciaba el health. Esta fase lo termina de cerrar.
            var (intentId, paymentId) = await h.SeedAbandonedCheckoutAsync(
                ageHours: 24 * 9,
                estado: EstadoPagoProveedor.Expirado);

            await h.Reconciliation.RunAsync();

            Assert.Equal(PlanChangeIntentState.Expired, (await h.GetIntentAsync(intentId)).Estado);
            // El pago ya cerrado no se vuelve a tocar: su historia la contó quien lo cerró.
            var payment = await h.GetPaymentAsync(paymentId);
            Assert.Equal(EstadoPagoProveedor.Expirado, payment.Estado);
            Assert.Equal("RECURRING_PENDING", payment.ProviderResultCode);
        }

        [Fact]
        public async Task Run_FailedPayment_ClosesTheOrphanIntentToo()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, _) = await h.SeedAbandonedCheckoutAsync(ageHours: 30, estado: EstadoPagoProveedor.Fallido);

            await h.Reconciliation.RunAsync();

            // Un pago fallido no es dinero vivo: el intento no puede quedar abierto para siempre.
            Assert.Equal(PlanChangeIntentState.Expired, (await h.GetIntentAsync(intentId)).Estado);
        }

        // ── 5. Aislamiento y estados que no se tocan ──

        [Fact]
        public async Task Run_AppliedIntent_IsNeverExpired()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, _) = await h.SeedAbandonedCheckoutAsync(ageHours: 72);
            await h.SetIntentStateAsync(intentId, PlanChangeIntentState.Applied);

            await h.Reconciliation.RunAsync();

            Assert.Equal(PlanChangeIntentState.Applied, (await h.GetIntentAsync(intentId)).Estado);
        }

        [Fact]
        public async Task Run_DoesNotMixTenants()
        {
            using var h = await Harness.CreateAsync();
            var otherTenantId = Guid.NewGuid();

            var (intentA, _) = await h.SeedAbandonedCheckoutAsync(ageHours: 30);
            var (intentB, _) = await h.SeedAbandonedCheckoutAsync(ageHours: 2, tenantId: otherTenantId);

            var report = await h.Reconciliation.RunAsync();

            Assert.Equal(1, report.PlanChangeCheckoutsExpired);
            Assert.Equal(PlanChangeIntentState.Expired, (await h.GetIntentAsync(intentA)).Estado);
            Assert.Equal(PlanChangeIntentState.Pending, (await h.GetIntentAsync(intentB)).Estado);

            var audit = await h.Db.PlatformAuditLogs
                .SingleAsync(log => log.Action == PlatformAuditActions.PlanChangePendingCheckoutExpired);
            Assert.Equal(h.TenantId, audit.TenantId);
        }

        [Fact]
        public async Task Run_ExpirationAlsoRunsInTheFastWorkerPass()
        {
            using var h = await Harness.CreateAsync();
            var (intentId, _) = await h.SeedAbandonedCheckoutAsync(ageHours: 30);

            // No hay que esperar al pase diario para dejar de trabar al cliente.
            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Equal(1, report.PlanChangeCheckoutsExpired);
            Assert.Equal(PlanChangeIntentState.Expired, (await h.GetIntentAsync(intentId)).Estado);
        }

        // ── 5. Supersede al iniciar otro cambio ──

        [Fact]
        public async Task CreateOrReuse_NewTargetWithAbandonedCheckoutPending_SupersedesInsteadOfBlocking()
        {
            using var h = await Harness.CreateAsync();
            var (oldIntentId, oldPaymentId) = await h.SeedAbandonedCheckoutAsync(ageHours: 1);

            var planChangeService = new PlanChangeService(h.Db, NullLogger<PlanChangeService>.Instance);
            var result = await planChangeService.CreateOrReuseAsync(h.BuildRequest(toRecurringPlanId: 7105, toPlanCode: "LC_M_05"));

            // Aunque esté DENTRO de la ventana: el cliente pidió otro cambio, no hay dinero, no se traba.
            Assert.True(result.Succeeded, result.Error);
            Assert.NotEqual(oldIntentId, result.Intent!.Id);

            Assert.Equal(PlanChangeIntentState.Superseded, (await h.GetIntentAsync(oldIntentId)).Estado);

            // El checkout viejo se cierra: si no, un webhook tardío podría aplicar un cambio descartado.
            Assert.Equal(EstadoPagoProveedor.Expirado, (await h.GetPaymentAsync(oldPaymentId)).Estado);

            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangePendingCheckoutSuperseded));
        }

        [Fact]
        public async Task CreateOrReuse_NewTargetButOldCheckoutHasMoney_StillBlocks()
        {
            using var h = await Harness.CreateAsync();
            var (_, oldPaymentId) = await h.SeedAbandonedCheckoutAsync(ageHours: 1, providerTransactionId: "5397431");

            var planChangeService = new PlanChangeService(h.Db, NullLogger<PlanChangeService>.Instance);
            var result = await planChangeService.CreateOrReuseAsync(h.BuildRequest(toRecurringPlanId: 7105, toPlanCode: "LC_M_05"));

            // Hay una transacción del proveedor: puede haber dinero. No se toca nada.
            Assert.False(result.Succeeded);
            Assert.Contains("cambio de plan en proceso", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(EstadoPagoProveedor.Pendiente, (await h.GetPaymentAsync(oldPaymentId)).Estado);
        }

        // ── 6. Health: ruido vs riesgo ──

        [Fact]
        public async Task Health_AbandonedCheckout_CountsAsPendingCheckoutNotMoneyRisk()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedAbandonedCheckoutAsync(ageHours: 6);

            var health = await h.Health.BuildAsync();

            Assert.Equal(1, health.PlanChangePendingCount);      // sigue contando como abierto
            Assert.Equal(1, health.PlanChangePendingCheckoutCount);
            Assert.Equal(0, health.PlanChangeMoneyRiskCount);    // lo que importa: cero riesgo
            Assert.NotNull(health.OldestPendingCheckoutUtc);
            Assert.True(health.OldestPendingCheckoutAgeHours >= 5.5);
        }

        [Fact]
        public async Task Health_ConfirmedPaymentPendingToApply_CountsAsMoneyRisk()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedAbandonedCheckoutAsync(
                ageHours: 6,
                estado: EstadoPagoProveedor.Confirmado,
                providerSubscriberId: "900117",
                confirmedAtUtc: DateTime.UtcNow.AddHours(-6));

            var health = await h.Health.BuildAsync();

            Assert.Equal(0, health.PlanChangePendingCheckoutCount);
            Assert.Equal(1, health.PlanChangeMoneyRiskCount);
            Assert.Null(health.OldestPendingCheckoutUtc);
        }

        [Fact]
        public async Task Health_AfterExpiration_ShowsNoPendingAtAll()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedAbandonedCheckoutAsync(ageHours: 30);

            await h.Reconciliation.RunAsync();
            var health = await h.Health.BuildAsync();

            Assert.Equal(0, health.PlanChangePendingCount);
            Assert.Equal(0, health.PlanChangePendingCheckoutCount);
            Assert.Equal(0, health.PlanChangeMoneyRiskCount);
        }

        // ── 7. La regla de señales de dinero, aislada ──

        [Fact]
        public void MoneySignals_NoPayment_IsNotAMoneySignal()
        {
            Assert.False(PlanChangeCheckoutAbandonmentRules.HasMoneySignals(null));
        }

        [Theory]
        [InlineData(EstadoPagoProveedor.Confirmado, null, null, false, true)]
        [InlineData(EstadoPagoProveedor.ManualReview, null, null, false, true)]
        [InlineData(EstadoPagoProveedor.Pendiente, "TX-1", null, false, true)]
        [InlineData(EstadoPagoProveedor.Pendiente, null, "900117", false, true)]
        [InlineData(EstadoPagoProveedor.Pendiente, null, null, true, true)]
        [InlineData(EstadoPagoProveedor.Pendiente, null, null, false, false)]
        [InlineData(EstadoPagoProveedor.Fallido, null, null, false, false)]
        [InlineData(EstadoPagoProveedor.Cancelado, null, null, false, false)]
        [InlineData(EstadoPagoProveedor.Expirado, null, null, false, false)]
        public void MoneySignals_FollowTheTable(
            EstadoPagoProveedor estado,
            string? transactionId,
            string? subscriberId,
            bool hasProviderEvent,
            bool expected)
        {
            var payment = new PagoSuscripcion
            {
                Estado = estado,
                ProviderTransactionId = transactionId,
                ProviderSubscriberId = subscriberId
            };

            Assert.Equal(expected, PlanChangeCheckoutAbandonmentRules.HasMoneySignals(payment, hasProviderEvent));
        }

        [Fact]
        public void MoneySignals_ConfirmationDate_IsAMoneySignal()
        {
            var payment = new PagoSuscripcion
            {
                Estado = EstadoPagoProveedor.Pendiente,
                FechaConfirmacionUtc = DateTime.UtcNow
            };

            Assert.True(PlanChangeCheckoutAbandonmentRules.HasMoneySignals(payment));
        }

        [Fact]
        public void IsAbandonedCheckout_MeasuresFromTheMostRecentActivity()
        {
            var nowUtc = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
            var oldIntentUtc = nowUtc.AddHours(-48);

            // El intent es viejo pero el cliente reabrió el checkout hace un rato: no es abandono.
            var freshPayment = new PagoSuscripcion
            {
                Estado = EstadoPagoProveedor.Pendiente,
                FechaCreacionUtc = nowUtc.AddHours(-1)
            };

            Assert.False(PlanChangeCheckoutAbandonmentRules.IsAbandonedCheckout(
                freshPayment, oldIntentUtc, nowUtc, expirationHours: 24));

            var stalePayment = new PagoSuscripcion
            {
                Estado = EstadoPagoProveedor.Pendiente,
                FechaCreacionUtc = nowUtc.AddHours(-30)
            };

            Assert.True(PlanChangeCheckoutAbandonmentRules.IsAbandonedCheckout(
                stalePayment, oldIntentUtc, nowUtc, expirationHours: 24));
        }

        // ── Infraestructura ──

        /// <summary>Fake que CUENTA llamadas: la expiración no debe tocar TiloPay ni una vez.</summary>
        private sealed class CountingAdmin : ITilopayRepeatAdminService
        {
            public bool IsEnabled { get; set; } = true;
            public List<string> DeletedSubscriberIds { get; } = new();
            public List<int> GetSuscriptorCalls { get; } = new();
            public int EditStatusCalls { get; private set; }

            public Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(int tilopayPlanId, CancellationToken cancellationToken = default)
            {
                GetSuscriptorCalls.Add(tilopayPlanId);
                return Task.FromResult<IReadOnlyList<TilopaySubscriber>>(Array.Empty<TilopaySubscriber>());
            }

            public Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default)
            {
                GetSuscriptorCalls.Add(tilopayPlanId);
                return Task.FromResult(TargetSubscriberAssessment.FromMatches(Array.Empty<TilopaySubscriber>(), tilopayPlanId));
            }

            public Task<SubscriberResolutionResult> ResolveSubscriberAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default)
            {
                GetSuscriptorCalls.Add(tilopayPlanId);
                return Task.FromResult(SubscriberResolutionResult.NotFound());
            }

            public Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("ok", "https://tp.cr/l/x"));

            public Task<TilopayAdminOperationResult> PauseSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("paused"));

            public Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("reactivated"));

            public Task<TilopayAdminOperationResult> DeleteSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default)
            {
                DeletedSubscriberIds.Add(subscriberId);
                return Task.FromResult(TilopayAdminOperationResult.Ok("ok"));
            }

            public Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(string subscriberId, TilopaySubscriberStatus status, CancellationToken cancellationToken = default)
            {
                EditStatusCalls++;
                return Task.FromResult(TilopayAdminOperationResult.Ok("edited"));
            }
        }

        private sealed class Harness : IDisposable
        {
            private readonly IDisposable _connection;
            private int _seedCount;

            public ApplicationDbContext Db { get; private init; } = null!;
            public CountingAdmin Admin { get; } = new();
            public BillingReconciliationOptions ReconOptions { get; } = new();
            public BillingReconciliationService Reconciliation { get; private set; } = null!;
            public BillingHealthService Health { get; private set; } = null!;
            public Guid TenantId { get; } = Guid.NewGuid();
            public Guid CurrentPlanId { get; private set; }

            private Harness(IDisposable connection) => _connection = connection;

            public static async Task<Harness> CreateAsync()
            {
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
                var h = new Harness(connection) { Db = context };

                var repeatOptions = CalculatorCatalog.BuildRepeatOptions();
                var tenantAccessor = new TenantExecutionContextAccessor();
                var cache = new MemoryCache(new MemoryCacheOptions());
                var clock = new FixedBusinessDateTimeProvider(
                    DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));

                var subscriptionService = new SuscripcionService(
                    context, cache, new TenantCommercialAccessCache(cache), clock,
                    Options.Create(repeatOptions), NullLogger<SuscripcionService>.Instance);

                var adminOptions = Options.Create(new OpcionesTilopayRepeatAdmin
                {
                    Enabled = true,
                    AutoCancelOldSubscriberOnUpgrade = true
                });

                var providerManager = new ProviderSubscriptionManager(
                    context, h.Admin, tenantAccessor, clock, adminOptions,
                    NullLogger<ProviderSubscriptionManager>.Instance);

                h.Reconciliation = new BillingReconciliationService(
                    context,
                    subscriptionService,
                    tenantAccessor,
                    clock,
                    Options.Create(repeatOptions),
                    Options.Create(h.ReconOptions),
                    NullLogger<BillingReconciliationService>.Instance,
                    subscriberResolutionService: null,
                    adminOptions: adminOptions,
                    providerSubscriptionManager: providerManager);

                h.Health = new BillingHealthService(context, subscriptionService);

                // Estado del tenant: suscripción vigente en LC_M_03, intacta.
                context.Tenants.Add(new Tenant { Id = h.TenantId, Nombre = "Tenant abandono", Activo = true });

                var currentPlan = new Plan
                {
                    Id = Guid.NewGuid(),
                    Codigo = "LC_M_03",
                    Nombre = "LC_M_03",
                    PrecioMensual = 20000m,
                    MonthlyEquivalentAmount = 20000m,
                    BillingCycle = BillingCycle.Monthly,
                    Moneda = "CRC",
                    MaxFuncionarios = 3,
                    Activo = true
                };
                context.Planes.Add(currentPlan);
                h.CurrentPlanId = currentPlan.Id;

                context.Suscripciones.Add(new Suscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = h.TenantId,
                    PlanId = currentPlan.Id,
                    CodigoPlan = "LC_M_03",
                    Estado = EstadoSuscripcion.Activa,
                    Proveedor = PaymentProviderType.Tilopay,
                    TilopayRecurringPlanId = FromRecurringPlanId,
                    ProviderSubscriptionId = "900370",
                    ProviderTransactionId = "9000001",
                    FechaInicio = DateTime.UtcNow.AddDays(-1),
                    FechaFin = DateTime.UtcNow.AddDays(30),
                    FechaProximoCobroUtc = DateTime.UtcNow.AddDays(30)
                });

                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();

                return h;
            }

            /// <summary>Checkout de cambio abierto y nunca pagado, con la antigüedad indicada.</summary>
            public async Task<(Guid IntentId, Guid PaymentId)> SeedAbandonedCheckoutAsync(
                int ageHours,
                EstadoPagoProveedor estado = EstadoPagoProveedor.Pendiente,
                string? providerTransactionId = null,
                string? providerSubscriberId = null,
                DateTime? confirmedAtUtc = null,
                Guid? tenantId = null)
            {
                var owner = tenantId ?? TenantId;
                var createdAtUtc = DateTime.UtcNow.AddHours(-ageHours);
                _seedCount++;

                if (owner != TenantId && !await Db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == owner))
                {
                    Db.Tenants.Add(new Tenant { Id = owner, Nombre = $"Tenant {_seedCount}", Activo = true });
                }

                var targetPlan = new Plan
                {
                    Id = Guid.NewGuid(),
                    Codigo = $"LC_M_04-{_seedCount}",
                    Nombre = "LC_M_04",
                    PrecioMensual = 25000m,
                    MonthlyEquivalentAmount = 25000m,
                    BillingCycle = BillingCycle.Monthly,
                    Moneda = "CRC",
                    MaxFuncionarios = 4,
                    Activo = true
                };
                Db.Planes.Add(targetPlan);

                var paymentId = Guid.NewGuid();
                Db.PagosSuscripcion.Add(new PagoSuscripcion
                {
                    Id = paymentId,
                    TenantId = owner,
                    PlanId = targetPlan.Id,
                    Proveedor = PaymentProviderType.Tilopay,
                    Estado = estado,
                    ReferenciaInterna = $"LXA-{Guid.NewGuid():N}"[..20],
                    ProviderTransactionId = providerTransactionId,
                    ProviderSubscriberId = providerSubscriberId,
                    TilopayRecurringPlanId = ToRecurringPlanId,
                    ClienteEmail = Email,
                    Monto = 25000m,
                    Moneda = "CRC",
                    FechaCreacionUtc = createdAtUtc,
                    FechaConfirmacionUtc = confirmedAtUtc,
                    FechaActualizacionUtc = createdAtUtc,
                    ProviderResultCode = "RECURRING_PENDING",
                    ProviderResultMessage = "Signup recurrente creado y pendiente de aprobacion por webhook."
                });

                var intentId = Guid.NewGuid();
                Db.PlanChangeIntents.Add(new PlanChangeIntent
                {
                    Id = intentId,
                    TenantId = owner,
                    FromPlanId = owner == TenantId ? CurrentPlanId : null,
                    FromPlanCode = "LC_M_03",
                    FromWorkerCount = 3,
                    FromTilopayRecurringPlanId = FromRecurringPlanId,
                    FromProviderSubscriptionId = "900370",
                    ToPlanId = targetPlan.Id,
                    ToPlanCode = "LC_M_04",
                    ToWorkerCount = 4,
                    ToBillingCycle = BillingCycle.Monthly,
                    ToTilopayRecurringPlanId = ToRecurringPlanId,
                    Estado = PlanChangeIntentState.Pending,
                    OldProviderCancellation = ProviderCancellationState.NotRequired,
                    PagoSuscripcionId = paymentId,
                    CreatedAtUtc = createdAtUtc
                });

                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();

                return (intentId, paymentId);
            }

            public PlanChangeRequest BuildRequest(int toRecurringPlanId, string toPlanCode) => new()
            {
                TenantId = TenantId,
                FromPlanId = CurrentPlanId,
                FromPlanCode = "LC_M_03",
                FromWorkerCount = 3,
                FromTilopayRecurringPlanId = FromRecurringPlanId,
                FromProviderSubscriptionId = "900370",
                ToPlanId = Guid.NewGuid(),
                ToPlanCode = toPlanCode,
                ToWorkerCount = 5,
                ToBillingCycle = BillingCycle.Monthly,
                ToTilopayRecurringPlanId = toRecurringPlanId
            };

            public async Task SetIntentStateAsync(Guid intentId, PlanChangeIntentState estado)
            {
                var intent = await Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
                intent.Estado = estado;
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public async Task<PlanChangeIntent> GetIntentAsync(Guid intentId)
            {
                Db.ChangeTracker.Clear();
                return await Db.PlanChangeIntents.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == intentId);
            }

            public async Task<PagoSuscripcion> GetPaymentAsync(Guid paymentId)
            {
                Db.ChangeTracker.Clear();
                return await Db.PagosSuscripcion.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == paymentId);
            }

            public async Task<Suscripcion> GetSubscriptionAsync()
            {
                Db.ChangeTracker.Clear();
                return await Db.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.TenantId == TenantId);
            }

            public Task<int> CountAuditAsync(string action) =>
                Db.PlatformAuditLogs.CountAsync(log => log.Action == action);

            public void Dispose()
            {
                Db.Dispose();
                _connection.Dispose();
            }
        }
    }
}
