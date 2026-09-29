using LuxuryApp.Controllers.Platform;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Payments;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Integración de la resolución de id_suscriptor y la gestión del suscriptor del proveedor,
    /// usando un cliente admin fake. Cubre webhook → resolución, fallback a alerta, reconciliación,
    /// blindaje de checkout, upgrade con cancelación del suscriptor viejo, autorización y masking.
    /// </summary>
    public class SubscriberResolutionIntegrationTests
    {
        private const string Email = "buyer1@example.com";

        // ── 6. Webhook repeat_registration resuelve y guarda el subscriber id ──
        [Fact]
        public async Task Webhook_Registration_ResolvesAndPersistsSubscriberId()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound(); // durante checkout no existe aún
            await h.StartCheckoutAsync();

            h.Admin.ResolutionResult = SubscriberResolutionResult.Found(
                new TilopaySubscriber { SubscriberId = "900830", Email = Email }, 1);

            await h.ProcessWebhookAsync("repeat_registration", amount: null, transactionId: "TX-REG-1");

            var payment = await h.Db.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal("900830", subscription.ProviderSubscriptionId);
            // El pago sigue Pendiente en el registro; el subscriber ya quedó en la suscripción.
            Assert.Equal("900830", payment.ProviderSubscriberId);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.ProviderSubscriberResolved));
        }

        // ── 7. Webhook repeat_payment_success activa y guarda subscriber id ──
        [Fact]
        public async Task Webhook_PaymentSuccess_ActivatesAndPersistsSubscriberId()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound();
            await h.StartCheckoutAsync();

            h.Admin.ResolutionResult = SubscriberResolutionResult.Found(
                new TilopaySubscriber { SubscriberId = "900830", Email = Email }, 1);

            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "TX-OK-1");

            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, subscription.Estado);
            Assert.Equal("900830", subscription.ProviderSubscriptionId);

            var payment = await h.Db.PagosSuscripcion.IgnoreQueryFilters()
                .SingleAsync(p => p.Estado == EstadoPagoProveedor.Confirmado);
            Assert.Equal("900830", payment.ProviderSubscriberId);
        }

        // ── 8. Si el API falla, la compra sigue activa pero se crea alerta ──
        [Fact]
        public async Task Webhook_PaymentSuccess_ApiFailure_KeepsActivationAndAlerts()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound();
            await h.StartCheckoutAsync();

            h.Admin.ResolutionResult = SubscriberResolutionResult.Failed("timeout simulado");

            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "TX-FAIL-1");

            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, subscription.Estado);          // activación intacta
            Assert.Null(subscription.ProviderSubscriptionId);                     // subscriber no resuelto
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.ProviderSubscriberResolutionFailed));
        }

        // ── 9. Reconciliación posterior repara ProviderSubscriptionId faltante ──
        [Fact]
        public async Task Reconciliation_RepairsMissingSubscriberId()
        {
            using var h = await Harness.CreateAsync(workers: 1);

            // Suscripción activa + pago confirmado, ambos SIN subscriber id (estado del bug real).
            h.Db.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                FechaInicio = DateTime.UtcNow.AddDays(-1),
                FechaFin = DateTime.UtcNow.AddMonths(1)
            });
            h.Db.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoPagoProveedor.Confirmado,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ReferenciaInterna = "LXA-RECON-1",
                ClienteEmail = Email,
                Monto = h.Data.Charge,
                Moneda = "CRC",
                FechaCreacionUtc = DateTime.UtcNow.AddHours(-2),
                FechaConfirmacionUtc = DateTime.UtcNow.AddHours(-2)
            });
            await h.Db.SaveChangesAsync();

            h.Admin.ResolutionResult = SubscriberResolutionResult.Found(
                new TilopaySubscriber { SubscriberId = "900830", Email = Email }, 1);

            var report = await h.Reconciliation.RunAsync();

            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal("900830", subscription.ProviderSubscriptionId);
            Assert.True(report.SubscriberIdsResolved >= 1);
        }

        // ── 10 (contrato del proveedor). Servicio admin REAL + respuesta con el formato de TiloPay → persiste 900830 ──
        [Fact]
        public async Task EndToEnd_RealTilopayResponse_PersistsSubscriberIdInPaymentAndSubscription()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Codigo = "LC_M_01",
                Nombre = "LuxuryCloud 1 Mensual",
                PrecioMensual = 8000m,
                BillingCycle = BillingCycle.Monthly,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true
            };
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Real", Activo = true });
            context.Planes.Add(plan);

            var paymentId = Guid.NewGuid();
            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = paymentId,
                TenantId = tenantId,
                PlanId = plan.Id,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoPagoProveedor.Confirmado,
                TilopayRecurringPlanId = 7101,
                ReferenciaInterna = "LXA-0B6BFF-71BBE2920A",
                ProviderTransactionId = "5328829",
                ProviderSubscriberId = null,
                ClienteEmail = "buyer1@example.com",
                Monto = 8000m,
                Moneda = "CRC",
                FechaCreacionUtc = DateTime.UtcNow.AddHours(-2),
                FechaConfirmacionUtc = DateTime.UtcNow.AddHours(-2)
            });
            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = plan.Id,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = 7101,
                ProviderSubscriptionId = null,
                FechaInicio = DateTime.UtcNow.AddDays(-1),
                FechaFin = DateTime.UtcNow.AddMonths(1)
            });
            await context.SaveChangesAsync();

            // Servicio admin REAL contra una respuesta con el formato de getSuscriptorRepeat (array "suscriptor").
            var handler = new RealResponseHandler(RealSuscriptorResponse);
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://app.tilopay.com/") };
            var adminService = new TilopayRepeatAdminService(
                httpClient,
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new OpcionesTilopay { ApiUser = "u", ApiPassword = "p", ApiKey = "k", BaseUrl = "https://app.tilopay.com/" }),
                Options.Create(new OpcionesTilopayRepeatAdmin { Enabled = true, ResolveRetryCount = 0 }),
                NullLogger<TilopayRepeatAdminService>.Instance);

            var resolution = new SubscriberResolutionService(
                context,
                adminService,
                new TenantExecutionContextAccessor(),
                new FixedBusinessDateTimeProvider(),
                NullLogger<SubscriberResolutionService>.Instance);

            var outcome = await resolution.TryResolveAndPersistAsync(new SubscriberResolutionContext
            {
                TenantId = tenantId,
                TilopayRecurringPlanId = 7101,
                Email = "buyer1@example.com",
                PaymentId = paymentId,
                IsAddon = false,
                Source = "reconciliation"
            });

            Assert.Equal(SubscriberPersistenceOutcome.Resolved, outcome);

            var pago = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            Assert.Equal("900830", pago.ProviderSubscriberId);

            var sub = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal("900830", sub.ProviderSubscriptionId);
        }

        private const string RealSuscriptorResponse =
            """
            {
              "type": "success",
              "message": "ok",
              "suscriptor": [
                {
                  "id": 900830,
                  "name": "Alex",
                  "lastname": "Example",
                  "email": "buyer1@example.com",
                  "modality": "LC_M_01",
                  "amount": "8000.00",
                  "expire": "2026-08-07",
                  "coupon": "",
                  "status": "Active",
                  "create": "2026-07-07 19:26:00"
                }
              ]
            }
            """;

        private sealed class RealResponseHandler : System.Net.Http.HttpMessageHandler
        {
            private readonly string _suscriptorBody;
            public RealResponseHandler(string suscriptorBody) => _suscriptorBody = suscriptorBody;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = path.EndsWith("login", StringComparison.OrdinalIgnoreCase)
                    ? """{ "access_token": "fake-token", "expires_in": 3600 }"""
                    : _suscriptorBody;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                });
            }
        }

        // ── 10. Checkout bloquea hosted link nuevo si ya existe suscriptor ──
        [Fact]
        public async Task Checkout_WithExistingSubscriber_RoutesToRecurrentUrlWithoutNewHostedLink()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            // Status explícito: recurrentUrl solo aplica a un suscriptor realmente ACTIVO.
            h.Admin.ResolutionResult = SubscriberResolutionResult.Found(
                new TilopaySubscriber { SubscriberId = "900830", Email = Email, Status = "Active" }, 1);
            h.Admin.RecurrentUrl = TilopayAdminOperationResult.Ok("ok", "https://tp.cr/l/recurrent-link");

            var checkout = await h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email);

            Assert.Equal("https://tp.cr/l/recurrent-link", checkout.RedirectUrl);
            // No se creó un intento de pago nuevo (habría duplicado el suscriptor).
            Assert.Equal(0, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.CheckoutBlockedExistingProviderSubscriber));
        }

        [Fact]
        public async Task Checkout_WithNoExistingSubscriber_CreatesHostedLinkNormally()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound();

            var checkout = await h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email);

            Assert.Contains("tp.cr", checkout.RedirectUrl, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
        }

        // ── Fail-CLOSED matrix: API admin caído decide según señal local ──

        [Fact]
        public async Task Checkout_ApiDown_CleanFirstPurchase_AllowsHostedLink()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.Failed("API caído");

            var checkout = await h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email);

            // Sin ninguna señal local: primera compra limpia → hosted link normal.
            Assert.Contains("tp.cr", checkout.RedirectUrl, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.CheckoutBlockedProviderVerificationUnavailable));
        }

        [Theory]
        [InlineData(EstadoPagoProveedor.Fallido)]
        [InlineData(EstadoPagoProveedor.Pendiente)]
        [InlineData(EstadoPagoProveedor.ManualReview)]
        [InlineData(EstadoPagoProveedor.Confirmado)]           // confirmado sin ProviderSubscriberId
        public async Task Checkout_ApiDown_WithPaymentSignal_Blocks(EstadoPagoProveedor estado)
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.Failed("API caído");

            h.Db.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = estado,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ReferenciaInterna = "LXA-SIGNAL-1",
                ClienteEmail = Email,
                Monto = h.Data.Charge,
                Moneda = "CRC",
                ProviderSubscriberId = null, // el caso "confirmado sin subscriber" es explícito
                FechaCreacionUtc = DateTime.UtcNow.AddMinutes(-10),
                FechaConfirmacionUtc = estado == EstadoPagoProveedor.Confirmado ? DateTime.UtcNow.AddMinutes(-10) : null
            });
            await h.Db.SaveChangesAsync();

            await Assert.ThrowsAsync<RecurringCheckoutBlockedException>(() =>
                h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email));

            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.CheckoutBlockedProviderVerificationUnavailable));
            // No se creó un intento NUEVO (sigue solo el sembrado).
            Assert.Equal(1, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
        }

        [Theory]
        [InlineData(EstadoSuscripcion.Activa)]
        [InlineData(EstadoSuscripcion.Morosa)]
        public async Task Checkout_ApiDown_WithSubscriptionSignal_Blocks(EstadoSuscripcion estado)
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.Failed("API caído");

            h.Db.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Estado = estado,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                FechaInicio = DateTime.UtcNow.AddDays(-10),
                FechaFin = DateTime.UtcNow.AddDays(20)
            });
            await h.Db.SaveChangesAsync();

            await Assert.ThrowsAsync<RecurringCheckoutBlockedException>(() =>
                h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email));

            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.CheckoutBlockedProviderVerificationUnavailable));
        }

        [Fact]
        public async Task Checkout_ApiDown_WithExistingProviderSubscriptionId_Blocks()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.Failed("API caído");

            h.Db.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Estado = EstadoSuscripcion.Cancelada, // aunque esté cancelada, el subscriber previo importa
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ProviderSubscriptionId = "900830",
                FechaInicio = DateTime.UtcNow.AddDays(-30)
            });
            await h.Db.SaveChangesAsync();

            await Assert.ThrowsAsync<RecurringCheckoutBlockedException>(() =>
                h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email));

            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.CheckoutBlockedProviderVerificationUnavailable));
        }

        // ══ Plan destino con suscriptor viejo ELIMINADO: volver a ese plan es legítimo ══
        // Regresión: LC_M_03 → LC_M_02 se bloqueaba con "Ya tenés una
        // suscripción activa" porque el 900117 del plan viejo estaba en status "Delete" y el filtro
        // solo reconocía "deleted". Un suscriptor eliminado no cobra: no puede bloquear nada.

        [Fact]
        public async Task Checkout_TargetSubscriberDeleted_AllowsHostedCheckoutAndNeverCallsRecurrentUrl()
        {
            using var h = await Harness.CreateAsync(workers: 1);

            // El tenant está hoy en OTRO plan (cambio de plan hacia el destino).
            SeedCurrentSubscriptionOnOtherPlan(h, otherRecurringPlanId: 7103);

            // En el plan destino solo queda el suscriptor viejo, ya eliminado.
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Delete" }
            };

            var checkout = await h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email);

            // Hosted checkout nuevo, no la URL de renovación del suscriptor muerto.
            Assert.Equal(0, h.Admin.RecurrentUrlCalls);
            Assert.NotEqual("https://tp.cr/l/recurrent-link", checkout.RedirectUrl);
            Assert.Equal(1, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());

            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.CheckoutBlockedExistingProviderSubscriber));
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeIgnoredInactiveTargetProviderSubscriber));
        }

        [Theory]
        [InlineData("Delete")]
        [InlineData("deleted")]
        [InlineData("Eliminado")]
        [InlineData("Cancelled")]
        [InlineData("inactivo")]
        [InlineData("4")]
        public async Task Checkout_TargetSubscriberInactiveVariants_AllowHostedCheckout(string status)
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = status }
            };

            await h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email);

            Assert.Equal(0, h.Admin.RecurrentUrlCalls);
            Assert.Equal(1, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
        }

        [Fact]
        public async Task Checkout_TargetHasMultipleDeletedSubscribers_AllowsHostedCheckout()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Delete" },
                new() { SubscriberId = "380001", Email = Email, Status = "Deleted" },
                new() { SubscriberId = "370002", Email = Email, Status = "4" }
            };

            await h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email);

            Assert.Equal(0, h.Admin.RecurrentUrlCalls);
            Assert.Equal(1, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeIgnoredInactiveTargetProviderSubscriber));
        }

        [Fact]
        public async Task Checkout_PlanChangeTargetHasActiveSubscriber_BlocksForManualReview()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            SeedCurrentSubscriptionOnOtherPlan(h, otherRecurringPlanId: 7103);

            // El destino YA tiene alguien cobrando: pagar dejaría dos suscriptores en ese plan.
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Active" }
            };

            var ex = await Assert.ThrowsAsync<RecurringCheckoutBlockedException>(() =>
                h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email));

            Assert.Contains("suscripción activa previa en el plan destino", ex.Message);
            Assert.Equal(0, h.Admin.RecurrentUrlCalls); // recurrentUrl no arregla un cambio de plan.
            Assert.Equal(0, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeBlockedExistingActiveTargetSubscriber));
        }

        [Fact]
        public async Task Checkout_TargetHasMultipleActiveSubscribers_BlocksManualReview()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Active" },
                new() { SubscriberId = "386130", Email = Email, Status = "Active" }
            };

            await Assert.ThrowsAsync<RecurringCheckoutBlockedException>(() =>
                h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email));

            Assert.Equal(0, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.CheckoutBlockedExistingProviderSubscriber));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Paused")]
        [InlineData("3")]
        [InlineData("algo-que-tilopay-invente")]
        public async Task Checkout_TargetSubscriberWithUnknownStatus_BlocksInsteadOfAssumingFree(string? status)
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = status }
            };

            await Assert.ThrowsAsync<RecurringCheckoutBlockedException>(() =>
                h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email));

            Assert.Equal(0, h.Admin.RecurrentUrlCalls);
            Assert.Equal(0, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.CheckoutBlockedUnknownTargetSubscriberStatus));
        }

        [Fact]
        public async Task Checkout_UnknownStatusAudit_DoesNotLeakRawSubscriberId()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "raro" }
            };

            await Assert.ThrowsAsync<RecurringCheckoutBlockedException>(() =>
                h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email));

            foreach (var audit in await h.Db.PlatformAuditLogs.ToListAsync())
            {
                Assert.DoesNotContain("900117", $"{audit.Reason} {audit.AfterJson} {audit.EntityId}");
                Assert.DoesNotContain(Email, $"{audit.Reason} {audit.AfterJson} {audit.EntityId}");
            }
        }

        // ── Actualizar tarjeta / reintentar pago del MISMO plan: ahí recurrentUrl sí es lo correcto ──
        // Es el único caso que justifica recurrentUrl: el suscriptor existe, está ACTIVO y no
        // estamos cambiando de plan, así que se renueva en vez de crear un segundo suscriptor.
        [Fact]
        public async Task Checkout_SamePlanWithActiveSubscriber_RoutesToRecurrentUrlForCardUpdate()
        {
            using var h = await Harness.CreateAsync(workers: 1);

            // El tenant YA está en este mismo plan recurrente.
            SeedCurrentSubscriptionOnOtherPlan(h, otherRecurringPlanId: h.Data.RecurringPlanId);

            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900830", Email = Email, Status = "Active" }
            };
            h.Admin.RecurrentUrl = TilopayAdminOperationResult.Ok("ok", "https://tp.cr/l/recurrent-link");

            var checkout = await h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email);

            Assert.Equal("https://tp.cr/l/recurrent-link", checkout.RedirectUrl);
            Assert.Equal(1, h.Admin.RecurrentUrlCalls);
            Assert.Equal(0, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
            // No es un cambio de plan: no aplica el bloqueo del plan destino.
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.PlanChangeBlockedExistingActiveTargetSubscriber));
        }

        // ══ El flujo que YA funciona no se puede romper ══
        // LC_M_02 → LC_M_03 (escenario de upgrade): el tenant tiene un suscriptor ACTIVO, pero
        // en su plan ACTUAL. El plan DESTINO está vacío, así que el checkout debe abrirse igual.
        // Si el pre-check mirara "¿tiene el cliente algo activo?" en vez de "¿hay alguien cobrando
        // el plan destino?", este upgrade se bloquearía. Es el error opuesto al del downgrade.
        [Fact]
        public async Task Checkout_PlanChangeToEmptyTargetPlan_StillOpensHostedCheckout()
        {
            using var h = await Harness.CreateAsync(workers: 1);

            // Suscriptor vivo en el plan actual (7102), como LC_M_02 antes del upgrade.
            SeedCurrentSubscriptionOnOtherPlan(h, otherRecurringPlanId: 7102);

            // El plan destino no tiene suscriptores todavía.
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>();

            var checkout = await h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email);

            Assert.NotNull(checkout.RedirectUrl);
            Assert.Equal(0, h.Admin.RecurrentUrlCalls);
            Assert.Equal(1, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.PlanChangeBlockedExistingActiveTargetSubscriber));
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.CheckoutBlockedExistingProviderSubscriber));
        }

        /// <summary>Pone al tenant en un plan recurrente distinto del que se va a comprar.</summary>
        private static void SeedCurrentSubscriptionOnOtherPlan(Harness h, int otherRecurringPlanId)
        {
            h.Db.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = otherRecurringPlanId,
                ProviderSubscriptionId = "386130",
                FechaInicio = DateTime.UtcNow.AddDays(-2),
                FechaFin = DateTime.UtcNow.AddDays(28),
                FechaUltimaActualizacionUtc = DateTime.UtcNow.AddDays(-2)
            });
            h.Db.SaveChanges();
        }

        [Fact]
        public async Task Checkout_AmbiguousSubscribers_Blocks()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.Ambiguous(2, "dos suscriptores por email");

            await Assert.ThrowsAsync<RecurringCheckoutBlockedException>(() =>
                h.Payments.CreateRecurringCheckoutAsync(h.TenantId, h.PlanId, "Owner", Email));

            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.CheckoutBlockedExistingProviderSubscriber));
            Assert.Equal(0, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
        }

        // ── 13. Upgrade cancela el suscriptor viejo tras activar el nuevo ──
        [Fact]
        public async Task UpgradeCancellation_DeletesOldSubscriberAndAudits()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            SeedAppliedUpgradeIntent(h, oldSubscriber: "OLD-111", newSubscriber: "NEW-222");
            await h.Db.SaveChangesAsync();

            h.Admin.DeleteResult = TilopayAdminOperationResult.Ok("eliminado");

            await h.ProviderManager.TryCancelOldSubscriberForUpgradeAsync(h.TenantId);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
            Assert.Contains("OLD-111", h.Admin.DeletedSubscriberIds);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.UpgradeOldProviderSubscriptionCancellationCompleted));
        }

        // ── 14. Si la cancelación del proveedor falla, BillingHealth queda en alerta ──
        [Fact]
        public async Task UpgradeCancellation_Failure_LeavesPendingAndHealthAlert()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            SeedAppliedUpgradeIntent(h, oldSubscriber: "OLD-111", newSubscriber: "NEW-222");
            await h.Db.SaveChangesAsync();

            // Delete y edit fallan, y la verificación muestra al viejo TODAVÍA activo:
            // fallo real (no idempotente), debe quedar pendiente.
            h.Admin.DeleteResult = TilopayAdminOperationResult.Fail("TiloPay rechazó la baja");
            h.Admin.EditResult = TilopayAdminOperationResult.Fail("edit también rechazado");
            h.Admin.GetSubscribers.Add(new TilopaySubscriber { SubscriberId = "OLD-111", Status = "Active" });

            await h.ProviderManager.TryCancelOldSubscriberForUpgradeAsync(h.TenantId);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(ProviderCancellationState.PendingManualCancellation, intent.OldProviderCancellation);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.UpgradeOldProviderSubscriptionCancellationFailed));

            var health = await h.Health.BuildAsync();
            Assert.True(health.ProviderCancellationsFailedLast7d >= 1);
        }

        // ── Regresión de la evidencia real LC_M_02 → LC_M_03 (prod 2026-07-15) ──

        /// <summary>
        /// El webhook de TiloPay NO trae id_suscriptor: el nuevo (900370) solo vive en el pago,
        /// resuelto antes por getSuscriptorRepeat. La suscripción DEBE quedar con 900370, jamás
        /// con el viejo 900770, y el intent debe registrar NewProviderSubscriptionId.
        /// </summary>
        [Fact]
        public async Task PlanChange_PaymentSuccessWithoutSubscriberInWebhook_UsesResolvedSubscriberFromPayment()
        {
            using var h = await Harness.CreateAsync(workers: 1);

            // Suscripción vigente LC_M_02 con el suscriptor viejo 900770.
            var oldSubscription = SeedActiveOldSubscription(h, oldSubscriberId: "900770", oldRecurringPlanId: 7102);
            // Intent de cambio Pending hacia el plan del harness (destino).
            SeedPendingPlanChangeIntent(h, oldSubscriberId: "900770", oldRecurringPlanId: 7102);
            await h.Db.SaveChangesAsync();

            // Checkout del destino; el registro resuelve el suscriptor NUEVO 900370.
            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound();
            await h.StartCheckoutAsync();
            h.Admin.ResolutionResult = SubscriberResolutionResult.Found(
                new TilopaySubscriber { SubscriberId = "900370", Email = Email }, 1);
            await h.ProcessWebhookAsync("repeat_registration", amount: null, transactionId: "TX-REG-900370");

            // El pago ya tiene 900370 (resuelto), pero el webhook de pago NO trae subscriber.
            h.Admin.DeleteResult = TilopayAdminOperationResult.Ok("ok");   // baja del viejo OK
            // getSuscriptorRepeat del plan viejo: 900770 ya no aparece → verificación confirma baja.
            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "9000001");

            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, subscription.Estado);
            Assert.Equal("900370", subscription.ProviderSubscriptionId);        // NO 900770
            Assert.NotEqual("900770", subscription.ProviderSubscriptionId);
            Assert.Equal(h.Data.RecurringPlanId, subscription.TilopayRecurringPlanId);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(PlanChangeIntentState.Applied, intent.Estado);
            Assert.Equal("900370", intent.NewProviderSubscriptionId);          // ya no NULL
        }

        /// <summary>
        /// El ciclo del plan nuevo arranca en la confirmación del pago, NO encadenado al
        /// vencimiento del plan viejo (bug: FechaInicio quedaba en el expire anterior).
        /// </summary>
        [Fact]
        public async Task PlanChange_NewCycleStartsAtPaymentConfirmation_NotChainedToOldExpiry()
        {
            using var h = await Harness.CreateAsync(workers: 1);

            var oldExpiry = DateTime.UtcNow.AddDays(30); // el plan viejo vence en 30 días
            SeedActiveOldSubscription(h, oldSubscriberId: "900770", oldRecurringPlanId: 7102, fechaFin: oldExpiry);
            SeedPendingPlanChangeIntent(h, oldSubscriberId: "900770", oldRecurringPlanId: 7102);
            await h.Db.SaveChangesAsync();

            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound();
            await h.StartCheckoutAsync();
            h.Admin.ResolutionResult = SubscriberResolutionResult.Found(
                new TilopaySubscriber { SubscriberId = "900370", Email = Email }, 1);
            await h.ProcessWebhookAsync("repeat_registration", amount: null, transactionId: "TX-REG-CYCLE");
            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "TX-PAY-CYCLE");

            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();

            // El ciclo NO arranca en el vencimiento viejo; arranca ahora (±1 día de tolerancia).
            Assert.True(subscription.FechaInicio < oldExpiry.AddDays(-1),
                $"FechaInicio {subscription.FechaInicio:O} quedó encadenada al vencimiento viejo {oldExpiry:O}");
            Assert.NotNull(subscription.FechaFin);
            // Mensual: fin ≈ inicio + 1 mes.
            Assert.Equal(subscription.FechaInicio.AddMonths(1), subscription.FechaFin);
            Assert.Equal(subscription.FechaFin, subscription.FechaProximoCobroUtc);
        }

        /// <summary>
        /// Si el suscriptor nuevo no se pudo resolver, el plan local NO se aplica: aplicarlo dejaría
        /// la suscripción en el plan nuevo apuntando al suscriptor viejo (doble cobro invisible).
        /// </summary>
        [Fact]
        public async Task PlanChange_PaymentSuccessWithoutResolvedSubscriber_DoesNotApplyPlan_AndAudits()
        {
            using var h = await Harness.CreateAsync(workers: 1);

            SeedActiveOldSubscription(h, oldSubscriberId: "900770", oldRecurringPlanId: 7102);
            SeedPendingPlanChangeIntent(h, oldSubscriberId: "900770", oldRecurringPlanId: 7102);
            await h.Db.SaveChangesAsync();

            // La resolución NUNCA encuentra el suscriptor nuevo (API caída o aún no propagado).
            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound();
            await h.StartCheckoutAsync();
            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "TX-NO-SUB");

            // El plan NO se aplicó: la suscripción sigue en el viejo, con su suscriptor viejo.
            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(7102, subscription.TilopayRecurringPlanId);
            Assert.Equal("900770", subscription.ProviderSubscriptionId);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(PlanChangeIntentState.Pending, intent.Estado);   // no Applied
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeBlockedMissingNewProviderSubscription));

            // El pago sí quedó confirmado (el dinero entró); la reconciliación terminará el cambio.
            Assert.Equal(1, await h.Db.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p => p.Estado == EstadoPagoProveedor.Confirmado));
        }

        /// <summary>
        /// Reparación del estado EXACTO que quedó en producción: intent Applied sin
        /// NewProviderSubscriptionId y suscripción en el plan destino con el suscriptor viejo
        /// y el ciclo encadenado. La reconciliación lo repara sin crear pagos nuevos.
        /// </summary>
        [Fact]
        public async Task Reconciliation_RepairsProductionInconsistentState_WithoutCreatingPayments()
        {
            using var h = await Harness.CreateAsync(workers: 1);

            var confirmedAtUtc = DateTime.UtcNow.AddHours(-2);
            var chainedStart = DateTime.UtcNow.AddDays(30);   // ciclo encadenado (bug)

            var paymentId = Guid.NewGuid();
            h.Db.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = paymentId,
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoPagoProveedor.Confirmado,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ReferenciaInterna = "LXA-PROD-REPAIR",
                ProviderTransactionId = "9000001",
                ProviderSubscriberId = "900370",             // el subscriber NUEVO sí está aquí
                ClienteEmail = Email,
                Monto = h.Data.Charge,
                Moneda = "CRC",
                FechaCreacionUtc = confirmedAtUtc,
                FechaConfirmacionUtc = confirmedAtUtc
            });
            // Suscripción en el plan DESTINO pero con el suscriptor VIEJO y ciclo encadenado.
            h.Db.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                CodigoPlan = h.Data.Code,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ProviderSubscriptionId = "900770",           // ← viejo (bug)
                FechaInicio = chainedStart,                  // ← encadenado (bug)
                FechaFin = chainedStart.AddMonths(1),
                FechaProximoCobroUtc = chainedStart.AddMonths(1)
            });
            h.Db.PlanChangeIntents.Add(new PlanChangeIntent
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                FromPlanCode = "LC_M_02",
                FromTilopayRecurringPlanId = 7102,
                FromProviderSubscriptionId = "900770",
                ToPlanId = h.PlanId,
                ToPlanCode = h.Data.Code,
                ToWorkerCount = h.Data.Workers,
                ToBillingCycle = BillingCycle.Monthly,
                ToTilopayRecurringPlanId = h.Data.RecurringPlanId,
                Estado = PlanChangeIntentState.Applied,
                OldProviderCancellation = ProviderCancellationState.PendingManualCancellation,
                NewProviderSubscriptionId = null,            // ← NULL (bug)
                PagoSuscripcionId = paymentId,
                AppliedAtUtc = confirmedAtUtc
            });
            await h.Db.SaveChangesAsync();

            var paymentsBefore = await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync();
            h.Admin.DeleteResult = TilopayAdminOperationResult.Ok("eliminado"); // baja del viejo OK y verificada

            var report = await h.Reconciliation.RunAsync();

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal("900370", intent.NewProviderSubscriptionId);
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);

            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal("900370", subscription.ProviderSubscriptionId);   // corregido
            Assert.Equal("9000001", subscription.ProviderTransactionId);
            // Ciclo recalculado desde la confirmación del pago, no encadenado.
            Assert.Equal(confirmedAtUtc, subscription.FechaInicio);
            Assert.Equal(confirmedAtUtc.AddMonths(1), subscription.FechaFin);
            Assert.Equal(subscription.FechaFin, subscription.FechaProximoCobroUtc);

            Assert.True(report.PlanChangesRepaired >= 1);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeInconsistentStateRepaired));
            // NUNCA crea pagos nuevos.
            Assert.Equal(paymentsBefore, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
        }

        // ── Verificación OBLIGATORIA post-baja: un 200 de TiloPay no basta ──
        [Fact]
        public async Task UpgradeCancellation_DeleteReturns200ButStillActive_NotCancelled_AuditsVerificationFailed()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            SeedAppliedUpgradeIntent(h, oldSubscriber: "OLD-111", newSubscriber: "NEW-222");
            await h.Db.SaveChangesAsync();

            // TiloPay dice 200 al delete... pero getSuscriptorRepeat muestra al viejo TODAVÍA Active.
            h.Admin.DeleteResult = TilopayAdminOperationResult.Ok("ok");
            h.Admin.GetSubscribers.Add(new TilopaySubscriber { SubscriberId = "OLD-111", Status = "Active" });

            await h.ProviderManager.TryCancelOldSubscriberForUpgradeAsync(h.TenantId);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(ProviderCancellationState.PendingManualCancellation, intent.OldProviderCancellation);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeOldSubscriberCancellationVerificationFailed));

            var health = await h.Health.BuildAsync();
            Assert.True(health.ProviderCancellationsFailedLast7d >= 1);
        }

        [Fact]
        public async Task UpgradeCancellation_Delete200AndVerifyAbsent_MarksCancelled()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            SeedAppliedUpgradeIntent(h, oldSubscriber: "OLD-111", newSubscriber: "NEW-222");
            await h.Db.SaveChangesAsync();

            h.Admin.DeleteResult = TilopayAdminOperationResult.Ok("ok");
            // GetSubscribers vacío: verificación confirma ausente.

            await h.ProviderManager.TryCancelOldSubscriberForUpgradeAsync(h.TenantId);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
        }

        [Fact]
        public async Task UpgradeCancellation_EditFallback200AndVerifyInactive_MarksCancelled()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            SeedAppliedUpgradeIntent(h, oldSubscriber: "OLD-111", newSubscriber: "NEW-222");
            await h.Db.SaveChangesAsync();

            // delete falla; edit status=4 devuelve 200; verificación: el viejo quedó status 4.
            h.Admin.DeleteResult = TilopayAdminOperationResult.Fail("delete rechazado");
            h.Admin.EditResult = TilopayAdminOperationResult.Ok("edited");
            h.Admin.GetSubscribers.Add(new TilopaySubscriber { SubscriberId = "OLD-111", Status = "4" });

            await h.ProviderManager.TryCancelOldSubscriberForUpgradeAsync(h.TenantId);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
        }

        // ── Recuperación: viejo cancelado externamente pero DB local incompleta ──
        [Fact]
        public async Task Reconciliation_OldCancelledExternallyButDbIncomplete_ConvergesWithoutDoubleCharge()
        {
            using var h = await Harness.CreateAsync(workers: 1);

            // Estado tras un crash: el nuevo plan YA quedó aplicado localmente (suscripción activa)
            // y el viejo YA fue eliminado en TiloPay, pero el intent quedó PendingManualCancellation.
            SeedAppliedUpgradeIntent(h, oldSubscriber: "OLD-GONE", newSubscriber: "NEW-999");
            h.Db.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                CodigoPlan = h.Data.Code,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ProviderSubscriptionId = "NEW-999",
                FechaInicio = DateTime.UtcNow.AddHours(-1),
                FechaFin = DateTime.UtcNow.AddMonths(1)
            });
            await h.Db.SaveChangesAsync();

            // TiloPay: delete falla ("no existe") y la verificación confirma que el viejo no está.
            h.Admin.DeleteResult = TilopayAdminOperationResult.Fail("no existe");
            h.Admin.EditResult = TilopayAdminOperationResult.Fail("no existe");

            var paymentsBefore = await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync();

            await h.Reconciliation.RunAsync();

            // Converge: intent Cancelled; suscripción local intacta en el plan nuevo; sin pagos nuevos.
            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);

            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(EstadoSuscripcion.Activa, subscription.Estado);
            Assert.Equal("NEW-999", subscription.ProviderSubscriptionId);

            Assert.Equal(paymentsBefore, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());
        }

        // ── Pendings legacy (previos al hardening) ──
        [Fact]
        public async Task LegacyPending_NewCheckoutExpiresIt_NoUnboundedDuplicates()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound();

            // Pending legacy: viejo (fuera de la ventana de reuso de 30 min), sin txid ni subscriber.
            h.Db.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoPagoProveedor.Pendiente,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ReferenciaInterna = "LXA-LEGACY-1",
                ProviderTransactionId = null,
                ProviderSubscriberId = null,
                ClienteEmail = Email,
                Monto = h.Data.Charge,
                Moneda = "CRC",
                CheckoutUrl = "https://tp.cr/l/legacy",
                FechaCreacionUtc = DateTime.UtcNow.AddDays(-2)
            });
            await h.Db.SaveChangesAsync();

            await h.StartCheckoutAsync();

            var attempts = await h.Db.PagosSuscripcion.IgnoreQueryFilters().ToListAsync();
            // El legacy quedó Expirado (no elegible) y solo hay UN pendiente abierto (el nuevo).
            Assert.Equal(EstadoPagoProveedor.Expirado, attempts.Single(p => p.ReferenciaInterna == "LXA-LEGACY-1").Estado);
            Assert.Equal(1, attempts.Count(p => p.Estado == EstadoPagoProveedor.Pendiente));
        }

        [Fact]
        public async Task WebhookWithNewLcRef_ConfirmsOnlyNewAttempt_NotLegacyPending()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound();

            // Legacy pending con OTRO email (no lo expira el checkout nuevo) y sin lc_ref propio.
            var legacyId = Guid.NewGuid();
            h.Db.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = legacyId,
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoPagoProveedor.Pendiente,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ReferenciaInterna = "LXA-LEGACY-2",
                ClienteEmail = "otro-legacy@test.local",
                Monto = h.Data.Charge,
                Moneda = "CRC",
                FechaCreacionUtc = DateTime.UtcNow.AddDays(-2)
            });
            await h.Db.SaveChangesAsync();

            await h.StartCheckoutAsync();
            // El webhook llega con el lc_ref del checkout NUEVO (correlación exacta por referencia).
            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "TX-NEW-REF-1");

            var attempts = await h.Db.PagosSuscripcion.IgnoreQueryFilters().ToListAsync();
            // El lc_ref nuevo confirmó SOLO el intento nuevo; el legacy no se tocó.
            Assert.Equal(EstadoPagoProveedor.Pendiente, attempts.Single(p => p.Id == legacyId).Estado);
            Assert.Equal(1, attempts.Count(p => p.Estado == EstadoPagoProveedor.Confirmado));
            Assert.NotEqual(legacyId, attempts.Single(p => p.Estado == EstadoPagoProveedor.Confirmado).Id);
        }

        [Fact]
        public async Task WebhookWithoutLcRef_TwoOpenPendings_GoesToManualReview_NotMisapplied()
        {
            using var h = await Harness.CreateAsync(workers: 1);

            // Dos pendings abiertos del MISMO plan y email (legacy + reciente), sembrados directo.
            foreach (var reference in new[] { "LXA-AMB-1", "LXA-AMB-2" })
            {
                h.Db.PagosSuscripcion.Add(new PagoSuscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = h.TenantId,
                    PlanId = h.PlanId,
                    Proveedor = PaymentProviderType.Tilopay,
                    Estado = EstadoPagoProveedor.Pendiente,
                    TilopayRecurringPlanId = h.Data.RecurringPlanId,
                    ReferenciaInterna = reference,
                    ClienteEmail = Email,
                    Monto = h.Data.Charge,
                    Moneda = "CRC",
                    FechaCreacionUtc = DateTime.UtcNow.AddHours(-3)
                });
            }
            await h.Db.SaveChangesAsync();

            // Webhook de éxito SIN lc_ref: ambiguo entre los dos pendings → revisión manual,
            // nunca aplicar a ciegas al equivocado.
            h.Provider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = h.Data.RecurringPlanId,
                CustomerEmail = Email,
                Amount = h.Data.Charge,
                Currency = "CRC",
                ProviderTransactionId = "TX-AMBIGUO-1",
                IsRecurring = true
            };

            var result = await h.Payments.ProcessTilopayWebhookAsync("{\"x\":1}", "corr-amb", "repeat_payment_success");

            Assert.False(result.IsProcessed);

            var attempts = await h.Db.PagosSuscripcion.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(0, attempts.Count(p => p.Estado == EstadoPagoProveedor.Confirmado));
            Assert.Empty(await h.Db.Suscripciones.IgnoreQueryFilters().Where(s => s.Estado == EstadoSuscripcion.Activa).ToListAsync());
        }

        // ── Idempotencia: si el viejo ya no está activo, la cancelación se considera exitosa ──
        [Fact]
        public async Task UpgradeCancellation_DeleteFailsButSubscriberAlreadyGone_IsTreatedAsSuccess()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            SeedAppliedUpgradeIntent(h, oldSubscriber: "OLD-GONE", newSubscriber: "NEW-222");
            await h.Db.SaveChangesAsync();

            // delete y edit fallan, PERO getSuscriptorRepeat no devuelve al viejo → ya no cobrable.
            h.Admin.DeleteResult = TilopayAdminOperationResult.Fail("no existe");
            h.Admin.EditResult = TilopayAdminOperationResult.Fail("no existe");
            // GetSubscribers vacío: el viejo ya no aparece.

            await h.ProviderManager.TryCancelOldSubscriberForUpgradeAsync(h.TenantId);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
        }

        // ── Cambio de plan: la reconciliación reintenta la cancelación del suscriptor viejo ──
        [Fact]
        public async Task Reconciliation_RetriesPendingOldSubscriberCancellation_AndAudits()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            SeedAppliedUpgradeIntent(h, oldSubscriber: "OLD-900770", newSubscriber: "NEW-393000");
            await h.Db.SaveChangesAsync();

            // En el webhook la cancelación había fallado (quedó PendingManualCancellation).
            // Ahora TiloPay ya responde OK: la reconciliación debe cancelar el viejo.
            h.Admin.DeleteResult = TilopayAdminOperationResult.Ok("eliminado");

            var report = await h.Reconciliation.RunAsync();

            Assert.Contains("OLD-900770", h.Admin.DeletedSubscriberIds);
            Assert.True(report.OldSubscriberCancellationsRetried >= 1);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeOldSubscriberCancellationRetried));
        }

        [Fact]
        public async Task Reconciliation_OldCancellationStillFails_StaysPending_AndHealthFlagsRisk()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            SeedAppliedUpgradeIntent(h, oldSubscriber: "OLD-900770", newSubscriber: "NEW-393000");
            await h.Db.SaveChangesAsync();

            h.Admin.DeleteResult = TilopayAdminOperationResult.Fail("TiloPay sigue rechazando la baja");
            h.Admin.EditResult = TilopayAdminOperationResult.Fail("edit también rechazado");
            h.Admin.GetSubscribers.Add(new TilopaySubscriber { SubscriberId = "OLD-900770", Status = "Active" });

            await h.Reconciliation.RunAsync();

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(ProviderCancellationState.PendingManualCancellation, intent.OldProviderCancellation);

            // Health debe exponer el riesgo de doble cobro (cambio aplicado, viejo no cancelado).
            var health = await h.Health.BuildAsync();
            Assert.True(health.PlanChangeManualReviewCount >= 1);
        }

        [Fact]
        public async Task Reconciliation_TwoTenantsWithPendingCancellations_NoTenantMixAndBothRetried()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            SeedAppliedUpgradeIntent(h, oldSubscriber: "OLD-A", newSubscriber: "NEW-A");
            await h.Db.SaveChangesAsync();

            // Segundo tenant con su propio intent aplicado pendiente de cancelar (mismo DbContext).
            var tenantB = Guid.NewGuid();
            h.Db.Tenants.Add(new Tenant { Id = tenantB, Nombre = "Tenant B", Activo = true });
            h.Db.PlanChangeIntents.Add(new PlanChangeIntent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantB,
                FromPlanCode = "LC_M_02",
                // Sin el plan viejo no habría cómo verificar la baja con getSuscriptorRepeat, y el
                // reintento salta el intent en vez de borrar a ciegas. En producción siempre viene.
                FromTilopayRecurringPlanId = 7102,
                FromProviderSubscriptionId = "OLD-B",
                ToPlanId = h.PlanId,
                ToPlanCode = "LC_M_03",
                ToWorkerCount = 3,
                ToBillingCycle = BillingCycle.Monthly,
                ToTilopayRecurringPlanId = 7103,
                Estado = PlanChangeIntentState.Applied,
                OldProviderCancellation = ProviderCancellationState.PendingManualCancellation,
                NewProviderSubscriptionId = "NEW-B",
                AppliedAtUtc = DateTime.UtcNow
            });
            await h.Db.SaveChangesAsync();

            h.Admin.DeleteResult = TilopayAdminOperationResult.Ok("eliminado");

            // No debe lanzar "contexto de sistema intentando mezclar tenants".
            var report = await h.Reconciliation.RunAsync();

            Assert.Contains("OLD-A", h.Admin.DeletedSubscriberIds);
            Assert.Contains("OLD-B", h.Admin.DeletedSubscriberIds);
            Assert.True(report.OldSubscriberCancellationsRetried >= 2);
        }

        // ── 15. Los controllers admin exigen política de super admin ──
        [Fact]
        public void ProviderSubscriptionController_RequiresPlatformSuperAdminPolicy()
        {
            AssertPlatformPolicy(typeof(PlatformProviderSubscriptionController));
            AssertPlatformPolicy(typeof(PlatformBillingHealthController));
        }

        // ── 16. La auditoría enmascara el id_suscriptor (no guarda el id completo en claro) ──
        [Fact]
        public async Task ResolvedAudit_MasksSubscriberId()
        {
            using var h = await Harness.CreateAsync(workers: 1);
            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound();
            await h.StartCheckoutAsync();
            h.Admin.ResolutionResult = SubscriberResolutionResult.Found(
                new TilopaySubscriber { SubscriberId = "900830", Email = Email }, 1);

            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "TX-MASK-1");

            var resolvedLog = await h.Db.PlatformAuditLogs
                .FirstAsync(l => l.Action == PlatformAuditActions.ProviderSubscriberResolved);

            // El id completo NUNCA aparece en claro; solo el sufijo enmascarado.
            Assert.DoesNotContain("900830", resolvedLog.Reason ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("***0830", resolvedLog.Reason ?? string.Empty, StringComparison.Ordinal);
        }

        // ══ Suscriptor resuelto TARDE: el pago se confirma antes de saber quién cobra ══
        // Regresión (LC_M_03 → LC_M_02): repeat_payment_success llegó
        // sin id_suscriptor (TiloPay nunca lo manda), el guard anti doble-cobro se negó a aplicar
        // el plan dentro de la transacción, el suscriptor se resolvió medio segundo después... y
        // nadie volvió a aplicar. El cliente pagó LC_M_02 y siguió viendo LC_M_03, con DOS
        // suscriptores activos en TiloPay.

        [Fact]
        public async Task Webhook_PaymentSuccessThenLateSubscriberResolution_AppliesPlanChangeAndCancelsOld()
        {
            using var h = await Harness.CreateAsync(workers: 2); // destino LC_M_02 / 7102

            // Estado previo: el tenant está en LC_M_03 (7103) con su suscriptor vivo.
            SeedActiveOldSubscription(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103);
            var intentId = SeedPendingPlanChangeIntent(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103, fromPlanCode: "LC_M_03");
            await h.Db.SaveChangesAsync();

            // Durante el checkout el destino todavía no tiene suscriptor.
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>();
            h.Admin.ResolutionResult = SubscriberResolutionResult.NotFound();
            await h.StartCheckoutAsync();
            await LinkIntentToPaymentAsync(h, intentId);

            // El pago se confirma y RECIÉN AHÍ TiloPay muestra al suscriptor cobrando el destino.
            // Es el mismo 900117 de antes: TiloPay reactivó el que estaba Delete en vez de crear uno.
            h.Admin.ResolutionResult = SubscriberResolutionResult.Found(
                new TilopaySubscriber { SubscriberId = "900117", Email = Email, Status = "Active" }, 1);
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Active" }
            };
            // GetSubscribers vacío = getSuscriptorRepeat del plan VIEJO tras la baja: el 386130 ya
            // no aparece, así que la cancelación queda verificada.

            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "5397431");

            // El cambio quedó APLICADO en el mismo webhook, no 24h después.
            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(7102, subscription.TilopayRecurringPlanId);
            Assert.Equal("900117", subscription.ProviderSubscriptionId);
            Assert.Equal("5397431", subscription.ProviderTransactionId);
            Assert.Equal(EstadoSuscripcion.Activa, subscription.Estado);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
            Assert.Equal(PlanChangeIntentState.Applied, intent.Estado);
            Assert.Equal("900117", intent.NewProviderSubscriptionId);

            // Y el viejo (386130) se canceló y verificó.
            Assert.Contains("386130", h.Admin.DeletedSubscriberIds);
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);

            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeAppliedAfterLateSubscriberResolution));
        }

        [Fact]
        public async Task LateApplication_ReactivatedPreviouslyDeletedSubscriber_IsAcceptedAsNew()
        {
            using var h = await Harness.CreateAsync(workers: 2);

            SeedActiveOldSubscription(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103);
            var intentId = SeedPendingPlanChangeIntent(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103, fromPlanCode: "LC_M_03");
            var paymentId = SeedConfirmedPayment(h, subscriberId: "900117", transactionId: "5397431");
            await h.LinkIntentAsync(intentId, paymentId);

            // 900117 fue el suscriptor VIEJO de un cambio anterior y estaba Delete; TiloPay lo
            // reactivó para este pago. Ser un id "histórico" no lo hace inválido: hoy cobra el destino.
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Active" }
            };

            var result = await h.LateApplication.ApplyPendingPlanChangeAfterSubscriberResolvedAsync(paymentId, "test");

            Assert.Equal(LatePlanChangeApplicationStatus.Applied, result.Status);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
            Assert.Equal("900117", intent.NewProviderSubscriptionId);
            Assert.Equal(PlanChangeIntentState.Applied, intent.Estado);
        }

        [Fact]
        public async Task Reconciliation_PendingIntentWithConfirmedPaymentAndSubscriber_IsRepairedAutomatically()
        {
            using var h = await Harness.CreateAsync(workers: 2);

            SeedActiveOldSubscription(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103);
            var intentId = SeedPendingPlanChangeIntent(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103, fromPlanCode: "LC_M_03");
            var paymentId = SeedConfirmedPayment(h, subscriberId: "900117", transactionId: "5397431");
            await h.LinkIntentAsync(intentId, paymentId);

            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Active" }
            };
            // GetSubscribers vacío = getSuscriptorRepeat del plan VIEJO tras la baja: el 386130 ya
            // no aparece, así que la cancelación queda verificada.

            var paymentsBefore = await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync();

            // El worker rápido: el doble suscriptor no puede esperar al pase diario.
            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Equal(1, report.LatePlanChangesApplied);

            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(7102, subscription.TilopayRecurringPlanId);
            Assert.Equal("900117", subscription.ProviderSubscriptionId);
            Assert.Equal("5397431", subscription.ProviderTransactionId);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
            Assert.Equal(PlanChangeIntentState.Applied, intent.Estado);
            Assert.Equal(ProviderCancellationState.Cancelled, intent.OldProviderCancellation);
            Assert.Contains("386130", h.Admin.DeletedSubscriberIds);

            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeConfirmedPaymentWithLateSubscriberRepaired));

            // La reparación NUNCA crea pagos ni checkouts.
            Assert.Equal(paymentsBefore, await h.Db.PagosSuscripcion.IgnoreQueryFilters().CountAsync());

            // Y el health queda sin riesgo.
            var health = await h.Health.BuildAsync();
            Assert.Equal(0, health.OldCancellationPendingCount);
        }

        [Fact]
        public async Task LateApplication_IsIdempotent_SecondRunDoesNothing()
        {
            using var h = await Harness.CreateAsync(workers: 2);

            SeedActiveOldSubscription(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103);
            var intentId = SeedPendingPlanChangeIntent(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103, fromPlanCode: "LC_M_03");
            var paymentId = SeedConfirmedPayment(h, subscriberId: "900117", transactionId: "5397431");
            await h.LinkIntentAsync(intentId, paymentId);

            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Active" }
            };

            var first = await h.LateApplication.ApplyPendingPlanChangeAfterSubscriberResolvedAsync(paymentId, "test");
            var second = await h.LateApplication.ApplyPendingPlanChangeAfterSubscriberResolvedAsync(paymentId, "test");

            Assert.Equal(LatePlanChangeApplicationStatus.Applied, first.Status);
            Assert.Equal(LatePlanChangeApplicationStatus.NotApplicable, second.Status);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeAppliedAfterLateSubscriberResolution));
        }

        [Fact]
        public async Task LateApplication_MultipleActiveInTarget_DoesNotApplyAndFlagsManualReview()
        {
            using var h = await Harness.CreateAsync(workers: 2);

            SeedActiveOldSubscription(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103);
            var intentId = SeedPendingPlanChangeIntent(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103, fromPlanCode: "LC_M_03");
            var paymentId = SeedConfirmedPayment(h, subscriberId: "900117", transactionId: "5397431");
            await h.LinkIntentAsync(intentId, paymentId);

            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Active" },
                new() { SubscriberId = "999999", Email = Email, Status = "Active" }
            };

            var result = await h.LateApplication.ApplyPendingPlanChangeAfterSubscriberResolvedAsync(paymentId, "test");

            Assert.Equal(LatePlanChangeApplicationStatus.ManualReview, result.Status);

            // Nada se movió: el plan sigue siendo el viejo.
            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(7103, subscription.TilopayRecurringPlanId);
            Assert.Empty(h.Admin.DeletedSubscriberIds);

            var intent = await h.Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
            Assert.Equal(PlanChangeIntentState.Pending, intent.Estado);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeLateSubscriberRequiresManualReview));
        }

        [Theory]
        [InlineData("Delete")]
        [InlineData("4")]
        public async Task LateApplication_ResolvedSubscriberInactiveInTarget_DoesNotApply(string status)
        {
            using var h = await Harness.CreateAsync(workers: 2);

            SeedActiveOldSubscription(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103);
            var intentId = SeedPendingPlanChangeIntent(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103, fromPlanCode: "LC_M_03");
            var paymentId = SeedConfirmedPayment(h, subscriberId: "900117", transactionId: "5397431");
            await h.LinkIntentAsync(intentId, paymentId);

            // El suscriptor del pago NO está cobrando el destino: aplicar dejaría la suscripción
            // apuntando a un id muerto.
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = status }
            };

            var result = await h.LateApplication.ApplyPendingPlanChangeAfterSubscriberResolvedAsync(paymentId, "test");

            Assert.Equal(LatePlanChangeApplicationStatus.LeftPendingNoActiveSubscriber, result.Status);

            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(7103, subscription.TilopayRecurringPlanId);
            Assert.Empty(h.Admin.DeletedSubscriberIds);
        }

        [Fact]
        public async Task LateApplication_UnknownStatusInTarget_FlagsManualReview()
        {
            using var h = await Harness.CreateAsync(workers: 2);

            SeedActiveOldSubscription(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103);
            var intentId = SeedPendingPlanChangeIntent(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103, fromPlanCode: "LC_M_03");
            var paymentId = SeedConfirmedPayment(h, subscriberId: "900117", transactionId: "5397431");
            await h.LinkIntentAsync(intentId, paymentId);

            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "algo-raro" }
            };

            var result = await h.LateApplication.ApplyPendingPlanChangeAfterSubscriberResolvedAsync(paymentId, "test");

            Assert.Equal(LatePlanChangeApplicationStatus.ManualReview, result.Status);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PlanChangeLateSubscriberRequiresManualReview));
        }

        [Fact]
        public async Task LateApplication_ActiveSubscriberDiffersFromPayment_FlagsManualReview()
        {
            using var h = await Harness.CreateAsync(workers: 2);

            SeedActiveOldSubscription(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103);
            var intentId = SeedPendingPlanChangeIntent(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103, fromPlanCode: "LC_M_03");
            var paymentId = SeedConfirmedPayment(h, subscriberId: "900117", transactionId: "5397431");
            await h.LinkIntentAsync(intentId, paymentId);

            // TiloPay dice que quien cobra el destino es OTRO: algo cambió bajo nuestros pies.
            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "777777", Email = Email, Status = "Active" }
            };

            var result = await h.LateApplication.ApplyPendingPlanChangeAfterSubscriberResolvedAsync(paymentId, "test");

            Assert.Equal(LatePlanChangeApplicationStatus.ManualReview, result.Status);
            Assert.Empty(h.Admin.DeletedSubscriberIds);
        }

        [Fact]
        public async Task LateApplication_PaymentNotConfirmed_DoesNothing()
        {
            using var h = await Harness.CreateAsync(workers: 2);

            SeedActiveOldSubscription(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103);
            var intentId = SeedPendingPlanChangeIntent(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103, fromPlanCode: "LC_M_03");
            var paymentId = SeedConfirmedPayment(h, subscriberId: "900117", transactionId: "5397431", estado: EstadoPagoProveedor.Pendiente);
            await h.LinkIntentAsync(intentId, paymentId);

            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Active" }
            };

            var result = await h.LateApplication.ApplyPendingPlanChangeAfterSubscriberResolvedAsync(paymentId, "test");

            Assert.Equal(LatePlanChangeApplicationStatus.NotApplicable, result.Status);
            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(7103, subscription.TilopayRecurringPlanId);
        }

        [Fact]
        public async Task LateApplication_BillingPeriodStartsAtPaymentConfirmation_NotAtRepairTime()
        {
            using var h = await Harness.CreateAsync(workers: 2);

            SeedActiveOldSubscription(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103);
            var intentId = SeedPendingPlanChangeIntent(h, oldSubscriberId: "386130", oldRecurringPlanId: 7103, fromPlanCode: "LC_M_03");

            // El pago se confirmó hace 5 horas; la reparación corre recién ahora.
            var confirmedAtUtc = DateTime.UtcNow.AddHours(-5);
            var paymentId = SeedConfirmedPayment(h, subscriberId: "900117", transactionId: "5397431", confirmedAtUtc: confirmedAtUtc);
            await h.LinkIntentAsync(intentId, paymentId);

            h.Admin.TargetSubscribers = new List<TilopaySubscriber>
            {
                new() { SubscriberId = "900117", Email = Email, Status = "Active" }
            };

            await h.LateApplication.ApplyPendingPlanChangeAfterSubscriberResolvedAsync(paymentId, "test");

            var subscription = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync();

            // El ciclo arranca cuando el cliente pagó, no cuando nos enteramos.
            Assert.Equal(confirmedAtUtc, subscription.FechaInicio, TimeSpan.FromSeconds(1));
            Assert.Equal(confirmedAtUtc.AddMonths(1), subscription.FechaFin!.Value, TimeSpan.FromSeconds(1));
            Assert.Equal(subscription.FechaFin, subscription.FechaProximoCobroUtc);
        }

        // ── Helpers ──

        /// <summary>Pago del plan destino tal como queda tras repeat_payment_success + resolución tardía.</summary>
        private static Guid SeedConfirmedPayment(
            Harness h,
            string subscriberId,
            string transactionId,
            EstadoPagoProveedor estado = EstadoPagoProveedor.Confirmado,
            DateTime? confirmedAtUtc = null)
        {
            var paymentId = Guid.NewGuid();
            var confirmed = confirmedAtUtc ?? DateTime.UtcNow.AddMinutes(-2);

            h.Db.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = paymentId,
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = estado,
                ReferenciaInterna = $"LXA-{Guid.NewGuid():N}"[..20],
                ProviderReference = Guid.NewGuid().ToString("N"),
                ProviderTransactionId = transactionId,
                ProviderSubscriberId = subscriberId,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ClienteEmail = Email,
                Monto = h.Data.Charge,
                Moneda = "CRC",
                FechaCreacionUtc = confirmed.AddMinutes(-5),
                FechaConfirmacionUtc = estado == EstadoPagoProveedor.Confirmado ? confirmed : null,
                FechaActualizacionUtc = confirmed
            });

            return paymentId;
        }

        private static async Task LinkIntentToPaymentAsync(Harness h, Guid intentId)
        {
            var payment = await h.Db.PagosSuscripcion.IgnoreQueryFilters()
                .OrderByDescending(p => p.FechaCreacionUtc)
                .FirstAsync();
            await h.LinkIntentAsync(intentId, payment.Id);
        }

        private static void AssertPlatformPolicy(Type controllerType)
        {
            var attribute = controllerType
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .OfType<AuthorizeAttribute>()
                .FirstOrDefault();

            Assert.NotNull(attribute);
            Assert.Equal(PlatformAuthorizationPolicies.PlatformSuperAdmin, attribute!.Policy);
        }

        /// <summary>Suscripción vigente en el plan VIEJO con su suscriptor (estado previo al cambio).</summary>
        private static Suscripcion SeedActiveOldSubscription(
            Harness h,
            string oldSubscriberId,
            int oldRecurringPlanId,
            DateTime? fechaFin = null)
        {
            var subscription = new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                CodigoPlan = "LC_M_02",
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = oldRecurringPlanId,
                ProviderSubscriptionId = oldSubscriberId,
                MaxFuncionarios = 2,
                FechaInicio = DateTime.UtcNow.AddDays(-1),
                FechaFin = fechaFin ?? DateTime.UtcNow.AddDays(30),
                FechaProximoCobroUtc = fechaFin ?? DateTime.UtcNow.AddDays(30),
                FechaUltimaActualizacionUtc = DateTime.UtcNow.AddDays(-1)
            };
            h.Db.Suscripciones.Add(subscription);
            return subscription;
        }

        /// <summary>Intent de cambio de plan Pending hacia el plan del harness.</summary>
        /// <summary>Intento de cambio PENDING hacia el plan del harness, como lo deja el checkout.</summary>
        private static Guid SeedPendingPlanChangeIntent(
            Harness h,
            string oldSubscriberId,
            int oldRecurringPlanId,
            string fromPlanCode = "LC_M_02")
        {
            var intentId = Guid.NewGuid();
            h.Db.PlanChangeIntents.Add(new PlanChangeIntent
            {
                Id = intentId,
                TenantId = h.TenantId,
                FromPlanCode = fromPlanCode,
                FromTilopayRecurringPlanId = oldRecurringPlanId,
                FromProviderSubscriptionId = oldSubscriberId,
                ToPlanId = h.PlanId,
                ToPlanCode = h.Data.Code,
                ToWorkerCount = h.Data.Workers,
                ToBillingCycle = BillingCycle.Monthly,
                ToTilopayRecurringPlanId = h.Data.RecurringPlanId,
                Estado = PlanChangeIntentState.Pending,
                OldProviderCancellation = ProviderCancellationState.NotRequired,
                CreatedAtUtc = DateTime.UtcNow
            });

            return intentId;
        }

        private static void SeedAppliedUpgradeIntent(Harness h, string oldSubscriber, string newSubscriber)
        {
            h.Db.PlanChangeIntents.Add(new PlanChangeIntent
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                FromPlanCode = "LC_M_01",
                FromTilopayRecurringPlanId = 7101,
                FromProviderSubscriptionId = oldSubscriber,
                ToPlanId = h.PlanId,
                ToPlanCode = "LC_M_02",
                ToWorkerCount = 2,
                ToBillingCycle = BillingCycle.Monthly,
                ToTilopayRecurringPlanId = 7102,
                Estado = PlanChangeIntentState.Applied,
                OldProviderCancellation = ProviderCancellationState.PendingManualCancellation,
                NewProviderSubscriptionId = newSubscriber,
                AppliedAtUtc = DateTime.UtcNow
            });
        }

        /// <summary>Cliente admin fake, programable y con contadores de invocación.</summary>
        // ── Renovación / regularización vía url_renew: repeat_payment_success SIN pending local ──
        [Fact]
        public async Task PaymentSuccessWithoutPending_CorrelatesExistingSubscription_ReactivatesAndClosesIncident()
        {
            using var h = await Harness.CreateAsync(3); // LC_M_03 → recurringPlanId 7103

            // Estado del tenant ANTES del success: base Morosa + gracia + incidente abierto,
            // suscriptor ya vivo en TiloPay. NO se abre checkout (no hay pending): es un url_renew.
            var subId = Guid.NewGuid();
            h.Db.Users.Add(new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = h.TenantId,
                Email = Email,
                UserName = Email,
                NormalizedEmail = Email.ToUpperInvariant(),
                NormalizedUserName = Email.ToUpperInvariant()
            });
            h.Db.Suscripciones.Add(new Suscripcion
            {
                Id = subId,
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                CodigoPlan = h.Data.Code,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ProviderSubscriptionId = "900370",
                Estado = EstadoSuscripcion.Morosa,
                PaymentRecoveryStatus = "GraceActive",
                FechaInicio = DateTime.UtcNow.AddDays(-31),
                FechaFin = DateTime.UtcNow.AddDays(-1),
                FechaProximoCobroUtc = DateTime.UtcNow.AddDays(-1),
                FechaFinGraciaUtc = DateTime.UtcNow.AddDays(3),
                FechaUltimaActualizacionUtc = DateTime.UtcNow.AddHours(-1)
            });
            h.Db.SubscriptionPaymentIncidents.Add(new SubscriptionPaymentIncident
            {
                Id = Guid.NewGuid(),
                TenantId = h.TenantId,
                Scope = PaymentIncidentScope.BasePlan,
                SuscripcionId = subId,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                PlanCode = h.Data.Code,
                Status = PaymentIncidentStatus.Open,
                FailureDetectedAtUtc = DateTime.UtcNow.AddHours(-1),
                GraceEndsAtUtc = DateTime.UtcNow.AddDays(3),
                FailureCount = 1,
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
                UpdatedAtUtc = DateTime.UtcNow.AddHours(-1)
            });
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();

            // El proveedor confirma un suscriptor para (plan, email).
            h.Admin.ResolutionResult = SubscriberResolutionResult.Found(
                new TilopaySubscriber { SubscriberId = "900370", Email = Email, Status = "Active" }, 1);

            // Llega el success de url_renew SIN pending (no se llamó StartCheckoutAsync).
            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "5483055");

            // CAUSA RAÍZ: antes esto quedaba SinRelacion. Ahora correlaciona la suscripción existente
            // (plan/email + verificación del proveedor), crea el intento y ACTIVA/renueva.
            var sub = await h.Db.Suscripciones.IgnoreQueryFilters().SingleAsync(s => s.Id == subId);
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);

            // Se creó/confirmó un pago para ese transactionId (ya no es un success huérfano).
            var confirmedPayment = await h.Db.PagosSuscripcion.IgnoreQueryFilters()
                .SingleOrDefaultAsync(p => p.ProviderTransactionId == "5483055" && p.Estado == EstadoPagoProveedor.Confirmado);
            Assert.NotNull(confirmedPayment);

            // El evento NO quedó SinRelacion: se procesó correlacionando la suscripción existente.
            var evento = await h.Db.EventosPago.IgnoreQueryFilters()
                .OrderByDescending(e => e.FechaRecepcionUtc)
                .FirstAsync();
            Assert.True(evento.Procesado);
            Assert.NotEqual("SinRelacion", evento.EstadoProcesamiento);
        }

        [Fact]
        public async Task PaymentSuccessWithoutPending_ReplaySamePayment_IsIdempotent_NoDoubleExtend()
        {
            using var h = await Harness.CreateAsync(3);

            h.Db.Users.Add(new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = h.TenantId,
                Email = Email,
                UserName = Email,
                NormalizedEmail = Email.ToUpperInvariant(),
                NormalizedUserName = Email.ToUpperInvariant()
            });
            var subId = Guid.NewGuid();
            h.Db.Suscripciones.Add(new Suscripcion
            {
                Id = subId,
                TenantId = h.TenantId,
                PlanId = h.PlanId,
                CodigoPlan = h.Data.Code,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = h.Data.RecurringPlanId,
                ProviderSubscriptionId = "900370",
                Estado = EstadoSuscripcion.Morosa,
                PaymentRecoveryStatus = "GraceActive",
                FechaInicio = DateTime.UtcNow.AddDays(-31),
                FechaFin = DateTime.UtcNow.AddDays(-1),
                FechaProximoCobroUtc = DateTime.UtcNow.AddDays(-1),
                FechaUltimaActualizacionUtc = DateTime.UtcNow.AddHours(-1)
            });
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();

            h.Admin.ResolutionResult = SubscriberResolutionResult.Found(
                new TilopaySubscriber { SubscriberId = "900370", Email = Email, Status = "Active" }, 1);

            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "5483055");
            var afterFirst = await h.Db.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == subId);
            var fechaFinFirst = afterFirst.FechaFin;

            // Replay del MISMO pago (mismo transactionId): idempotente, no extiende dos veces.
            await h.ProcessWebhookAsync("repeat_payment_success", amount: h.Data.Charge, transactionId: "5483055");
            var afterReplay = await h.Db.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == subId);

            Assert.Equal(fechaFinFirst, afterReplay.FechaFin);
            Assert.Equal(EstadoSuscripcion.Activa, afterReplay.Estado);

            var confirmedPayments = await h.Db.PagosSuscripcion.IgnoreQueryFilters()
                .CountAsync(p => p.ProviderTransactionId == "5483055" && p.Estado == EstadoPagoProveedor.Confirmado);
            Assert.Equal(1, confirmedPayments); // un solo pago confirmado, no duplicado
        }

        private sealed class FakeAdmin : ITilopayRepeatAdminService
        {
            public bool IsEnabled { get; set; } = true;
            public SubscriberResolutionResult ResolutionResult { get; set; } = SubscriberResolutionResult.NotFound();
            public TilopayAdminOperationResult RecurrentUrl { get; set; } = TilopayAdminOperationResult.Ok("ok", "https://tp.cr/l/recurrent-link");
            public TilopayAdminOperationResult DeleteResult { get; set; } = TilopayAdminOperationResult.Ok("ok");
            public TilopayAdminOperationResult EditResult { get; set; } = TilopayAdminOperationResult.Ok("edited");
            /// <summary>Lo que devuelve getSuscriptorRepeat (usado por la verificación de idempotencia).</summary>
            public List<TilopaySubscriber> GetSubscribers { get; } = new();
            public List<string> DeletedSubscriberIds { get; } = new();

            /// <summary>
            /// Suscriptores del plan DESTINO. Si se setea, el pre-check evalúa esta lista con las
            /// reglas reales; si no, el veredicto se deriva de <see cref="ResolutionResult"/>.
            /// </summary>
            public List<TilopaySubscriber>? TargetSubscribers { get; set; }

            /// <summary>Cuántas veces se pidió recurrentUrl: un suscriptor Delete NO debe generarla.</summary>
            public int RecurrentUrlCalls { get; private set; }

            public Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(int tilopayPlanId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<TilopaySubscriber>>(GetSubscribers.ToList());

            public Task<SubscriberResolutionResult> ResolveSubscriberAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(ResolutionResult);

            /// <summary>
            /// Deriva el veredicto del mismo <see cref="ResolutionResult"/> que ya programan los
            /// tests, pero clasificando con las reglas REALES (TargetSubscriberAssessment.FromMatches):
            /// el fake no inventa una tabla propia, así que si la regla de estado cambia, estos
            /// tests lo notan.
            /// </summary>
            public Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                TargetSubscribers is not null
                    ? Task.FromResult(TargetSubscriberAssessment.FromMatches(TargetSubscribers, tilopayPlanId))
                    : Task.FromResult(ResolutionResult.Status switch
                {
                    SubscriberResolutionStatus.Error =>
                        TargetSubscriberAssessment.Error(ResolutionResult.Detail ?? "error simulado"),

                    SubscriberResolutionStatus.Found =>
                        TargetSubscriberAssessment.FromMatches(new[] { ResolutionResult.Subscriber! }, tilopayPlanId),

                    // Ambiguo = varios ACTIVOS por email (así lo entiende el pre-check).
                    SubscriberResolutionStatus.Ambiguous =>
                        TargetSubscriberAssessment.FromMatches(
                            Enumerable.Range(0, Math.Max(2, ResolutionResult.MatchCount))
                                .Select(i => new TilopaySubscriber { SubscriberId = $"AMB-{i}", Email = email, Status = "Active" })
                                .ToList(),
                            tilopayPlanId),

                    _ => TargetSubscriberAssessment.FromMatches(Array.Empty<TilopaySubscriber>(), tilopayPlanId)
                });

            public Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default)
            {
                RecurrentUrlCalls++;
                return Task.FromResult(RecurrentUrl);
            }

            public Task<TilopayAdminOperationResult> PauseSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("paused"));

            public Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("reactivated"));

            public Task<TilopayAdminOperationResult> DeleteSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default)
            {
                DeletedSubscriberIds.Add(subscriberId);
                return Task.FromResult(DeleteResult);
            }

            public Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(string subscriberId, TilopaySubscriberStatus status, CancellationToken cancellationToken = default) =>
                Task.FromResult(EditResult);
        }

        private sealed class FakeProvider : IPaymentProvider
        {
            public PaymentProviderType ProviderType => PaymentProviderType.Tilopay;
            public PaymentProviderWebhookData WebhookData { get; set; } = new() { ProviderType = PaymentProviderType.Tilopay, IsRecurring = true };

            public Task<PaymentCheckoutResult> CreateCheckoutAsync(PaymentCheckoutRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new PaymentCheckoutResult { ProviderType = PaymentProviderType.Tilopay, RedirectUrl = request.SuccessUrl });

            public PaymentProviderWebhookData ParseWebhook(string payload) => WebhookData;

            public Task<PaymentVerificationResult> VerifyPaymentAsync(PaymentVerificationRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new PaymentVerificationResult { ProviderType = PaymentProviderType.Tilopay, Exists = true, IsSuccess = true, Reference = request.Reference });
        }

        private sealed class Harness : IDisposable
        {
            private readonly IDisposable _connection;
            public ApplicationDbContext Db { get; private init; } = null!;
            public FakeAdmin Admin { get; } = new();
            public FakeProvider Provider { get; } = new();
            public SaaSPaymentService Payments { get; private set; } = null!;
            public BillingReconciliationService Reconciliation { get; private set; } = null!;
            public ProviderSubscriptionManager ProviderManager { get; private set; } = null!;
            public PlanChangeLateApplicationService LateApplication { get; private set; } = null!;
            public BillingHealthService Health { get; private set; } = null!;
            public Guid TenantId { get; } = Guid.NewGuid();
            public Guid PlanId { get; private set; }
            public CalculatorPlanData Data { get; private set; } = null!;

            private string _reference = string.Empty;

            private Harness(IDisposable connection) => _connection = connection;

            public static async Task<Harness> CreateAsync(int workers)
            {
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
                var h = new Harness(connection) { Db = context };
                h.Data = CalculatorCatalog.Find(workers, BillingCycle.Monthly);

                var repeatOptions = CalculatorCatalog.BuildRepeatOptions();
                var adminOptions = Options.Create(new OpcionesTilopayRepeatAdmin { Enabled = true, BlockDuplicateCheckout = true, AutoCancelOldSubscriberOnUpgrade = true });
                // Reloj anclado a "ahora" para que las marcas de auditoría caigan dentro de las
                // ventanas de tiempo reales (UtcNow) que usa BillingHealthService. Kind=Unspecified
                // porque FixedBusinessDateTimeProvider aplica un offset -6 al DateTime recibido.
                var clock = new FixedBusinessDateTimeProvider(
                    DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
                var tenantAccessor = new TenantExecutionContextAccessor();
                var cache = new MemoryCache(new MemoryCacheOptions());
                var subscriptionService = new SuscripcionService(context, cache, new TenantCommercialAccessCache(cache), clock, Options.Create(repeatOptions), NullLogger<SuscripcionService>.Instance);

                var resolutionService = new SubscriberResolutionService(context, h.Admin, tenantAccessor, clock, NullLogger<SubscriberResolutionService>.Instance);
                h.ProviderManager = new ProviderSubscriptionManager(context, h.Admin, tenantAccessor, clock, adminOptions, NullLogger<ProviderSubscriptionManager>.Instance);

                var planChangeService = new PlanChangeService(context, NullLogger<PlanChangeService>.Instance);

                h.LateApplication = new PlanChangeLateApplicationService(
                    context,
                    subscriptionService,
                    planChangeService,
                    tenantAccessor,
                    clock,
                    h.Admin,
                    NullLogger<PlanChangeLateApplicationService>.Instance,
                    h.ProviderManager);

                h.Payments = new SaaSPaymentService(
                    context,
                    new PaymentProviderResolver(new IPaymentProvider[] { h.Provider }),
                    subscriptionService,
                    tenantAccessor,
                    Options.Create(new OpcionesPago { ProveedorPredeterminado = PaymentProviderType.Tilopay }),
                    Options.Create(new OpcionesTilopay { MerchantId = "m", WebhookAccessToken = "t" }),
                    Options.Create(repeatOptions),
                    NullLogger<SaaSPaymentService>.Instance,
                    environment: null,
                    planChangeService: planChangeService,
                    subscriberResolutionService: resolutionService,
                    tilopayRepeatAdminService: h.Admin,
                    tilopayRepeatAdminOptions: adminOptions,
                    providerSubscriptionManager: h.ProviderManager,
                    planChangeLateApplicationService: h.LateApplication);

                h.Reconciliation = new BillingReconciliationService(
                    context,
                    subscriptionService,
                    tenantAccessor,
                    clock,
                    Options.Create(repeatOptions),
                    Options.Create(new BillingReconciliationOptions()),
                    NullLogger<BillingReconciliationService>.Instance,
                    resolutionService,
                    adminOptions,
                    h.ProviderManager,
                    h.LateApplication);

                h.Health = new BillingHealthService(context, subscriptionService);

                context.Tenants.Add(new Tenant { Id = h.TenantId, Nombre = "Tenant Sub", Activo = true });
                var plan = new Plan
                {
                    Id = Guid.NewGuid(),
                    Codigo = h.Data.Code,
                    Nombre = "Plan sub",
                    PrecioMensual = h.Data.Charge,
                    MonthlyEquivalentAmount = h.Data.MonthlyEquivalent,
                    BillingCycle = h.Data.Cycle,
                    Moneda = "CRC",
                    MaxFuncionarios = h.Data.Workers,
                    Activo = true
                };
                context.Planes.Add(plan);
                h.PlanId = plan.Id;
                await context.SaveChangesAsync();

                return h;
            }

            public async Task StartCheckoutAsync()
            {
                var checkout = await Payments.CreateRecurringCheckoutAsync(TenantId, PlanId, "Owner", Email);
                _reference = checkout.ProviderReference ?? string.Empty;
            }

            public Task ProcessWebhookAsync(string eventType, decimal? amount, string transactionId)
            {
                Provider.WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventType = eventType == "repeat_registration" ? "tilopay.repeat.notification" : "tilopay.repeat.notification",
                    Reference = _reference,
                    RecurringPlanId = Data.RecurringPlanId,
                    CustomerEmail = Email,
                    Amount = amount,
                    Currency = "CRC",
                    ProviderTransactionId = transactionId,
                    IsRecurring = true
                };

                return Payments.ProcessTilopayWebhookAsync("{\"x\":1}", $"corr-{transactionId}", eventType);
            }

            public Task<int> CountAuditAsync(string action) =>
                Db.PlatformAuditLogs.CountAsync(l => l.Action == action);

            /// <summary>Enlaza intent ↔ pago, como hace el controller con el lc_ref tras abrir el checkout.</summary>
            public async Task LinkIntentAsync(Guid intentId, Guid paymentId)
            {
                await Db.SaveChangesAsync(); // vuelca lo que sembró el test antes de consultar

                var intent = await Db.PlanChangeIntents.IgnoreQueryFilters().SingleAsync(i => i.Id == intentId);
                intent.PagoSuscripcionId = paymentId;
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public void Dispose()
            {
                Db.Dispose();
                _connection.Dispose();
            }
        }
    }
}
