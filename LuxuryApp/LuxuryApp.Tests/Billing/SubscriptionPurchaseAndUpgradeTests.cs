using LuxuryApp.Models.Platform;
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
    /// Pruebas end-to-end del motor de compra y upgrade de la calculadora:
    /// comprar cada plan (1..11, Mensual/Anual) y, a cada uno, aumentarle un funcionario.
    /// Verifica activación, límite, vigencia, intento de cambio aplicado, alerta de
    /// cancelación de la suscripción anterior, anti doble-cambio e idempotencia.
    /// </summary>
    public class SubscriptionPurchaseAndUpgradeTests
    {
        public static IEnumerable<object[]> AllPlans() =>
            CalculatorCatalog.All.Select(plan => new object[] { plan.Workers, plan.Cycle });

        public static IEnumerable<object[]> UpgradablePlans() =>
            CalculatorCatalog.All
                .Where(plan => plan.Workers < PlanCodes.CalculatorMaxWorkers)
                .Select(plan => new object[] { plan.Workers, plan.Cycle });

        // ── Comprar cada uno de los 22 planes ──
        [Theory]
        [MemberData(nameof(AllPlans))]
        public async Task Buy_EachPlan_ActivatesWithExactLimitAmountAndCycle(int workers, BillingCycle cycle)
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var data = CalculatorCatalog.Find(workers, cycle);
            var plan = SeedTenantAndPlan(context, tenantId, data);
            await context.SaveChangesAsync();

            var service = CreatePaymentService(context, out _);

            await service.CreateRecurringCheckoutAsync(tenantId, plan.Id, "Owner", "owner@test.local");
            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            await service.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
            {
                PaymentId = pending.Id,
                ProviderTransactionId = $"TX-BUY-{data.Code}",
                ProviderSubscriberId = $"sub-{data.Code}",
                ApprovedAmount = data.Charge,
                Currency = "CRC",
                Observation = "compra"
            });

            var sub = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);
            Assert.Equal(plan.Id, sub.PlanId);
            Assert.Equal(data.RecurringPlanId, sub.TilopayRecurringPlanId);
            Assert.Equal(workers, sub.MaxFuncionarios);
            Assert.Equal(data.MonthlyEquivalent, sub.PrecioMensual);
            Assert.NotNull(sub.FechaFin);
            var expectedEnd = cycle == BillingCycle.Annual
                ? sub.FechaInicio.AddYears(1)
                : sub.FechaInicio.AddMonths(1);
            Assert.Equal(expectedEnd, sub.FechaFin);
            Assert.Equal(sub.FechaFin, sub.FechaProximoCobroUtc);
        }

        // ── A cada plan, aumentarle un funcionario (N -> N+1) ──
        [Theory]
        [MemberData(nameof(UpgradablePlans))]
        public async Task Upgrade_PlusOneFuncionario_SwitchesPlanAndFlagsOldSubscriptionForCancellation(int workers, BillingCycle cycle)
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var fromData = CalculatorCatalog.Find(workers, cycle);
            var toData = CalculatorCatalog.Find(workers + 1, cycle);

            var fromPlan = SeedTenantAndPlan(context, tenantId, fromData);
            var toPlan = SeedPlan(context, toData);
            await context.SaveChangesAsync();

            var service = CreatePaymentService(context, out var planChangeService);

            // 1) Comprar el plan N
            await service.CreateRecurringCheckoutAsync(tenantId, fromPlan.Id, "Owner", "owner@test.local");
            var firstPending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            await service.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
            {
                PaymentId = firstPending.Id,
                ProviderTransactionId = $"TX-{fromData.Code}",
                ProviderSubscriberId = $"sub-{fromData.Code}",
                ApprovedAmount = fromData.Charge,
                Currency = "CRC",
                Observation = "compra inicial"
            });

            // 2) Iniciar el cambio (lo que hace CheckoutCalculadora): registrar el intento
            var currentSub = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var startResult = await planChangeService.CreateOrReuseAsync(new PlanChangeRequest
            {
                TenantId = tenantId,
                FromPlanId = currentSub.PlanId,
                FromPlanCode = currentSub.CodigoPlan,
                FromWorkerCount = currentSub.MaxFuncionarios,
                FromTilopayRecurringPlanId = currentSub.TilopayRecurringPlanId,
                FromProviderSubscriptionId = currentSub.ProviderSubscriptionId,
                ToPlanId = toPlan.Id,
                ToPlanCode = toData.Code,
                ToWorkerCount = toData.Workers,
                ToBillingCycle = cycle,
                ToTilopayRecurringPlanId = toData.RecurringPlanId
            });
            Assert.True(startResult.Succeeded, startResult.Error);

            // 3) Checkout + aprobación del plan N+1
            await service.CreateRecurringCheckoutAsync(tenantId, toPlan.Id, "Owner", "owner@test.local");
            var upgradePending = await context.PagosSuscripcion
                .IgnoreQueryFilters()
                .Where(p => p.PlanId == toPlan.Id && p.Estado == EstadoPagoProveedor.Pendiente)
                .SingleAsync();
            await service.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
            {
                PaymentId = upgradePending.Id,
                ProviderTransactionId = $"TX-{toData.Code}",
                ProviderSubscriberId = $"sub-{toData.Code}",
                ApprovedAmount = toData.Charge,
                Currency = "CRC",
                Observation = "upgrade +1"
            });

            // Suscripción ahora en el plan nuevo con el nuevo límite
            var sub = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(toPlan.Id, sub.PlanId);
            Assert.Equal(workers + 1, sub.MaxFuncionarios);
            Assert.Equal(toData.RecurringPlanId, sub.TilopayRecurringPlanId);
            Assert.Equal($"sub-{toData.Code}", sub.ProviderSubscriptionId);

            // Intento aplicado + suscripción vieja marcada para cancelación manual
            var intent = await context.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(PlanChangeIntentState.Applied, intent.Estado);
            Assert.Equal(ProviderCancellationState.PendingManualCancellation, intent.OldProviderCancellation);
            Assert.Equal($"sub-{toData.Code}", intent.NewProviderSubscriptionId);
            Assert.NotNull(intent.AppliedAtUtc);

            // Alerta de plataforma para cancelar la suscripción anterior en TiloPay
            var alert = await context.PlatformAuditLogs
                .SingleAsync(log => log.Action == PlatformAuditActions.PlanUpgradeRequiresProviderCancellation);
            Assert.Equal(tenantId, alert.TenantId);
            Assert.Contains($"sub-{fromData.Code}", alert.Reason ?? string.Empty, StringComparison.Ordinal);
        }

        // ── Anti doble-cambio: no se permiten dos cambios abiertos a destinos distintos ──
        [Fact]
        public async Task PlanChange_SecondOpenChangeToDifferentTarget_IsRejected_SameTargetReused()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            SeedTenantOnly(context, tenantId);
            await context.SaveChangesAsync();

            var planChangeService = new PlanChangeService(context, NullLogger<PlanChangeService>.Instance);

            var first = await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 3, cycle: BillingCycle.Monthly));
            Assert.True(first.Succeeded);

            // Mismo destino: se reutiliza el intento abierto (anti doble-click).
            var sameTarget = await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 3, cycle: BillingCycle.Monthly));
            Assert.True(sameTarget.Succeeded);
            Assert.Equal(first.Intent!.Id, sameTarget.Intent!.Id);
            Assert.Single(await context.PlanChangeIntents.IgnoreQueryFilters().ToListAsync());

            // Otro destino, y el abierto tiene un pago con SEÑAL DE DINERO (transacción del
            // proveedor): puede haber un cobro real detrás, así que se rechaza. Este es el bloqueo
            // que protege de verdad. Lo que decide no es "¿hay pago?" sino "¿hay dinero?".
            var data = CalculatorCatalog.Find(3, BillingCycle.Monthly);
            var paidPlan = SeedPlan(context, data);
            var payment = new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = paidPlan.Id,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoPagoProveedor.Pendiente,
                ReferenciaInterna = $"LXA-{Guid.NewGuid():N}"[..20],
                ProviderTransactionId = "TX-EN-VUELO",
                TilopayRecurringPlanId = data.RecurringPlanId,
                ClienteEmail = "owner@test.local",
                Monto = data.Charge,
                Moneda = "CRC",
                FechaCreacionUtc = DateTime.UtcNow.AddMinutes(-5)
            };
            context.PagosSuscripcion.Add(payment);
            first.Intent!.PagoSuscripcionId = payment.Id;
            await context.SaveChangesAsync();

            var differentTargetWithMoney = await planChangeService.CreateOrReuseAsync(
                BuildChangeRequest(tenantId, toWorkers: 4, cycle: BillingCycle.Monthly));
            Assert.False(differentTargetWithMoney.Succeeded);
            Assert.Contains("cambio de plan en proceso", differentTargetWithMoney.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // Y el pago con dinero no se tocó.
            Assert.Equal(EstadoPagoProveedor.Pendiente, (await context.PagosSuscripcion.IgnoreQueryFilters()
                .SingleAsync(p => p.Id == payment.Id)).Estado);
        }

        // ── Un pending SIN pago no puede trabar al tenant para siempre ──
        // Es el estado que dejaba un checkout bloqueado: intento abierto, cero dinero detrás.
        [Fact]
        public async Task PlanChange_OpenPendingWithoutPayment_IsSupersededByNewTargetInsteadOfBlocking()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            SeedTenantOnly(context, tenantId);
            await context.SaveChangesAsync();

            var planChangeService = new PlanChangeService(context, NullLogger<PlanChangeService>.Instance);

            var blocked = await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 3, cycle: BillingCycle.Monthly));
            Assert.True(blocked.Succeeded);
            Assert.Null(blocked.Intent!.PagoSuscripcionId);

            var newTarget = await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 4, cycle: BillingCycle.Monthly));

            Assert.True(newTarget.Succeeded);
            Assert.NotEqual(blocked.Intent!.Id, newTarget.Intent!.Id);

            var superseded = await context.PlanChangeIntents.IgnoreQueryFilters()
                .SingleAsync(intent => intent.Id == blocked.Intent!.Id);
            Assert.Equal(PlanChangeIntentState.Superseded, superseded.Estado);

            // Un solo Pending vivo: el índice único por tenant sigue respetado.
            Assert.Single(await context.PlanChangeIntents.IgnoreQueryFilters()
                .Where(intent => intent.Estado == PlanChangeIntentState.Pending)
                .ToListAsync());

            Assert.Equal(1, await context.PlatformAuditLogs
                .CountAsync(log => log.Action == PlatformAuditActions.PlanChangePendingCheckoutSuperseded));
        }

        // ── Un checkout que nunca se abrió no puede dejar al tenant trabado ──
        // El intent se crea ANTES del pre-check del proveedor; si el pre-check bloquea, ese Pending
        // quedaba vivo y el índice único (un Pending por tenant) trababa el siguiente intento.
        [Fact]
        public async Task ExpirePendingAfterBlockedCheckout_PendingWithoutPayment_IsClosedAndAudited()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            SeedTenantOnly(context, tenantId);
            await context.SaveChangesAsync();

            var planChangeService = new PlanChangeService(context, NullLogger<PlanChangeService>.Instance);
            var start = await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 2, cycle: BillingCycle.Monthly));
            Assert.True(start.Succeeded);

            await planChangeService.ExpirePendingAfterBlockedCheckoutAsync(
                tenantId,
                start.Intent!.Id,
                "el proveedor bloqueó el checkout");

            var intent = await context.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(PlanChangeIntentState.Cancelled, intent.Estado);
            Assert.Equal(1, await context.PlatformAuditLogs
                .CountAsync(log => log.Action == PlatformAuditActions.PlanChangePendingIntentExpiredAfterBlockedCheckout));

            // Y el tenant puede volver a intentar el MISMO cambio sin trabas.
            var retry = await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 2, cycle: BillingCycle.Monthly));
            Assert.True(retry.Succeeded);
            Assert.NotEqual(start.Intent!.Id, retry.Intent!.Id);
        }

        [Fact]
        public async Task ExpirePendingAfterBlockedCheckout_PendingWithPayment_IsNeverTouched()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            SeedTenantOnly(context, tenantId);
            await context.SaveChangesAsync();

            var planChangeService = new PlanChangeService(context, NullLogger<PlanChangeService>.Instance);
            var start = await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 2, cycle: BillingCycle.Monthly));

            // Hay un pago enlazado: puede haber dinero real en TiloPay detrás de este intento.
            start.Intent!.PagoSuscripcionId = Guid.NewGuid();
            await context.SaveChangesAsync();

            await planChangeService.ExpirePendingAfterBlockedCheckoutAsync(tenantId, start.Intent!.Id, "bloqueo");

            var intent = await context.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(PlanChangeIntentState.Pending, intent.Estado);
            Assert.Equal(0, await context.PlatformAuditLogs
                .CountAsync(log => log.Action == PlatformAuditActions.PlanChangePendingIntentExpiredAfterBlockedCheckout));
        }

        [Fact]
        public async Task ExpirePendingAfterBlockedCheckout_AppliedIntent_IsNeverTouched()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            SeedTenantOnly(context, tenantId);
            await context.SaveChangesAsync();

            var planChangeService = new PlanChangeService(context, NullLogger<PlanChangeService>.Instance);
            var start = await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 2, cycle: BillingCycle.Monthly));

            start.Intent!.Estado = PlanChangeIntentState.Applied;
            await context.SaveChangesAsync();

            await planChangeService.ExpirePendingAfterBlockedCheckoutAsync(tenantId, start.Intent!.Id, "bloqueo");

            var intent = await context.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(PlanChangeIntentState.Applied, intent.Estado);
        }

        [Fact]
        public async Task ExpirePendingAfterBlockedCheckout_OtherTenantIntent_IsNeverTouched()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var otherTenantId = Guid.NewGuid();
            SeedTenantOnly(context, tenantId);
            SeedTenantOnly(context, otherTenantId);
            await context.SaveChangesAsync();

            var planChangeService = new PlanChangeService(context, NullLogger<PlanChangeService>.Instance);
            var start = await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 2, cycle: BillingCycle.Monthly));

            await planChangeService.ExpirePendingAfterBlockedCheckoutAsync(otherTenantId, start.Intent!.Id, "bloqueo");

            var intent = await context.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(PlanChangeIntentState.Pending, intent.Estado);
        }

        // ── Reutilizar un intento viejo no puede arrastrar un ORIGEN obsoleto ──
        // El From* decide QUÉ suscriptor se cancela después: si quedó de cuando el tenant estaba en
        // otro plan, se cancelaría el equivocado.
        [Fact]
        public async Task PlanChange_ReusingOpenIntent_RefreshesOriginFromCurrentSubscription()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            SeedTenantOnly(context, tenantId);
            await context.SaveChangesAsync();

            var planChangeService = new PlanChangeService(context, NullLogger<PlanChangeService>.Instance);

            var first = await planChangeService.CreateOrReuseAsync(
                BuildChangeRequest(tenantId, toWorkers: 3, cycle: BillingCycle.Monthly, fromSubscriber: "OLD-111"));
            Assert.True(first.Succeeded);

            // El tenant cambió de plan mientras tanto: el origen real ahora es otro suscriptor.
            var refreshed = BuildChangeRequest(tenantId, toWorkers: 3, cycle: BillingCycle.Monthly, fromSubscriber: "NEW-222") with
            {
                FromTilopayRecurringPlanId = 7103,
                FromPlanCode = "LC_M_03"
            };

            var reused = await planChangeService.CreateOrReuseAsync(refreshed);

            Assert.True(reused.Succeeded);
            Assert.Equal(first.Intent!.Id, reused.Intent!.Id);
            Assert.Equal("NEW-222", reused.Intent!.FromProviderSubscriptionId);
            Assert.Equal(7103, reused.Intent!.FromTilopayRecurringPlanId);
            Assert.Equal("LC_M_03", reused.Intent!.FromPlanCode);
        }

        // ── Pago de upgrade fallido: se mantiene el plan actual, intento sigue Pending ──
        [Fact]
        public async Task Upgrade_FailedPayment_KeepsCurrentPlanAndIntentPending()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var fromData = CalculatorCatalog.Find(2, BillingCycle.Monthly);
            var toData = CalculatorCatalog.Find(3, BillingCycle.Monthly);
            var fromPlan = SeedTenantAndPlan(context, tenantId, fromData);
            var toPlan = SeedPlan(context, toData);
            await context.SaveChangesAsync();

            var service = CreatePaymentService(context, out var planChangeService);

            await service.CreateRecurringCheckoutAsync(tenantId, fromPlan.Id, "Owner", "owner@test.local");
            var firstPending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            await service.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
            {
                PaymentId = firstPending.Id,
                ProviderTransactionId = "TX-FROM-2",
                ProviderSubscriberId = "sub-from-2",
                ApprovedAmount = fromData.Charge,
                Currency = "CRC"
            });

            await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 3, cycle: BillingCycle.Monthly, toPlanId: toPlan.Id, fromPlanId: fromPlan.Id, fromSubscriber: "sub-from-2"));

            await service.CreateRecurringCheckoutAsync(tenantId, toPlan.Id, "Owner", "owner@test.local");
            var upgradePending = await context.PagosSuscripcion
                .IgnoreQueryFilters()
                .Where(p => p.PlanId == toPlan.Id && p.Estado == EstadoPagoProveedor.Pendiente)
                .SingleAsync();

            // Monto incorrecto => la aprobación falla y NO activa
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
                {
                    PaymentId = upgradePending.Id,
                    ProviderTransactionId = "TX-TO-3-WRONG",
                    ProviderSubscriberId = "sub-to-3",
                    ApprovedAmount = 999999m,
                    Currency = "CRC"
                }));

            var sub = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(fromPlan.Id, sub.PlanId);
            Assert.Equal(2, sub.MaxFuncionarios);

            var intent = await context.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(PlanChangeIntentState.Pending, intent.Estado);
            Assert.Empty(await context.PlatformAuditLogs.ToListAsync());
        }

        // ── Aprobación duplicada del upgrade: se aplica una sola vez ──
        [Fact]
        public async Task Upgrade_DuplicateApproval_AppliesOnce()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var fromData = CalculatorCatalog.Find(5, BillingCycle.Annual);
            var toData = CalculatorCatalog.Find(6, BillingCycle.Annual);
            var fromPlan = SeedTenantAndPlan(context, tenantId, fromData);
            var toPlan = SeedPlan(context, toData);
            await context.SaveChangesAsync();

            var service = CreatePaymentService(context, out var planChangeService);

            await service.CreateRecurringCheckoutAsync(tenantId, fromPlan.Id, "Owner", "owner@test.local");
            var firstPending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            await service.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
            {
                PaymentId = firstPending.Id,
                ProviderTransactionId = "TX-FROM-5A",
                ProviderSubscriberId = "sub-from-5a",
                ApprovedAmount = fromData.Charge,
                Currency = "CRC"
            });

            await planChangeService.CreateOrReuseAsync(BuildChangeRequest(tenantId, toWorkers: 6, cycle: BillingCycle.Annual, toPlanId: toPlan.Id, fromPlanId: fromPlan.Id, fromSubscriber: "sub-from-5a"));

            await service.CreateRecurringCheckoutAsync(tenantId, toPlan.Id, "Owner", "owner@test.local");
            var upgradePending = await context.PagosSuscripcion
                .IgnoreQueryFilters()
                .Where(p => p.PlanId == toPlan.Id && p.Estado == EstadoPagoProveedor.Pendiente)
                .SingleAsync();

            await service.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
            {
                PaymentId = upgradePending.Id,
                ProviderTransactionId = "TX-TO-6A",
                ProviderSubscriberId = "sub-to-6a",
                ApprovedAmount = toData.Charge,
                Currency = "CRC"
            });

            // Segunda aprobación del MISMO pago: rechazada (ya confirmado)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
                {
                    PaymentId = upgradePending.Id,
                    ProviderTransactionId = "TX-TO-6A-DUP",
                    ProviderSubscriberId = "sub-to-6a",
                    ApprovedAmount = toData.Charge,
                    Currency = "CRC"
                }));

            Assert.Single(await context.PlanChangeIntents.IgnoreQueryFilters().ToListAsync());
            var alerts = await context.PlatformAuditLogs
                .Where(log => log.Action == PlatformAuditActions.PlanUpgradeRequiresProviderCancellation)
                .ToListAsync();
            Assert.Single(alerts);
        }

        // ── Pago en revisión manual reciente: se bloquea un nuevo checkout (anti doble cobro) ──
        [Fact]
        public async Task Checkout_WithRecentManualReviewPayment_IsBlockedAndAttemptIsPreserved()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var data = CalculatorCatalog.Find(2, BillingCycle.Monthly);
            var plan = SeedTenantAndPlan(context, tenantId, data);
            await context.SaveChangesAsync();

            var service = CreatePaymentService(context, out _);

            await service.CreateRecurringCheckoutAsync(tenantId, plan.Id, "Owner", "owner@test.local");
            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            // El webhook dejó este intento en revisión manual (posible dinero ya cobrado en TiloPay).
            pending.Estado = EstadoPagoProveedor.ManualReview;
            pending.FechaActualizacionUtc = DateTime.UtcNow;
            await context.SaveChangesAsync();

            await Assert.ThrowsAsync<RecurringCheckoutBlockedException>(() =>
                service.CreateRecurringCheckoutAsync(tenantId, plan.Id, "Owner", "owner@test.local"));

            // El intento en revisión NO se expira: la conciliación manual todavía puede aprobarlo.
            var preserved = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(EstadoPagoProveedor.ManualReview, preserved.Estado);
        }

        // ── Revisión manual vieja (>72h): el checkout vuelve a permitirse y el intento no se expira ──
        [Fact]
        public async Task Checkout_WithStaleManualReviewPayment_IsAllowedAndAttemptIsNotExpired()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var data = CalculatorCatalog.Find(2, BillingCycle.Monthly);
            var plan = SeedTenantAndPlan(context, tenantId, data);
            await context.SaveChangesAsync();

            var service = CreatePaymentService(context, out _);

            await service.CreateRecurringCheckoutAsync(tenantId, plan.Id, "Owner", "owner@test.local");
            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            pending.Estado = EstadoPagoProveedor.ManualReview;
            pending.FechaCreacionUtc = DateTime.UtcNow.AddDays(-4);
            pending.FechaActualizacionUtc = DateTime.UtcNow.AddDays(-4);
            await context.SaveChangesAsync();

            var checkout = await service.CreateRecurringCheckoutAsync(tenantId, plan.Id, "Owner", "owner@test.local");
            Assert.False(string.IsNullOrWhiteSpace(checkout.RedirectUrl));

            // El viejo ManualReview sigue abierto para conciliación; solo se crean pendientes nuevos.
            var attempts = await context.PagosSuscripcion.IgnoreQueryFilters().OrderBy(p => p.FechaCreacionUtc).ToListAsync();
            Assert.Equal(2, attempts.Count);
            Assert.Equal(EstadoPagoProveedor.ManualReview, attempts[0].Estado);
            Assert.Equal(EstadoPagoProveedor.Pendiente, attempts[1].Estado);
        }

        // ── Conciliación manual sin caducidad: un pending viejo (>72h) sigue siendo aprobable ──
        [Fact]
        public async Task ManualApproval_OfOldPending_ActivatesSubscription_WebhookApprovalStillExpires()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var data = CalculatorCatalog.Find(1, BillingCycle.Monthly);
            var plan = SeedTenantAndPlan(context, tenantId, data);
            await context.SaveChangesAsync();

            var service = CreatePaymentService(context, out _);

            await service.CreateRecurringCheckoutAsync(tenantId, plan.Id, "Owner", "owner@test.local");
            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            pending.FechaCreacionUtc = DateTime.UtcNow.AddDays(-10);
            await context.SaveChangesAsync();

            // Con origen webhook la vigencia de 72h sigue aplicando.
            var webhookAttempt = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
                {
                    PaymentId = pending.Id,
                    ProviderTransactionId = "TX-OLD-WEBHOOK",
                    ApprovedAmount = data.Charge,
                    Currency = "CRC",
                    Source = "webhook"
                }));
            Assert.Contains("vigente", webhookAttempt.Message, StringComparison.OrdinalIgnoreCase);

            // La conciliación manual del SuperAdmin no caduca: el pago real se puede activar.
            var result = await service.ApproveRecurringPaymentAsync(new RecurringPaymentApprovalRequest
            {
                PaymentId = pending.Id,
                ProviderTransactionId = "TX-OLD-MANUAL",
                ProviderSubscriberId = "sub-old",
                ApprovedAmount = data.Charge,
                Currency = "CRC",
                Source = "manual",
                Observation = "pago real cobrado por TiloPay, conciliado tarde"
            });

            Assert.Equal(EstadoPagoProveedor.Confirmado, result.PaymentStatus);
            var sub = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);
        }

        // ── Helpers ──

        private static PlanChangeRequest BuildChangeRequest(
            Guid tenantId,
            int toWorkers,
            BillingCycle cycle,
            Guid? toPlanId = null,
            Guid? fromPlanId = null,
            string? fromSubscriber = null)
        {
            var toData = CalculatorCatalog.Find(toWorkers, cycle);
            return new PlanChangeRequest
            {
                TenantId = tenantId,
                FromPlanId = fromPlanId,
                FromPlanCode = "LC_M_01",
                FromWorkerCount = 1,
                FromTilopayRecurringPlanId = 7101,
                FromProviderSubscriptionId = fromSubscriber,
                ToPlanId = toPlanId ?? Guid.NewGuid(),
                ToPlanCode = toData.Code,
                ToWorkerCount = toData.Workers,
                ToBillingCycle = cycle,
                ToTilopayRecurringPlanId = toData.RecurringPlanId
            };
        }

        private static Plan SeedTenantAndPlan(ApplicationDbContext context, Guid tenantId, CalculatorPlanData data)
        {
            SeedTenantOnly(context, tenantId);
            return SeedPlan(context, data);
        }

        private static void SeedTenantOnly(ApplicationDbContext context, Guid tenantId) =>
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Calc", Activo = true });

        private static Plan SeedPlan(ApplicationDbContext context, CalculatorPlanData data)
        {
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Codigo = data.Code,
                Nombre = $"LuxuryCloud {data.Workers} {data.Cycle}",
                PrecioMensual = data.Charge,
                MonthlyEquivalentAmount = data.MonthlyEquivalent,
                BillingCycle = data.Cycle,
                Moneda = "CRC",
                MaxFuncionarios = data.Workers,
                Activo = true
            };
            context.Planes.Add(plan);
            return plan;
        }

        private static (ApplicationDbContext Context, IDisposable Connection) CreateSystemContext()
        {
            var tenantProvider = new TestTenantProvider();
            return TestDbContextFactory.CreateSqliteContext(tenantProvider);
        }

        private static SaaSPaymentService CreatePaymentService(ApplicationDbContext context, out IPlanChangeService planChangeService)
        {
            var repeatOptions = CalculatorCatalog.BuildRepeatOptions();
            planChangeService = new PlanChangeService(context, NullLogger<PlanChangeService>.Instance);

            var cache = new MemoryCache(new MemoryCacheOptions());
            var subscriptionService = new SuscripcionService(
                context,
                cache,
                new TenantCommercialAccessCache(cache),
                new FixedBusinessDateTimeProvider(),
                Options.Create(repeatOptions),
                NullLogger<SuscripcionService>.Instance);

            return new SaaSPaymentService(
                context,
                new PaymentProviderResolver(Array.Empty<IPaymentProvider>()),
                subscriptionService,
                new TenantExecutionContextAccessor(),
                Options.Create(new OpcionesPago { ProveedorPredeterminado = PaymentProviderType.Tilopay }),
                Options.Create(new OpcionesTilopay { MerchantId = "merchant-1", WebhookAccessToken = "token-seguro" }),
                Options.Create(repeatOptions),
                NullLogger<SaaSPaymentService>.Instance,
                environment: null,
                planChangeService: planChangeService);
        }
    }

    internal sealed record CalculatorPlanData(int Workers, BillingCycle Cycle, int RecurringPlanId, decimal Charge, decimal MonthlyEquivalent)
    {
        public string Code => PlanCodes.BuildCalculatorCode(Workers, Cycle)!;
    }

    internal static class CalculatorCatalog
    {
        public static readonly IReadOnlyList<CalculatorPlanData> All = new[]
        {
            new CalculatorPlanData(1, BillingCycle.Monthly, 7101, 8000m, 8000m),
            new CalculatorPlanData(2, BillingCycle.Monthly, 7102, 15000m, 15000m),
            new CalculatorPlanData(3, BillingCycle.Monthly, 7103, 20000m, 20000m),
            new CalculatorPlanData(4, BillingCycle.Monthly, 7104, 25000m, 25000m),
            new CalculatorPlanData(5, BillingCycle.Monthly, 7105, 30000m, 30000m),
            new CalculatorPlanData(6, BillingCycle.Monthly, 7106, 35000m, 35000m),
            new CalculatorPlanData(7, BillingCycle.Monthly, 7107, 40000m, 40000m),
            new CalculatorPlanData(8, BillingCycle.Monthly, 7108, 45000m, 45000m),
            new CalculatorPlanData(9, BillingCycle.Monthly, 7109, 50000m, 50000m),
            new CalculatorPlanData(10, BillingCycle.Monthly, 7110, 55000m, 55000m),
            new CalculatorPlanData(11, BillingCycle.Monthly, 7111, 60000m, 60000m),
            new CalculatorPlanData(1, BillingCycle.Annual, 7201, 81600m, 6800m),
            new CalculatorPlanData(2, BillingCycle.Annual, 7202, 153000m, 12750m),
            new CalculatorPlanData(3, BillingCycle.Annual, 7203, 204000m, 17000m),
            new CalculatorPlanData(4, BillingCycle.Annual, 7204, 255000m, 21250m),
            new CalculatorPlanData(5, BillingCycle.Annual, 7205, 306000m, 25500m),
            new CalculatorPlanData(6, BillingCycle.Annual, 7206, 336000m, 28000m),
            new CalculatorPlanData(7, BillingCycle.Annual, 7207, 360000m, 30000m),
            new CalculatorPlanData(8, BillingCycle.Annual, 7208, 378000m, 31500m),
            new CalculatorPlanData(9, BillingCycle.Annual, 7209, 390000m, 32500m),
            new CalculatorPlanData(10, BillingCycle.Annual, 7210, 429000m, 35750m),
            new CalculatorPlanData(11, BillingCycle.Annual, 7211, 468000m, 39000m)
        };

        public static CalculatorPlanData Find(int workers, BillingCycle cycle) =>
            All.Single(plan => plan.Workers == workers && plan.Cycle == cycle);

        public static TilopayRepeatOptions BuildRepeatOptions()
        {
            var options = new TilopayRepeatOptions
            {
                Enabled = true,
                UseHostedLinks = true,
                UseRecurringCheckoutForPublicPlans = true
            };

            foreach (var plan in All)
            {
                options.Calculator.Add(new TilopayRepeatPlanOption
                {
                    Code = plan.Code,
                    TilopayPlanId = plan.RecurringPlanId,
                    MonthlyPrice = plan.Charge,
                    MonthlyEquivalentAmount = plan.MonthlyEquivalent,
                    BillingCycle = plan.Cycle,
                    Currency = "CRC",
                    MaxFuncionarios = plan.Workers,
                    CheckoutUrl = $"https://tp.cr/l/{plan.Code}",
                    UsesRecurringCheckout = true,
                    IsPublic = true
                });
            }

            return options;
        }
    }
}
