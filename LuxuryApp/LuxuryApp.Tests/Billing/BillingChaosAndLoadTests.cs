using System.Diagnostics;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Payments;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Chaos testing del pipeline recurrente: duplicados, fuera de orden, reintentos tras
    /// crash, fallos transitorios, upgrade compitiendo con renovación. El invariante en
    /// TODOS los casos: nunca doble activación, nunca doble factura, estado final coherente.
    /// </summary>
    public class BillingChaosAndLoadTests
    {
        // ── Webhook duplicado (mismo transactionId): activa UNA sola vez ──
        [Fact]
        public async Task Chaos_DuplicateSuccessWebhook_ActivatesOnceAndCreatesSingleInvoice()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1);

            var replay = await fixture.SendSignupSuccessWebhookAsync(); // replay exacto del mismo evento

            Assert.True(replay.IsDuplicate);

            var suscripcion = await fixture.Context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Equal(fixture.ExpectedFirstPeriodEndUtc, suscripcion.FechaFin);

            Assert.Equal(1, await fixture.Context.Facturas.IgnoreQueryFilters().CountAsync());
            Assert.Equal(1, await fixture.Context.EventosPago.IgnoreQueryFilters().CountAsync());
        }

        // ── Idempotencia: replay de un pago APROBADO clasificado como fallo/genérico ──
        // Reproduce el bug de prod: un reenvío del paymentId ya aprobado degradaba el pago a
        // Fallido y ponía la suscripción en Morosa. Debe tratarse como duplicado sin degradar.
        [Fact]
        public async Task Chaos_ReplayOfApprovedPaymentAsNotification_DoesNotDegradePaymentOrSubscription()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1);

            var approved = await fixture.Context.PagosSuscripcion.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(p => p.Estado == EstadoPagoProveedor.Confirmado);
            var approvedTxn = approved.ProviderTransactionId!;

            // Replay del MISMO txn aprobado, sin marcadores de éxito (como lo envía el proveedor).
            var replay = await fixture.SendReplayNotificationWebhookAsync(approvedTxn);

            Assert.True(replay.IsDuplicate);
            Assert.True(replay.IsProcessed);

            // El pago aprobado NO se degrada.
            var payment = await fixture.Context.PagosSuscripcion.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(p => p.Id == approved.Id);
            Assert.Equal(EstadoPagoProveedor.Confirmado, payment.Estado);

            // La suscripción NO cae en morosa.
            var suscripcion = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Null(suscripcion.FechaFinGraciaUtc);

            // No se creó un segundo pago.
            Assert.Equal(1, await fixture.Context.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
        }

        [Fact]
        public async Task Chaos_ReplayOfApprovedPayment_MarksEventAsDuplicado()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1);
            var approvedTxn = (await fixture.Context.PagosSuscripcion.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(p => p.Estado == EstadoPagoProveedor.Confirmado)).ProviderTransactionId!;

            await fixture.SendReplayNotificationWebhookAsync(approvedTxn, incomingEvent: "repeat_payment_failed");

            // El evento del replay queda marcado como Duplicado (terminal), no como Procesado normal.
            var replayEvent = await fixture.Context.EventosPago.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.EstadoProcesamiento == "Duplicado")
                .SingleAsync();
            Assert.True(replayEvent.Procesado);

            // Sigue habiendo un solo pago confirmado.
            Assert.Equal(1, await fixture.Context.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p => p.Estado == EstadoPagoProveedor.Confirmado));
        }

        // ── Regresión: replay de un pago aprobado (plan LC_M_03, monto 20000) ──
        [Fact]
        public async Task Chaos_ApprovedReplay_WithoutSuccessMarkers_DoesNotDegrade()
        {
            // workers:3 = LC_M_03 = TilopayRecurringPlanId 7103, cargo 20000.
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 3, signupTransactionId: "9000001");

            var beforePayment = await fixture.Context.PagosSuscripcion.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(p => p.Estado == EstadoPagoProveedor.Confirmado);
            Assert.Equal("9000001", beforePayment.ProviderTransactionId);

            var replay = await fixture.SendReplayNotificationWebhookAsync("9000001");

            Assert.True(replay.IsDuplicate);
            var payment = await fixture.Context.PagosSuscripcion.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(p => p.Id == beforePayment.Id);
            Assert.Equal(EstadoPagoProveedor.Confirmado, payment.Estado);
            Assert.NotEqual("REPEAT_PAYMENT_FAILED", payment.ProviderResultCode);
            var suscripcion = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
        }

        // ── Requisito 5: un webhook nuevo con paymentId inexistente NO afecta pagos aprobados ──
        [Fact]
        public async Task Chaos_NewFailedWebhookUnknownTxn_DoesNotAffectApprovedPayment()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1);
            var approvedId = (await fixture.Context.PagosSuscripcion.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(p => p.Estado == EstadoPagoProveedor.Confirmado)).Id;

            // Fallo real de OTRA transacción (renovación fallida): no debe tocar el pago aprobado.
            await fixture.SendRenewalFailedWebhookAsync("TX-UNKNOWN-NEW-FAIL");

            var approved = await fixture.Context.PagosSuscripcion.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(p => p.Id == approvedId);
            Assert.Equal(EstadoPagoProveedor.Confirmado, approved.Estado); // el aprobado sigue intacto
        }

        // ── Fuera de orden: fallo de renovación y luego cobro exitoso ──
        [Fact]
        public async Task Chaos_FailureThenSuccess_RecoversToActiveWithTwoInvoices()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1);

            await fixture.SendRenewalFailedWebhookAsync("TX-CHAOS-FAIL-1");
            var afterFailure = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Morosa, afterFailure.Estado);
            Assert.NotNull(afterFailure.FechaFinGraciaUtc);

            var recovery = await fixture.SendRenewalSuccessWebhookAsync("TX-CHAOS-RETRY-1");
            Assert.True(recovery.IsProcessed);

            var suscripcion = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Null(suscripcion.FechaFinGraciaUtc);

            // Signup + renovación exitosa = 2 facturas pagadas (el fallo genera factura "Fallido", no pagada).
            Assert.Equal(2, await fixture.Context.Facturas.IgnoreQueryFilters().CountAsync(f => f.Estado == "Pagado"));
        }

        // ── Recuperación de pago por webhook real: duplicado y reintentos del MISMO ciclo ──
        [Fact]
        public async Task Chaos_FailedRenewal_DuplicateAndRetries_OneIncident_OriginalGrace_StaysMorosa()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1, withPaymentRecovery: true);

            // 1er rechazo: abre incidente + gracia.
            var first = await fixture.SendRenewalFailedWebhookAsync("TX-RETRY-FAIL-1");
            Assert.True(first.IsProcessed);
            var incident = await fixture.Context.SubscriptionPaymentIncidents.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            var originalGraceEnds = incident.GraceEndsAtUtc;
            Assert.NotNull(originalGraceEnds);
            var afterFirst = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Morosa, afterFirst.Estado);
            Assert.Equal("GraceActive", afterFirst.PaymentRecoveryStatus);
            Assert.Equal(originalGraceEnds, afterFirst.FechaFinGraciaUtc);
            var paymentsAfterFirst = await fixture.Context.PagosSuscripcion.IgnoreQueryFilters().CountAsync();

            // Webhook DUPLICADO (mismo txn): no duplica pago, ni incidente, ni extiende la gracia.
            await fixture.SendRenewalFailedWebhookAsync("TX-RETRY-FAIL-1");
            Assert.Equal(paymentsAfterFirst, await fixture.Context.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
            var afterDuplicate = await fixture.Context.SubscriptionPaymentIncidents.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(1, afterDuplicate.FailureCount);
            Assert.Equal(originalGraceEnds, afterDuplicate.GraceEndsAtUtc);

            // REINTENTO real de TiloPay (otro txn) del mismo ciclo: suma el conteo y NADA más.
            await fixture.SendRenewalFailedWebhookAsync("TX-RETRY-FAIL-2");
            var afterRetry = await fixture.Context.SubscriptionPaymentIncidents.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(2, afterRetry.FailureCount);
            Assert.Equal(originalGraceEnds, afterRetry.GraceEndsAtUtc);
            Assert.Equal(incident.FailureDetectedAtUtc, afterRetry.FailureDetectedAtUtc);
            Assert.Equal(PaymentIncidentStatus.Open, afterRetry.Status);

            var subscription = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            // Antes del fix: Morosa caía a Fallida (acceso cortado en plena gracia) y la gracia se recalculaba.
            Assert.Equal(EstadoSuscripcion.Morosa, subscription.Estado);
            Assert.Equal("GraceActive", subscription.PaymentRecoveryStatus);
            Assert.Equal(originalGraceEnds, subscription.FechaFinGraciaUtc);
        }

        [Fact]
        public async Task Chaos_HealthyInitialFlow_WithPaymentRecoveryEnabled_StaysActive_NoIncident()
        {
            // Regresión del flujo inicial (checkout + repeat_payment_success) con la recuperación de
            // pago encendida como en producción.
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1, withPaymentRecovery: true);

            var subscription = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, subscription.Estado);
            Assert.Null(subscription.PaymentRecoveryStatus);
            Assert.Null(subscription.FechaFinGraciaUtc);
            Assert.Equal(0, await fixture.Context.SubscriptionPaymentIncidents.IgnoreQueryFilters().CountAsync());
            Assert.Equal(1, await fixture.Context.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p => p.Estado == EstadoPagoProveedor.Confirmado));
        }

        [Fact]
        public async Task Chaos_FailureThenSuccess_WithPaymentRecovery_ResolvesIncidentOnce()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1, withPaymentRecovery: true);

            await fixture.SendRenewalFailedWebhookAsync("TX-REC-FAIL-1");
            var success = await fixture.SendRenewalSuccessWebhookAsync("TX-REC-OK-1");
            Assert.True(success.IsProcessed);
            var endAfterSuccess = (await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync()).FechaFin;

            // Replay exacto del success: sin doble pago, sin doble extensión, incidente resuelto una vez.
            await fixture.SendRenewalSuccessWebhookAsync("TX-REC-OK-1");

            var subscription = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, subscription.Estado);
            Assert.Null(subscription.PaymentRecoveryStatus);
            Assert.Null(subscription.FechaFinGraciaUtc);
            Assert.Equal(endAfterSuccess, subscription.FechaFin);
            Assert.Equal(1, await fixture.Context.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p => p.ProviderTransactionId == "TX-REC-OK-1"));

            var incident = await fixture.Context.SubscriptionPaymentIncidents.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(PaymentIncidentStatus.Resolved, incident.Status);
            Assert.Equal(1, await fixture.Context.PlatformAuditLogs.CountAsync(l =>
                l.Action == LuxuryApp.Models.Platform.PlatformAuditActions.SubscriptionPaymentRecoveryResolved));
        }

        // ── Reinicio a mitad de procesamiento: el retry converge sin doble extensión ──
        [Fact]
        public async Task Chaos_RetryAfterSimulatedCrash_DoesNotExtendTwice()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1);

            var periodEndAfterFirstRun = (await fixture.Context.Suscripciones
                .IgnoreQueryFilters().AsNoTracking().SingleAsync()).FechaFin;

            // Simular crash tras confirmar el pago pero antes de marcar el evento terminal:
            // TiloPay reintentaría el MISMO webhook contra un evento no terminal.
            var evento = await fixture.Context.EventosPago.IgnoreQueryFilters().SingleAsync();
            evento.Procesado = false;
            evento.EstadoProcesamiento = "Recibido";
            evento.FechaProcesamientoUtc = null;
            await fixture.Context.SaveChangesAsync();

            var retry = await fixture.SendSignupSuccessWebhookAsync();

            Assert.True(retry.IsDuplicate || retry.IsProcessed);

            var suscripcion = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Equal(periodEndAfterFirstRun, suscripcion.FechaFin); // sin doble extensión
            Assert.Equal(1, await fixture.Context.Facturas.IgnoreQueryFilters().CountAsync());
        }

        // ── Fallo transitorio (excepción inesperada) y reintento ──
        [Fact]
        public async Task Chaos_TransientFailureOnFirstAttempt_RetrySucceedsOnce()
        {
            using var fixture = await ChaosFixture.CreateAsync(workers: 1);
            await fixture.StartCheckoutAsync();

            fixture.Provider.FailNextParse = true; // red/parseo muere en el primer intento

            await Assert.ThrowsAnyAsync<Exception>(() => fixture.SendSignupSuccessWebhookAsync());

            var retry = await fixture.SendSignupSuccessWebhookAsync();
            Assert.True(retry.IsProcessed);

            var suscripcion = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Equal(1, await fixture.Context.Facturas.IgnoreQueryFilters().CountAsync());
        }

        // ── Cancelación y luego replay tardío del alta: no resucita la suscripción ──
        [Fact]
        public async Task Chaos_LateDuplicateAfterCancellation_DoesNotResurrectSubscription()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1);

            await fixture.SendCancellationWebhookAsync();
            var cancelled = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Cancelada, cancelled.Estado);

            var lateReplay = await fixture.SendSignupSuccessWebhookAsync(); // webhook retrasado horas

            Assert.True(lateReplay.IsDuplicate);
            var final = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Cancelada, final.Estado);
            Assert.Equal(1, await fixture.Context.Facturas.IgnoreQueryFilters().CountAsync());
        }

        // ── Upgrade compitiendo con una renovación del plan anterior ──
        [Fact]
        public async Task Chaos_RenewalArrivesDuringUpgrade_FinalStateIsUpgradedPlanWithoutDoubleActivation()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 2);

            // 1) Se inicia el cambio 2 -> 3 funcionarios.
            var toData = CalculatorCatalog.Find(3, BillingCycle.Monthly);
            var toPlan = fixture.SeedPlan(toData);
            await fixture.Context.SaveChangesAsync();
            var start = await fixture.PlanChangeService.CreateOrReuseAsync(fixture.BuildChangeRequest(toPlan.Id, toData));
            Assert.True(start.Succeeded);

            // 2) MIENTRAS tanto llega la renovación del plan viejo.
            var renewal = await fixture.SendRenewalSuccessWebhookAsync("TX-RENEW-DURING-UPGRADE");
            Assert.True(renewal.IsProcessed);

            var intentAfterRenewal = await fixture.Context.PlanChangeIntents.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(PlanChangeIntentState.Pending, intentAfterRenewal.Estado); // la renovación NO aplica el cambio

            // 3) Se completa el pago del plan nuevo.
            await fixture.PaymentService.CreateRecurringCheckoutAsync(fixture.TenantId, toPlan.Id, "Owner", fixture.Email);
            var upgradePending = await fixture.Context.PagosSuscripcion
                .IgnoreQueryFilters()
                .SingleAsync(p => p.PlanId == toPlan.Id && p.Estado == EstadoPagoProveedor.Pendiente);

            await fixture.PaymentService.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
            {
                PaymentId = upgradePending.Id,
                ProviderTransactionId = "TX-UPGRADE-3",
                ProviderSubscriberId = "sub-upgrade-3",
                ApprovedAmount = toData.Charge,
                Currency = "CRC"
            });

            var suscripcion = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(toPlan.Id, suscripcion.PlanId);
            Assert.Equal(3, suscripcion.MaxFuncionarios);

            var intent = await fixture.Context.PlanChangeIntents.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(PlanChangeIntentState.Applied, intent.Estado);
            Assert.Equal(ProviderCancellationState.PendingManualCancellation, intent.OldProviderCancellation);

            // Alta + renovación + upgrade = 3 facturas exactas, una por cobro real.
            Assert.Equal(3, await fixture.Context.Facturas.IgnoreQueryFilters().CountAsync());
        }

        // ── Carga: decenas de tenants, cientos de eventos consecutivos con duplicados ──
        [Fact]
        public async Task Load_SixtyTenantSignupsWithDuplicates_AllConsistentNoDuplicateRecords()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            const int tenantCount = 60;
            var repeatOptions = CalculatorCatalog.BuildRepeatOptions();
            var provider = new ChaosTilopayProvider();
            var paymentService = CreatePaymentService(context, repeatOptions, provider, out _);
            var data = CalculatorCatalog.Find(1, BillingCycle.Monthly);

            var stopwatch = Stopwatch.StartNew();
            var references = new List<(Guid TenantId, string Reference, string Email, Guid PlanId)>();

            for (var i = 0; i < tenantCount; i++)
            {
                var tenantId = Guid.NewGuid();
                var email = $"owner{i}@load.local";
                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = $"Tenant {i}", Activo = true });
                var plan = new Plan
                {
                    Id = Guid.NewGuid(),
                    Codigo = data.Code,
                    Nombre = $"Plan carga {i}",
                    PrecioMensual = data.Charge,
                    MonthlyEquivalentAmount = data.MonthlyEquivalent,
                    BillingCycle = data.Cycle,
                    Moneda = "CRC",
                    MaxFuncionarios = data.Workers,
                    Activo = true
                };

                // El catálogo exige un único Plan por Código: reutilizar la fila si ya existe.
                var existingPlan = await context.Planes.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.Codigo == data.Code);
                if (existingPlan is null)
                {
                    context.Planes.Add(plan);
                }
                else
                {
                    plan = existingPlan;
                }

                await context.SaveChangesAsync();

                var checkout = await paymentService.CreateRecurringCheckoutAsync(tenantId, plan.Id, $"Owner {i}", email);
                references.Add((tenantId, checkout.ProviderReference!, email, plan.Id));
            }

            // Ola de webhooks: cada evento llega DOS veces (original + reintento del proveedor).
            for (var i = 0; i < references.Count; i++)
            {
                var (tenantId, reference, email, _) = references[i];
                provider.WebhookData = BuildSuccessWebhook(reference, email, $"TX-LOAD-{i:D4}", $"sub-load-{i:D4}", data);

                var first = await paymentService.ProcessTilopayWebhookAsync("{\"chaos\":true}", $"corr-load-{i}", "repeat_payment_success");
                var duplicate = await paymentService.ProcessTilopayWebhookAsync("{\"chaos\":true}", $"corr-load-{i}-dup", "repeat_payment_success");

                Assert.True(first.IsProcessed, $"Evento {i} no procesado");
                Assert.True(duplicate.IsDuplicate, $"Duplicado {i} no detectado");
            }

            stopwatch.Stop();

            var subscriptions = await context.Suscripciones.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            Assert.Equal(tenantCount, subscriptions.Count);
            Assert.All(subscriptions, s => Assert.Equal(EstadoSuscripcion.Activa, s.Estado));

            Assert.Equal(tenantCount, await context.Facturas.IgnoreQueryFilters().CountAsync());
            Assert.Equal(tenantCount, await context.EventosPago.IgnoreQueryFilters().CountAsync());
            Assert.Equal(tenantCount, await context.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p => p.Estado == EstadoPagoProveedor.Confirmado));

            // Sin registros duplicados por reintentos.
            var duplicateTransactions = await context.PagosSuscripcion.IgnoreQueryFilters()
                .Where(p => p.ProviderTransactionId != null)
                .GroupBy(p => p.ProviderTransactionId)
                .Where(g => g.Count() > 1)
                .CountAsync();
            Assert.Equal(0, duplicateTransactions);

            // 120 webhooks + 60 checkouts en SQLite: presupuesto amplio para CI lentos.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(120),
                $"Procesamiento demasiado lento: {stopwatch.Elapsed}");
        }

        // ── Carga: dos años de renovaciones consecutivas de un mismo tenant ──
        [Fact]
        public async Task Load_TwoYearsOfMonthlyRenewals_PeriodsAccumulateExactly()
        {
            using var fixture = await ChaosFixture.CreateWithSignupAsync(workers: 1);
            const int renewals = 24;

            for (var month = 1; month <= renewals; month++)
            {
                var result = await fixture.SendRenewalSuccessWebhookAsync($"TX-RENEW-{month:D3}");
                Assert.True(result.IsProcessed, $"Renovación {month} falló");
            }

            var suscripcion = await fixture.Context.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);

            // Alta (1 mes) + 24 renovaciones = fin exactamente 25 meses después del inicio fijo.
            Assert.Equal(fixture.FirstPeriodStartUtc.AddMonths(renewals + 1), suscripcion.FechaFin);
            Assert.Equal(renewals + 1, await fixture.Context.Facturas.IgnoreQueryFilters().CountAsync());
            Assert.Equal(renewals + 1, await fixture.Context.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p => p.Estado == EstadoPagoProveedor.Confirmado));
        }

        // ── Infraestructura ──

        private static PaymentProviderWebhookData BuildSuccessWebhook(
            string reference,
            string email,
            string transactionId,
            string subscriberId,
            CalculatorPlanData data) =>
            new()
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = reference,
                RecurringPlanId = data.RecurringPlanId,
                CustomerEmail = email,
                Amount = data.Charge,
                Currency = "CRC",
                ProviderTransactionId = transactionId,
                ProviderSubscriberId = subscriberId,
                IsRecurring = true
            };

        private static (ApplicationDbContext Context, IDisposable Connection) CreateSystemContext()
        {
            var tenantProvider = new TestTenantProvider();
            return TestDbContextFactory.CreateSqliteContext(tenantProvider);
        }

        private static SaaSPaymentService CreatePaymentService(
            ApplicationDbContext context,
            TilopayRepeatOptions repeatOptions,
            IPaymentProvider provider,
            out IPlanChangeService planChangeService,
            bool withPaymentRecovery = false)
        {
            planChangeService = new PlanChangeService(context, NullLogger<PlanChangeService>.Instance);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            var clock = new FixedBusinessDateTimeProvider();
            var recoveryOptions = Options.Create(new LuxuryApp.Services.Billing.BillingPaymentRecoveryOptions
            {
                Enabled = withPaymentRecovery,
                GraceDays = 5,
                AutoSuspendAfterGrace = false
            });
            var subscriptionService = new SuscripcionService(
                context,
                cache,
                accessCache,
                clock,
                Options.Create(repeatOptions),
                NullLogger<SuscripcionService>.Instance,
                recoveryOptions);

            // Recuperación de pago real (incidente + gracia) enganchada al webhook, solo si se pide.
            var paymentRecovery = withPaymentRecovery
                ? new LuxuryApp.Services.Billing.PaymentRecoveryService(
                    context,
                    new TenantExecutionContextAccessor(),
                    clock,
                    recoveryOptions,
                    NullLogger<LuxuryApp.Services.Billing.PaymentRecoveryService>.Instance,
                    new FakeTenantOwnerResolver(),
                    accessCache)
                : null;

            return new SaaSPaymentService(
                context,
                new PaymentProviderResolver(new[] { provider }),
                subscriptionService,
                new TenantExecutionContextAccessor(),
                Options.Create(new OpcionesPago { ProveedorPredeterminado = PaymentProviderType.Tilopay }),
                Options.Create(new OpcionesTilopay { MerchantId = "merchant-1", WebhookAccessToken = "token-seguro" }),
                Options.Create(repeatOptions),
                NullLogger<SaaSPaymentService>.Instance,
                environment: null,
                planChangeService: planChangeService,
                paymentRecovery: paymentRecovery);
        }

        private sealed class ChaosTilopayProvider : IPaymentProvider
        {
            public PaymentProviderType ProviderType => PaymentProviderType.Tilopay;

            public PaymentProviderWebhookData WebhookData { get; set; } = new()
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                IsRecurring = true
            };

            /// <summary>Simula un fallo transitorio (red/parseo) SOLO en la siguiente llamada.</summary>
            public bool FailNextParse { get; set; }

            public Task<PaymentCheckoutResult> CreateCheckoutAsync(
                PaymentCheckoutRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PaymentCheckoutResult
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    RedirectUrl = request.SuccessUrl
                });

            public PaymentProviderWebhookData ParseWebhook(string payload)
            {
                if (FailNextParse)
                {
                    FailNextParse = false;
                    throw new HttpRequestException("Fallo transitorio simulado de red durante el webhook.");
                }

                return WebhookData;
            }

            public Task<PaymentVerificationResult> VerifyPaymentAsync(
                PaymentVerificationRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PaymentVerificationResult
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    Exists = true,
                    IsSuccess = true,
                    Reference = request.Reference
                });
        }

        /// <summary>
        /// Escenario base reutilizable: tenant + plan calculadora + checkout + (opcional) alta
        /// aprobada por webhook. Expone helpers para inyectar webhooks de renovación, fallo,
        /// cancelación y replays exactos.
        /// </summary>
        private sealed class ChaosFixture : IDisposable
        {
            private static readonly DateTime FixedNowUtc =
                new DateTimeOffset(new DateTime(2026, 5, 26, 10, 30, 0), TimeSpan.FromHours(-6)).UtcDateTime;

            private readonly IDisposable _connection;
            private CalculatorPlanData _data = null!;
            private string _signupTransactionId = null!;
            private string _subscriberId = null!;
            private string _checkoutReference = null!;

            public ApplicationDbContext Context { get; private set; } = null!;
            public ChaosTilopayProvider Provider { get; } = new();
            public SaaSPaymentService PaymentService { get; private set; } = null!;
            public IPlanChangeService PlanChangeService { get; private set; } = null!;
            public Guid TenantId { get; } = Guid.NewGuid();
            public Guid PlanId { get; private set; }
            public string Email => "owner@chaos.local";

            public DateTime FirstPeriodStartUtc => FixedNowUtc;
            public DateTime ExpectedFirstPeriodEndUtc => FixedNowUtc.AddMonths(1);

            private ChaosFixture(IDisposable connection)
            {
                _connection = connection;
            }

            public static async Task<ChaosFixture> CreateAsync(int workers, string? signupTransactionId = null, bool withPaymentRecovery = false)
            {
                var tenantProvider = new TestTenantProvider();
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
                var fixture = new ChaosFixture(connection) { Context = context };

                fixture._data = CalculatorCatalog.Find(workers, BillingCycle.Monthly);
                var repeatOptions = CalculatorCatalog.BuildRepeatOptions();
                fixture.PaymentService = CreatePaymentService(context, repeatOptions, fixture.Provider, out var planChangeService, withPaymentRecovery);
                fixture.PlanChangeService = planChangeService;

                context.Tenants.Add(new Tenant { Id = fixture.TenantId, Nombre = "Tenant Chaos", Activo = true });
                var plan = fixture.SeedPlan(fixture._data);
                fixture.PlanId = plan.Id;
                await context.SaveChangesAsync();

                fixture._signupTransactionId = signupTransactionId ?? $"TX-SIGNUP-{Guid.NewGuid():N}"[..20];
                fixture._subscriberId = $"sub-chaos-{Guid.NewGuid():N}"[..20];
                return fixture;
            }

            public static async Task<ChaosFixture> CreateWithSignupAsync(int workers, string? signupTransactionId = null, bool withPaymentRecovery = false)
            {
                var fixture = await CreateAsync(workers, signupTransactionId, withPaymentRecovery);
                await fixture.StartCheckoutAsync();
                var signup = await fixture.SendSignupSuccessWebhookAsync();
                Assert.True(signup.IsProcessed, "El alta inicial del fixture no se procesó.");
                return fixture;
            }

            /// <summary>Txn del alta (el ProviderTransactionId del pago aprobado inicial).</summary>
            public string SignupTransactionId => _signupTransactionId;

            /// <summary>
            /// Replay de un webhook recurrente SIN marcadores de éxito (ni monto ni code=1): reproduce
            /// el caso de producción donde un reenvío llegó como "tilopay_repeat_notification" y caía en
            /// la rama de fallo. <paramref name="incomingEvent"/> null = tipo genérico de notificación.
            /// </summary>
            public Task<PaymentWebhookProcessingResult> SendReplayNotificationWebhookAsync(
                string transactionId, string? incomingEvent = null)
            {
                Provider.WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventType = "tilopay.repeat.notification",
                    Reference = string.Empty,
                    RecurringPlanId = _data.RecurringPlanId,
                    CustomerEmail = Email,
                    ProviderTransactionId = transactionId,
                    ProviderSubscriberId = _subscriberId,
                    IsRecurring = true
                };

                return PaymentService.ProcessTilopayWebhookAsync(
                    "{\"replay\":true}", $"corr-replay-{transactionId}", incomingEvent);
            }

            public Plan SeedPlan(CalculatorPlanData data)
            {
                var plan = new Plan
                {
                    Id = Guid.NewGuid(),
                    Codigo = data.Code,
                    Nombre = $"LuxuryCloud chaos {data.Workers}",
                    PrecioMensual = data.Charge,
                    MonthlyEquivalentAmount = data.MonthlyEquivalent,
                    BillingCycle = data.Cycle,
                    Moneda = "CRC",
                    MaxFuncionarios = data.Workers,
                    Activo = true
                };
                Context.Planes.Add(plan);
                return plan;
            }

            public async Task StartCheckoutAsync()
            {
                var checkout = await PaymentService.CreateRecurringCheckoutAsync(TenantId, PlanId, "Owner", Email);
                _checkoutReference = checkout.ProviderReference!;
            }

            public Task<PaymentWebhookProcessingResult> SendSignupSuccessWebhookAsync()
            {
                Provider.WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventType = "tilopay.repeat.notification",
                    Reference = _checkoutReference,
                    RecurringPlanId = _data.RecurringPlanId,
                    CustomerEmail = Email,
                    Amount = _data.Charge,
                    Currency = "CRC",
                    ProviderTransactionId = _signupTransactionId,
                    ProviderSubscriberId = _subscriberId,
                    IsRecurring = true
                };

                return PaymentService.ProcessTilopayWebhookAsync("{\"chaos\":true}", "corr-signup", "repeat_payment_success");
            }

            public Task<PaymentWebhookProcessingResult> SendRenewalSuccessWebhookAsync(string transactionId)
            {
                Provider.WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventType = "tilopay.repeat.notification",
                    Reference = string.Empty,
                    RecurringPlanId = _data.RecurringPlanId,
                    CustomerEmail = Email,
                    Amount = _data.Charge,
                    Currency = "CRC",
                    ProviderTransactionId = transactionId,
                    ProviderSubscriberId = _subscriberId,
                    IsRecurring = true
                };

                return PaymentService.ProcessTilopayWebhookAsync("{\"chaos\":true}", $"corr-{transactionId}", "repeat_payment_success");
            }

            public Task<PaymentWebhookProcessingResult> SendRenewalFailedWebhookAsync(string transactionId)
            {
                Provider.WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventType = "tilopay.repeat.notification",
                    Reference = string.Empty,
                    RecurringPlanId = _data.RecurringPlanId,
                    CustomerEmail = Email,
                    ProviderTransactionId = transactionId,
                    ProviderSubscriberId = _subscriberId,
                    IsRecurring = true
                };

                return PaymentService.ProcessTilopayWebhookAsync("{\"chaos\":true}", $"corr-{transactionId}", "repeat_payment_failed");
            }

            public Task<PaymentWebhookProcessingResult> SendCancellationWebhookAsync()
            {
                Provider.WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventType = "tilopay.repeat.notification",
                    Reference = string.Empty,
                    RecurringPlanId = _data.RecurringPlanId,
                    CustomerEmail = Email,
                    ProviderSubscriberId = _subscriberId,
                    IsRecurring = true
                };

                return PaymentService.ProcessTilopayWebhookAsync("{\"chaos\":true}", "corr-cancel", "repeat_subscription_cancelled");
            }

            public PlanChangeRequest BuildChangeRequest(Guid toPlanId, CalculatorPlanData toData) =>
                new()
                {
                    TenantId = TenantId,
                    FromPlanId = PlanId,
                    FromPlanCode = _data.Code,
                    FromWorkerCount = _data.Workers,
                    FromTilopayRecurringPlanId = _data.RecurringPlanId,
                    FromProviderSubscriptionId = _subscriberId,
                    ToPlanId = toPlanId,
                    ToPlanCode = toData.Code,
                    ToWorkerCount = toData.Workers,
                    ToBillingCycle = toData.Cycle,
                    ToTilopayRecurringPlanId = toData.RecurringPlanId
                };

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }
    }
}
