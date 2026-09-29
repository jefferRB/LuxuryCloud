using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Payments;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class RecurringCheckoutConfigurationTests
    {
        [Fact]
        public async Task CreateRecurringCheckoutAsync_ShouldRequireHostedLinkKeyForConfiguredPlan()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant A",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.Basic,
                Nombre = "Basico",
                PrecioMensual = 8000,
                Moneda = "CRC",
                Activo = true
            });

            await context.SaveChangesAsync();

            var service = CreatePaymentService(
                context,
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    UseRecurringCheckoutForPublicPlans = true,
                    Basic = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7001,
                        Code = PlanCodes.Basic
                    }
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateRecurringCheckoutAsync(
                    tenantId,
                    planId,
                    "Owner",
                    "owner@test.local"));

            Assert.Contains("TilopayRepeat:Basic:CheckoutUrl", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CreateRecurringCheckoutAsync_ShouldAppendCorrelationToHostedLink()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant A",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var service = CreatePaymentService(
                context,
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    EnableTestRecurringPlan = true,
                    TestRecurring = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7099,
                        Code = PlanCodes.TestRecurring,
                        CheckoutUrl = "https://tp.cr/l/test-link?plan=7099",
                        IsValidation = true
                    }
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                });

            var result = await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            Assert.StartsWith("https://tp.cr/l/test-link?plan=7099&", result.RedirectUrl, StringComparison.Ordinal);
            var uri = new Uri(result.RedirectUrl, UriKind.Absolute);
            var query = QueryHelpers.ParseQuery(uri.Query);

            Assert.Equal("7099", query["plan"].ToString());
            Assert.True(query.ContainsKey("lc_ref"));
            Assert.Equal(PlanCodes.TestRecurring, query["lc_plan"].ToString());
            Assert.Equal("owner@test.local", query["lc_email"].ToString());
        }

        [Fact]
        public async Task CreateRecurringCheckoutAsync_ShouldCreatePendingSubscriptionWithoutGrantingAccess()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Pending",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var service = CreatePaymentService(
                context,
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    EnableTestRecurringPlan = true,
                    TestRecurring = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7099,
                        Code = PlanCodes.TestRecurring,
                        MonthlyPrice = 1000,
                        Currency = "CRC",
                        MaxFuncionarios = 1,
                        CheckoutUrl = "https://tp.cr/l/test-link",
                        IsValidation = true
                    }
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                });

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var subscriptionService = CreateSubscriptionService(context, new TilopayRepeatOptions());

            Assert.Equal(EstadoSuscripcion.Pendiente, suscripcion.Estado);
            Assert.False(subscriptionService.CanAccessApp(suscripcion));
        }

        [Fact]
        public async Task CreateRecurringCheckoutAsync_WithExistingActiveSubscription_ShouldNotReplacePlanUntilWebhookApproval()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var currentPlanId = Guid.NewGuid();
            var targetPlanId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Active",
                Activo = true
            });

            context.Planes.AddRange(
                new Plan
                {
                    Id = currentPlanId,
                    Codigo = PlanCodes.Basic,
                    Nombre = "Basico",
                    PrecioMensual = 8000,
                    Moneda = "CRC",
                    MaxFuncionarios = 1,
                    Activo = true
                },
                new Plan
                {
                    Id = targetPlanId,
                    Codigo = PlanCodes.Pro,
                    Nombre = "Pro",
                    PrecioMensual = 20000,
                    Moneda = "CRC",
                    MaxFuncionarios = 3,
                    Activo = true
                });

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = currentPlanId,
                CodigoPlan = PlanCodes.Basic,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                FechaInicio = DateTime.UtcNow.AddDays(-3),
                FechaFin = DateTime.UtcNow.AddDays(27),
                FechaUltimaActualizacionUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var service = CreatePaymentService(
                context,
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    UseRecurringCheckoutForPublicPlans = true,
                    Pro = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7002,
                        Code = PlanCodes.Pro,
                        MonthlyPrice = 20000,
                        Currency = "CRC",
                        MaxFuncionarios = 3,
                        CheckoutUrl = "https://tp.cr/l/pro-link"
                    }
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                });

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                targetPlanId,
                "Owner",
                "owner@test.local");

            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            Assert.Equal(currentPlanId, suscripcion.PlanId);
            Assert.Equal(PlanCodes.Basic, suscripcion.CodigoPlan);
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Equal(targetPlanId, intento.PlanId);
            Assert.Equal(EstadoPagoProveedor.Pendiente, intento.Estado);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldMarkRecurringWebhookForManualReviewWhenAmountDoesNotMatchExpectedFirstCharge()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Test",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var repeatOptions = new TilopayRepeatOptions
            {
                Enabled = true,
                UseHostedLinks = true,
                EnableTestRecurringPlan = true,
                TestRecurring = new TilopayRepeatPlanOption
                {
                    TilopayPlanId = 7099,
                    Code = PlanCodes.TestRecurring,
                    MonthlyPrice = 1000,
                    Currency = "CRC",
                    MaxFuncionarios = 1,
                    CheckoutUrl = "https://tp.cr/l/test-link",
                    IsValidation = true
                }
            };

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                repeatOptions,
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            var checkout = await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventId = "evt-repeat-amount-mismatch",
                EventType = "tilopay.repeat.notification",
                Reference = checkout.CorrelationId ?? checkout.ProviderReference ?? string.Empty,
                RecurringPlanId = 7099,
                ProviderSubscriberId = "subscriber-7099",
                ProviderTransactionId = "tx-repeat-2000",
                CustomerEmail = "owner@test.local",
                StatusCode = "1",
                StatusDescription = "Approved",
                Amount = 2000m,
                Currency = "CRC",
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync("{}", "corr-amount");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            Assert.False(result.IsProcessed);
            Assert.Equal("PendingManualReview", evento.EstadoProcesamiento);
            Assert.Contains("Monto por pago inicial debe ser 0.00", evento.Error ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(EstadoSuscripcion.Pendiente, suscripcion.Estado);
            Assert.NotEqual(EstadoPagoProveedor.Confirmado, intento.Estado);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldActivateTestRecurringWhenWebhookUsesPlanCodeAndCorrelationToken()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Test Approved",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var repeatOptions = new TilopayRepeatOptions
            {
                Enabled = true,
                UseHostedLinks = true,
                EnableTestRecurringPlan = true,
                TestRecurring = new TilopayRepeatPlanOption
                {
                    TilopayPlanId = 7099,
                    Code = PlanCodes.TestRecurring,
                    MonthlyPrice = 1000,
                    Currency = "CRC",
                    MaxFuncionarios = 1,
                    CheckoutUrl = "https://tp.cr/l/test-link",
                    IsValidation = true
                }
            };

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                repeatOptions,
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            var checkout = await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventId = "evt-repeat-test-approved",
                EventType = "tilopay.repeat.notification",
                Reference = checkout.CorrelationId ?? checkout.ProviderReference ?? string.Empty,
                PlanCode = PlanCodes.TestRecurring,
                ProviderSubscriberId = "subscriber-7099",
                ProviderTransactionId = "tx-repeat-1000",
                CustomerEmail = "owner@test.local",
                StatusCode = "1",
                StatusDescription = "Approved",
                Amount = 1000m,
                Currency = "CRC",
                IsRecurring = false
            };

            var result = await service.ProcessTilopayWebhookAsync("{}", "corr-approved");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            var subscriptionService = CreateSubscriptionService(context, repeatOptions);

            Assert.True(result.IsProcessed);
            Assert.Equal(EstadoPagoProveedor.Confirmado, result.EstadoPago);
            Assert.True(evento.Procesado);
            Assert.Equal("Procesado", evento.EstadoProcesamiento);
            Assert.Equal(7099, evento.TilopayRecurringPlanId);
            Assert.Equal(EstadoPagoProveedor.Confirmado, intento.Estado);
            Assert.Equal(7099, intento.TilopayRecurringPlanId);
            Assert.Equal("subscriber-7099", intento.ProviderSubscriberId);
            Assert.Equal("tx-repeat-1000", intento.ProviderTransactionId);
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Equal("subscriber-7099", suscripcion.ProviderSubscriptionId);
            Assert.Equal("tx-repeat-1000", suscripcion.ProviderTransactionId);
            Assert.NotNull(suscripcion.FechaFin);
            Assert.Equal(suscripcion.FechaFin, suscripcion.FechaProximoCobroUtc);
            Assert.True(subscriptionService.CanAccessApp(suscripcion));
        }

        [Fact]
        public async Task ApproveRecurringPaymentAsync_ShouldActivatePendingSubscriptionAndPersistProviderTransaction()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Manual Approval",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var repeatOptions = BuildTestRecurringOptions();
            var service = CreatePaymentService(
                context,
                repeatOptions,
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                });

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            var result = await service.ApproveRecurringPaymentAsync(
                new RecurringPaymentApprovalRequest
                {
                    PaymentId = pending.Id,
                    ProviderTransactionId = "TYP-APPROVED-1000",
                    ProviderSubscriberId = "subscriber-manual-1000",
                    ApprovedAmount = 1000m,
                    Currency = "CRC",
                    Observation = "Aprobado manualmente contra dashboard sandbox."
                });

            var payment = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            var subscription = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var manualEvent = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var subscriptionService = CreateSubscriptionService(context, repeatOptions);

            Assert.Equal(EstadoPagoProveedor.Confirmado, result.PaymentStatus);
            Assert.Equal(EstadoSuscripcion.Activa, result.SubscriptionStatus);
            Assert.Equal(EstadoPagoProveedor.Confirmado, payment.Estado);
            Assert.Equal("TYP-APPROVED-1000", payment.ProviderTransactionId);
            Assert.Equal("subscriber-manual-1000", payment.ProviderSubscriberId);
            Assert.Equal(EstadoSuscripcion.Activa, subscription.Estado);
            Assert.Equal("TYP-APPROVED-1000", subscription.ProviderTransactionId);
            Assert.Equal("subscriber-manual-1000", subscription.ProviderSubscriptionId);
            Assert.Equal(subscription.FechaFin, subscription.FechaProximoCobroUtc);
            Assert.Equal(1, subscription.MaxFuncionarios);
            Assert.True(subscriptionService.CanAccessApp(subscription));
            Assert.Equal("tilopay.repeat.manual.approval", manualEvent.Tipo);
            Assert.Equal("TYP-APPROVED-1000", manualEvent.ProviderTransactionId);
        }

        [Fact]
        public async Task ApproveRecurringPaymentAsync_ShouldRejectUnexpectedAmount()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Wrong Amount",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var service = CreatePaymentService(
                context,
                BuildTestRecurringOptions(),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                });

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApproveRecurringPaymentAsync(
                    new RecurringPaymentApprovalRequest
                    {
                        PaymentId = pending.Id,
                        ProviderTransactionId = "TYP-WRONG-AMOUNT",
                        ApprovedAmount = 8000m,
                        Currency = "CRC",
                        Observation = "Monto no coincide."
                    }));

            Assert.Contains("Monto aprobado", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ApproveRecurringPaymentAsync_ShouldRejectUnexpectedCurrency()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Wrong Currency",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var service = CreatePaymentService(
                context,
                BuildTestRecurringOptions(),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                });

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApproveRecurringPaymentAsync(
                    new RecurringPaymentApprovalRequest
                    {
                        PaymentId = pending.Id,
                        ProviderTransactionId = "TYP-WRONG-CURRENCY",
                        ApprovedAmount = 1000m,
                        Currency = "USD",
                        Observation = "Moneda no coincide."
                    }));

            Assert.Contains("Moneda recibida", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ApproveRecurringPaymentAsync_ShouldRejectAlreadyApprovedPayment()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Confirmed",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var service = CreatePaymentService(
                context,
                BuildTestRecurringOptions(),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                });

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            await service.ApproveRecurringPaymentAsync(
                new RecurringPaymentApprovalRequest
                {
                    PaymentId = pending.Id,
                    ProviderTransactionId = "TYP-FIRST-APPROVAL",
                    ApprovedAmount = 1000m,
                    Currency = "CRC",
                    Observation = "Primera aprobacion."
                });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApproveRecurringPaymentAsync(
                    new RecurringPaymentApprovalRequest
                    {
                        PaymentId = pending.Id,
                        ProviderTransactionId = "TYP-SECOND-APPROVAL",
                        ApprovedAmount = 1000m,
                        Currency = "CRC",
                        Observation = "Segunda aprobacion."
                    }));

            Assert.Contains("ya fue aprobado", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CreateRecurringCheckoutAsync_ShouldReuseRecentPendingRecurringAttemptForSameTenantEmailAndPlan()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Duplicated Pending",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var service = CreatePaymentService(
                context,
                BuildTestRecurringOptions(),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                });

            var first = await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var second = await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var attempts = await context.PagosSuscripcion
                .IgnoreQueryFilters()
                .OrderBy(payment => payment.FechaCreacionUtc)
                .ToListAsync();

            var attempt = Assert.Single(attempts);
            Assert.Equal(EstadoPagoProveedor.Pendiente, attempt.Estado);
            Assert.Equal(first.RedirectUrl, second.RedirectUrl);
            Assert.Equal(first.CorrelationId, second.CorrelationId);
        }

        [Fact]
        public void TilopayRepeatPlanOption_ShouldExposeExpectedFirstChargeAmountAsMonthlyPrice()
        {
            var plan = new TilopayRepeatPlanOption
            {
                Code = PlanCodes.WhatsApp400,
                MonthlyPrice = 6000m
            };

            Assert.Equal(6000m, plan.ExpectedFirstChargeAmount);
        }

        [Fact]
        public void TilopayParseWebhook_ShouldTreatManagedPlanCodeAsRecurring()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var provider = new TilopayService(
                new HttpClient(),
                cache,
                Options.Create(new OpcionesTilopay()),
                NullLogger<TilopayService>.Instance);

            var webhook = provider.ParseWebhook(
                """
                {
                  "lc_plan": "TEST_RECURRING",
                  "lc_ref": "ABC123",
                  "code": "1",
                  "codeDescription": "Approved",
                  "amount": 1000,
                  "currency": "CRC"
                }
                """);

            Assert.True(webhook.IsRecurring);
            Assert.Equal(PlanCodes.TestRecurring, webhook.PlanCode);
            Assert.Equal("ABC123", webhook.Reference);
            Assert.Equal(1000m, webhook.Amount);
            Assert.Equal("CRC", webhook.Currency);
        }

        [Fact]
        public void TilopayParseWebhook_ShouldParseOfficialRepeatPaymentSuccessPayload()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var provider = new TilopayService(
                new HttpClient(),
                cache,
                Options.Create(new OpcionesTilopay()),
                NullLogger<TilopayService>.Instance);

            var webhook = provider.ParseWebhook(
                """
                {
                  "id_plan": 7099,
                  "email": "owner@test.local",
                  "amount": 1000,
                  "auth": "123456",
                  "orderNumber": "PRE123456"
                }
                """);

            Assert.True(webhook.IsRecurring);
            Assert.Equal(7099, webhook.RecurringPlanId);
            Assert.Equal("owner@test.local", webhook.CustomerEmail);
            Assert.Equal(1000m, webhook.Amount);
            Assert.Equal("123456", webhook.AuthorizationCode);
            Assert.Equal("PRE123456", webhook.ProviderOrderNumber);
        }

        [Fact]
        public void TilopayParseWebhook_ShouldParseOfficialRepeatRegistrationPayload()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var provider = new TilopayService(
                new HttpClient(),
                cache,
                Options.Create(new OpcionesTilopay()),
                NullLogger<TilopayService>.Instance);

            var webhook = provider.ParseWebhook(
                """
                {
                  "id_plan": 7099,
                  "email": "owner@test.local",
                  "modality": "Plan mensual",
                  "amount": 1000,
                  "frequency": "monthly",
                  "coupon": "TESTCODE",
                  "free_trial": 1,
                  "next_payment_date": "2026-06-26"
                }
                """);

            Assert.True(webhook.IsRecurring);
            Assert.Equal(7099, webhook.RecurringPlanId);
            Assert.Equal("owner@test.local", webhook.CustomerEmail);
            Assert.Equal("Plan mensual", webhook.RecurringModality);
            Assert.Equal("monthly", webhook.RecurringFrequency);
            Assert.Equal("TESTCODE", webhook.CouponCode);
            Assert.True(webhook.HasFreeTrial);
            Assert.Equal(new DateTime(2026, 6, 26, 0, 0, 0, DateTimeKind.Utc), webhook.NextBillingDateUtc);
        }

        [Fact]
        public void TilopayParseWebhook_ShouldParseOfficialRepeatPaymentFailedPayload()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var provider = new TilopayService(
                new HttpClient(),
                cache,
                Options.Create(new OpcionesTilopay()),
                NullLogger<TilopayService>.Instance);

            var webhook = provider.ParseWebhook(
                """
                {
                  "id_plan": 7099,
                  "email": "owner@test.local",
                  "amount": 1000
                }
                """);

            Assert.True(webhook.IsRecurring);
            Assert.Equal(7099, webhook.RecurringPlanId);
            Assert.Equal("owner@test.local", webhook.CustomerEmail);
            Assert.Equal(1000m, webhook.Amount);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldActivatePendingTestRecurringFromOfficialRepeatPaymentSuccessEvent()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var nextPaymentDateUtc = new DateTime(2026, 6, 26, 0, 0, 0, DateTimeKind.Utc);

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Official Success",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var repeatOptions = BuildTestRecurringOptions();
            var service = CreatePaymentService(
                context,
                repeatOptions,
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7099,
                CustomerEmail = "owner@test.local",
                Amount = 1000m,
                ProviderSubscriberId = "subscriber-7099",
                ProviderOrderNumber = "PRE123456",
                AuthorizationCode = "123456",
                NextBillingDateUtc = nextPaymentDateUtc,
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7099,
                  "email": "owner@test.local",
                  "amount": 1000,
                  "auth": "123456",
                  "orderNumber": "PRE123456"
                }
                """,
                "corr-repeat-success",
                "repeat_payment_success");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            Assert.True(result.IsProcessed);
            Assert.Equal(EstadoPagoProveedor.Confirmado, result.EstadoPago);
            Assert.Equal("Procesado", evento.EstadoProcesamiento);
            Assert.Equal(7099, evento.TilopayRecurringPlanId);
            Assert.Equal("PRE123456", evento.ProviderTransactionId);
            Assert.Equal(EstadoPagoProveedor.Confirmado, intento.Estado);
            Assert.Equal("PRE123456", intento.ProviderTransactionId);
            Assert.Equal("123456", intento.ProviderAuthorizationCode);
            Assert.Equal("subscriber-7099", intento.ProviderSubscriberId);
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Equal("subscriber-7099", suscripcion.ProviderSubscriptionId);
            Assert.Equal("PRE123456", suscripcion.ProviderTransactionId);
            Assert.Equal(nextPaymentDateUtc, suscripcion.FechaProximoCobroUtc);
            Assert.Equal(1, suscripcion.MaxFuncionarios);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldIgnoreDuplicateRecurringPaymentTransactionWithDifferentEventType()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            const string providerOrderNumber = "PRE-DUP-RECURRING-001";

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Duplicate Recurring",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var repeatOptions = BuildTestRecurringOptions();
            var service = CreatePaymentService(
                context,
                repeatOptions,
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7099,
                CustomerEmail = "owner@test.local",
                Amount = 1000m,
                ProviderSubscriberId = "subscriber-dup-7099",
                ProviderOrderNumber = providerOrderNumber,
                AuthorizationCode = "123456",
                IsRecurring = true
            };

            var firstResult = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7099,
                  "email": "owner@test.local",
                  "amount": 1000,
                  "auth": "123456",
                  "orderNumber": "PRE-DUP-RECURRING-001"
                }
                """,
                "corr-repeat-duplicate-first",
                "repeat_payment_success");

            var firstPeriodEndUtc = context.Suscripciones
                .IgnoreQueryFilters()
                .Single()
                .FechaFin;

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7099,
                CustomerEmail = "owner@test.local",
                Amount = 1000m,
                ProviderSubscriberId = "subscriber-dup-7099",
                ProviderOrderNumber = providerOrderNumber,
                AuthorizationCode = "123456",
                IsRecurring = true
            };

            var duplicateResult = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7099,
                  "email": "owner@test.local",
                  "amount": 1000,
                  "auth": "123456",
                  "orderNumber": "PRE-DUP-RECURRING-001"
                }
                """,
                "corr-repeat-duplicate-second",
                "repeat_payment_paid");

            var subscription = context.Suscripciones.IgnoreQueryFilters().Single();
            var payments = context.PagosSuscripcion.IgnoreQueryFilters().ToList();
            var events = context.EventosPago.IgnoreQueryFilters().ToList();

            Assert.True(firstResult.IsProcessed);
            Assert.True(duplicateResult.IsDuplicate);
            Assert.True(duplicateResult.IsProcessed);
            Assert.Equal(firstPeriodEndUtc, subscription.FechaFin);
            Assert.Single(payments);
            Assert.Equal(2, events.Count);
            Assert.All(events, evento => Assert.True(evento.Procesado));
        }

        [Theory]
        [InlineData(PlanCodes.Basic, "Basico", 7001, 8000, 1)]
        [InlineData(PlanCodes.Pro, "Pro", 7002, 20000, 3)]
        [InlineData(PlanCodes.Business, "Business", 7003, 35000, 7)]
        public async Task ProcessTilopayWebhookAsync_ShouldActivatePendingPublicRecurringPlanFromOfficialRepeatPaymentSuccessEvent(
            string planCode,
            string planName,
            int recurringPlanId,
            int monthlyPrice,
            int maxFuncionarios)
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var nextPaymentDateUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = $"Tenant {planCode}",
                Activo = true
            });

            context.Planes.Add(CreateRecurringPlan(
                planId,
                planCode,
                planName,
                monthlyPrice,
                maxFuncionarios));

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var repeatOptions = BuildPublicRecurringOptions(
                planCode,
                recurringPlanId,
                monthlyPrice,
                maxFuncionarios,
                $"https://tp.cr/l/{planCode.ToLowerInvariant()}");
            var service = CreatePaymentService(
                context,
                repeatOptions,
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var providerOrderNumber = $"PRE-{recurringPlanId}";
            var providerSubscriberId = $"subscriber-{planCode.ToLowerInvariant()}";

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = recurringPlanId,
                CustomerEmail = "owner@test.local",
                Amount = monthlyPrice,
                ProviderSubscriberId = providerSubscriberId,
                ProviderOrderNumber = providerOrderNumber,
                AuthorizationCode = "123456",
                NextBillingDateUtc = nextPaymentDateUtc,
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                $$"""
                {
                  "id_plan": {{recurringPlanId}},
                  "email": "owner@test.local",
                  "amount": {{monthlyPrice}},
                  "auth": "123456",
                  "orderNumber": "{{providerOrderNumber}}"
                }
                """,
                $"corr-repeat-success-{planCode.ToLowerInvariant()}",
                "repeat_payment_success");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            Assert.True(result.IsProcessed);
            Assert.Equal(EstadoPagoProveedor.Confirmado, result.EstadoPago);
            Assert.Equal("Procesado", evento.EstadoProcesamiento);
            Assert.Equal(recurringPlanId, evento.TilopayRecurringPlanId);
            Assert.Equal(providerOrderNumber, evento.ProviderTransactionId);
            Assert.Equal(EstadoPagoProveedor.Confirmado, intento.Estado);
            Assert.Equal(recurringPlanId, intento.TilopayRecurringPlanId);
            Assert.Equal(providerOrderNumber, intento.ProviderTransactionId);
            Assert.Equal(providerSubscriberId, intento.ProviderSubscriberId);
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Equal(planId, suscripcion.PlanId);
            Assert.Equal(planCode, suscripcion.CodigoPlan);
            Assert.Equal(providerSubscriberId, suscripcion.ProviderSubscriptionId);
            Assert.Equal(providerOrderNumber, suscripcion.ProviderTransactionId);
            Assert.Equal(nextPaymentDateUtc, suscripcion.FechaProximoCobroUtc);
            Assert.Equal(monthlyPrice, suscripcion.PrecioMensual);
            Assert.Equal(maxFuncionarios, suscripcion.MaxFuncionarios);
        }

        [Theory]
        [InlineData(PlanCodes.WhatsApp400, "WhatsApp 400", 7011, 6000, 400, 15)]
        [InlineData(PlanCodes.WhatsApp800, "WhatsApp 800", 7012, 12000, 800, 30)]
        [InlineData(PlanCodes.WhatsApp1200, "WhatsApp 1200", 7013, 18000, 1200, 45)]
        public async Task ProcessTilopayWebhookAsync_ShouldActivateWhatsAppAddonFromOfficialRepeatPaymentSuccessEvent(
            string addonCode,
            string addonName,
            int recurringPlanId,
            int monthlyPrice,
            int monthlyMessageLimit,
            int dailyMessageLimit)
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var basePlanId = Guid.NewGuid();
            var addonPlanId = Guid.NewGuid();
            var nextPaymentDateUtc = new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = $"Tenant {addonCode}",
                Activo = true
            });

            context.Planes.AddRange(
                CreateRecurringPlan(
                    basePlanId,
                    PlanCodes.Pro,
                    "Pro",
                    20000,
                    3),
                CreateWhatsAppAddonPlan(
                    addonPlanId,
                    addonCode,
                    addonName,
                    monthlyPrice,
                    monthlyMessageLimit));

            context.Suscripciones.Add(CreateActiveBaseSubscription(
                tenantId,
                basePlanId,
                PlanCodes.Pro,
                monthlyPrice: 20000m,
                maxFuncionarios: 3,
                providerSubscriptionId: "base-pro-subscriber",
                providerTransactionId: "BASE-PRO-TX"));

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var repeatOptions = BuildAddonRecurringOptions(
                addonCode,
                recurringPlanId,
                monthlyPrice,
                monthlyMessageLimit,
                dailyMessageLimit,
                $"https://tp.cr/l/{addonCode.ToLowerInvariant()}");
            var service = CreatePaymentService(
                context,
                repeatOptions,
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                addonPlanId,
                "Owner",
                "owner@test.local");

            var providerOrderNumber = $"PRE-{recurringPlanId}";
            var providerSubscriberId = $"subscriber-{addonCode.ToLowerInvariant()}";

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = recurringPlanId,
                CustomerEmail = "owner@test.local",
                Amount = monthlyPrice,
                Currency = "CRC",
                ProviderSubscriberId = providerSubscriberId,
                ProviderOrderNumber = providerOrderNumber,
                AuthorizationCode = "123456",
                NextBillingDateUtc = nextPaymentDateUtc,
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                $$"""
                {
                  "id_plan": {{recurringPlanId}},
                  "email": "owner@test.local",
                  "amount": {{monthlyPrice}},
                  "auth": "123456",
                  "orderNumber": "{{providerOrderNumber}}"
                }
                """,
                $"corr-repeat-success-{addonCode.ToLowerInvariant()}",
                "repeat_payment_success");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var baseSubscription = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var addon = await context.TenantSubscriptionAddons.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            Assert.True(result.IsProcessed);
            Assert.Equal(EstadoPagoProveedor.Confirmado, result.EstadoPago);
            Assert.Equal("Procesado", evento.EstadoProcesamiento);
            Assert.Equal(recurringPlanId, evento.TilopayRecurringPlanId);
            Assert.Equal(providerOrderNumber, evento.ProviderTransactionId);

            Assert.Equal(EstadoPagoProveedor.Confirmado, intento.Estado);
            Assert.Equal(providerOrderNumber, intento.ProviderTransactionId);
            Assert.Equal("123456", intento.ProviderAuthorizationCode);
            Assert.Equal(providerSubscriberId, intento.ProviderSubscriberId);

            Assert.Equal(EstadoSuscripcion.Activa, addon.Estado);
            Assert.Equal(addonPlanId, addon.PlanId);
            Assert.Equal(addonCode, addon.AddonCode);
            Assert.Equal(recurringPlanId, addon.TilopayRecurringPlanId);
            Assert.Equal(providerSubscriberId, addon.ProviderSubscriptionId);
            Assert.Equal(providerOrderNumber, addon.ProviderTransactionId);
            Assert.Equal(monthlyPrice, addon.PrecioMensual);
            Assert.Equal(monthlyMessageLimit, addon.MonthlyMessageLimit);
            Assert.Equal(nextPaymentDateUtc, addon.FechaProximoCobroUtc);
            Assert.NotNull(addon.FechaFin);
            // Opción A: la cuota mensual comercial vive en el add-on; el pago recurrente NO crea ni
            // habilita TenantWhatsAppSettings (la integración se configura aparte en /WhatsApp).
            Assert.Equal(monthlyMessageLimit, addon.MonthlyMessageLimit);
            Assert.Empty(context.TenantWhatsAppSettings.IgnoreQueryFilters());
            // dailyMessageLimit sigue siendo el default operativo del add-on (no el paquete mensual).
            Assert.True(dailyMessageLimit > 0);

            Assert.Equal(EstadoSuscripcion.Activa, baseSubscription.Estado);
            Assert.Equal(basePlanId, baseSubscription.PlanId);
            Assert.Equal(PlanCodes.Pro, baseSubscription.CodigoPlan);
            Assert.Equal(3, baseSubscription.MaxFuncionarios);
            Assert.Equal("base-pro-subscriber", baseSubscription.ProviderSubscriptionId);
            Assert.Equal("BASE-PRO-TX", baseSubscription.ProviderTransactionId);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldMarkWhatsAppAddonForManualReviewWhenTenantHasNoActiveBasePlan()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var addonPlanId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Without Base Plan",
                Activo = true
            });

            context.Planes.Add(CreateWhatsAppAddonPlan(
                addonPlanId,
                PlanCodes.WhatsApp400,
                "WhatsApp 400",
                6000,
                400));

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                BuildAddonRecurringOptions(
                    PlanCodes.WhatsApp400,
                    recurringPlanId: 7011,
                    monthlyPrice: 6000,
                    monthlyMessageLimit: 400,
                    dailyMessageLimit: 15,
                    checkoutUrl: "https://tp.cr/l/wa400"),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                addonPlanId,
                "Owner",
                "owner@test.local");

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7011,
                CustomerEmail = "owner@test.local",
                Amount = 6000m,
                Currency = "CRC",
                ProviderSubscriberId = "subscriber-wa400",
                ProviderOrderNumber = "PRE-WA400-NO-BASE",
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7011,
                  "email": "owner@test.local",
                  "amount": 6000,
                  "orderNumber": "PRE-WA400-NO-BASE"
                }
                """,
                "corr-repeat-wa400-no-base",
                "repeat_payment_success");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            Assert.False(result.IsProcessed);
            Assert.Equal("PendingManualReview", evento.EstadoProcesamiento);
            Assert.Contains("plan base activo", evento.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(EstadoPagoProveedor.ManualReview, intento.Estado);
            Assert.Empty(await context.Suscripciones.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await context.TenantSubscriptionAddons.IgnoreQueryFilters().ToListAsync());
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldReplacePreviousWhatsAppAddonOnlyAfterApprovedPayment()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var basePlanId = Guid.NewGuid();
            var currentAddonPlanId = Guid.NewGuid();
            var newAddonPlanId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Replace Addon",
                Activo = true
            });

            context.Planes.AddRange(
                CreateRecurringPlan(
                    basePlanId,
                    PlanCodes.Business,
                    "Business",
                    35000,
                    7),
                CreateWhatsAppAddonPlan(
                    currentAddonPlanId,
                    PlanCodes.WhatsApp400,
                    "WhatsApp 400",
                    6000,
                    400),
                CreateWhatsAppAddonPlan(
                    newAddonPlanId,
                    PlanCodes.WhatsApp800,
                    "WhatsApp 800",
                    12000,
                    800));

            context.Suscripciones.Add(CreateActiveBaseSubscription(
                tenantId,
                basePlanId,
                PlanCodes.Business,
                monthlyPrice: 35000m,
                maxFuncionarios: 7,
                providerSubscriptionId: "base-business-subscriber",
                providerTransactionId: "BASE-BUSINESS-TX"));

            context.TenantSubscriptionAddons.Add(CreateActiveWhatsAppAddon(
                tenantId,
                currentAddonPlanId,
                PlanCodes.WhatsApp400,
                monthlyPrice: 6000m,
                monthlyMessageLimit: 400,
                providerSubscriptionId: "subscriber-wa400-active",
                providerTransactionId: "WA400-ACTIVE-TX"));

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                BuildAddonRecurringOptions(
                    PlanCodes.WhatsApp800,
                    recurringPlanId: 7012,
                    monthlyPrice: 12000,
                    monthlyMessageLimit: 800,
                    dailyMessageLimit: 30,
                    checkoutUrl: "https://tp.cr/l/wa800"),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                newAddonPlanId,
                "Owner",
                "owner@test.local");

            var addonBeforeApproval = await context.TenantSubscriptionAddons.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(currentAddonPlanId, addonBeforeApproval.PlanId);
            Assert.Equal(PlanCodes.WhatsApp400, addonBeforeApproval.AddonCode);
            Assert.Equal(400, addonBeforeApproval.MonthlyMessageLimit);

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7012,
                CustomerEmail = "owner@test.local",
                Amount = 12000m,
                Currency = "CRC",
                ProviderSubscriberId = "subscriber-wa800-active",
                ProviderOrderNumber = "PRE-WA800-REPLACE",
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7012,
                  "email": "owner@test.local",
                  "amount": 12000,
                  "orderNumber": "PRE-WA800-REPLACE"
                }
                """,
                "corr-repeat-wa800-replace",
                "repeat_payment_success");

            var addonAfterApproval = await context.TenantSubscriptionAddons.IgnoreQueryFilters().SingleAsync();

            Assert.True(result.IsProcessed);
            Assert.Equal(addonBeforeApproval.Id, addonAfterApproval.Id);
            Assert.Equal(newAddonPlanId, addonAfterApproval.PlanId);
            Assert.Equal(PlanCodes.WhatsApp800, addonAfterApproval.AddonCode);
            // La cuota mensual comercial se actualiza en el add-on (WA400 → WA800).
            Assert.Equal(800, addonAfterApproval.MonthlyMessageLimit);
            Assert.Equal("subscriber-wa800-active", addonAfterApproval.ProviderSubscriptionId);
            Assert.Equal("PRE-WA800-REPLACE", addonAfterApproval.ProviderTransactionId);
            // Opción A: el cambio de paquete no crea/toca la configuración técnica de WhatsApp.
            Assert.Empty(context.TenantWhatsAppSettings.IgnoreQueryFilters());
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldKeepPreviousWhatsAppAddonWhenNewAddonPaymentFails()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var basePlanId = Guid.NewGuid();
            var currentAddonPlanId = Guid.NewGuid();
            var newAddonPlanId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Failed Addon Upgrade",
                Activo = true
            });

            context.Planes.AddRange(
                CreateRecurringPlan(
                    basePlanId,
                    PlanCodes.Basic,
                    "Basico",
                    8000,
                    1),
                CreateWhatsAppAddonPlan(
                    currentAddonPlanId,
                    PlanCodes.WhatsApp400,
                    "WhatsApp 400",
                    6000,
                    400),
                CreateWhatsAppAddonPlan(
                    newAddonPlanId,
                    PlanCodes.WhatsApp800,
                    "WhatsApp 800",
                    12000,
                    800));

            context.Suscripciones.Add(CreateActiveBaseSubscription(
                tenantId,
                basePlanId,
                PlanCodes.Basic,
                monthlyPrice: 8000m,
                maxFuncionarios: 1,
                providerSubscriptionId: "base-basic-subscriber",
                providerTransactionId: "BASE-BASIC-TX"));

            context.TenantSubscriptionAddons.Add(CreateActiveWhatsAppAddon(
                tenantId,
                currentAddonPlanId,
                PlanCodes.WhatsApp400,
                monthlyPrice: 6000m,
                monthlyMessageLimit: 400,
                providerSubscriptionId: "subscriber-wa400-active",
                providerTransactionId: "WA400-ACTIVE-TX"));

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                BuildAddonRecurringOptions(
                    PlanCodes.WhatsApp800,
                    recurringPlanId: 7012,
                    monthlyPrice: 12000,
                    monthlyMessageLimit: 800,
                    dailyMessageLimit: 30,
                    checkoutUrl: "https://tp.cr/l/wa800"),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                newAddonPlanId,
                "Owner",
                "owner@test.local");

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7012,
                CustomerEmail = "owner@test.local",
                Amount = 12000m,
                Currency = "CRC",
                ProviderSubscriberId = "subscriber-wa800-pending",
                ProviderOrderNumber = "PRE-WA800-FAILED",
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7012,
                  "email": "owner@test.local",
                  "amount": 12000,
                  "orderNumber": "PRE-WA800-FAILED"
                }
                """,
                "corr-repeat-wa800-failed",
                "repeat_payment_failed");

            var addon = await context.TenantSubscriptionAddons.IgnoreQueryFilters().SingleAsync();
            var failedAttempt = await context.PagosSuscripcion
                .IgnoreQueryFilters()
                .OrderByDescending(payment => payment.FechaCreacionUtc)
                .FirstAsync();
            var baseSubscription = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();

            Assert.True(result.IsProcessed);
            Assert.Equal(PlanCodes.WhatsApp400, addon.AddonCode);
            Assert.Equal(currentAddonPlanId, addon.PlanId);
            Assert.Equal(EstadoSuscripcion.Activa, addon.Estado);
            Assert.Equal(EstadoPagoProveedor.Fallido, failedAttempt.Estado);
            Assert.Equal(basePlanId, baseSubscription.PlanId);
            Assert.Equal(PlanCodes.Basic, baseSubscription.CodigoPlan);
            Assert.Equal(1, baseSubscription.MaxFuncionarios);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldCancelWhatsAppAddonWithoutChangingBaseSubscription()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var basePlanId = Guid.NewGuid();
            var addonPlanId = Guid.NewGuid();
            var expirationDateUtc = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Cancel Addon",
                Activo = true
            });

            context.Planes.AddRange(
                CreateRecurringPlan(
                    basePlanId,
                    PlanCodes.Pro,
                    "Pro",
                    20000,
                    3),
                CreateWhatsAppAddonPlan(
                    addonPlanId,
                    PlanCodes.WhatsApp400,
                    "WhatsApp 400",
                    6000,
                    400));

            context.Suscripciones.Add(CreateActiveBaseSubscription(
                tenantId,
                basePlanId,
                PlanCodes.Pro,
                monthlyPrice: 20000m,
                maxFuncionarios: 3,
                providerSubscriptionId: "base-pro-subscriber",
                providerTransactionId: "BASE-PRO-TX"));

            context.TenantSubscriptionAddons.Add(CreateActiveWhatsAppAddon(
                tenantId,
                addonPlanId,
                PlanCodes.WhatsApp400,
                monthlyPrice: 6000m,
                monthlyMessageLimit: 400,
                providerSubscriptionId: "subscriber-wa400-active",
                providerTransactionId: "WA400-ACTIVE-TX"));

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                BuildAddonRecurringOptions(
                    PlanCodes.WhatsApp400,
                    recurringPlanId: 7011,
                    monthlyPrice: 6000,
                    monthlyMessageLimit: 400,
                    dailyMessageLimit: 15,
                    checkoutUrl: "https://tp.cr/l/wa400"),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7011,
                CustomerEmail = "owner@test.local",
                ProviderSubscriberId = "subscriber-wa400-active",
                ExpirationDateUtc = expirationDateUtc,
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7011,
                  "email": "owner@test.local",
                  "expire": "2026-07-20"
                }
                """,
                "corr-repeat-wa400-cancelled",
                "repeat_subscription_cancelled");

            var addon = await context.TenantSubscriptionAddons.IgnoreQueryFilters().SingleAsync();
            var baseSubscription = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();

            Assert.True(result.IsProcessed);
            Assert.Equal(EstadoSuscripcion.Cancelada, addon.Estado);
            Assert.Equal(expirationDateUtc, addon.FechaFin);
            Assert.Equal(EstadoSuscripcion.Activa, baseSubscription.Estado);
            Assert.Equal(basePlanId, baseSubscription.PlanId);
            Assert.Equal(PlanCodes.Pro, baseSubscription.CodigoPlan);
            Assert.Equal(3, baseSubscription.MaxFuncionarios);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldMarkPublicRecurringWebhookForManualReviewWhenAmountDoesNotMatchAndOfficialPayloadHasNoReference()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Basic Wrong Amount",
                Activo = true
            });

            context.Planes.Add(CreateRecurringPlan(
                planId,
                PlanCodes.Basic,
                "Basico",
                8000,
                1));

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                BuildPublicRecurringOptions(
                    PlanCodes.Basic,
                    recurringPlanId: 7001,
                    monthlyPrice: 8000,
                    maxFuncionarios: 1,
                    checkoutUrl: "https://tp.cr/l/basic"),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7001,
                CustomerEmail = "owner@test.local",
                Amount = 9000m,
                Currency = "CRC",
                ProviderSubscriberId = "subscriber-basic",
                ProviderOrderNumber = "PRE-BASIC-WRONG-AMOUNT",
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7001,
                  "email": "owner@test.local",
                  "amount": 9000,
                  "orderNumber": "PRE-BASIC-WRONG-AMOUNT"
                }
                """,
                "corr-repeat-basic-wrong-amount",
                "repeat_payment_success");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            Assert.False(result.IsProcessed);
            Assert.Equal("PendingManualReview", evento.EstadoProcesamiento);
            Assert.Equal(intento.Id, evento.PagoSuscripcionId);
            Assert.Contains("monto", evento.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(EstadoPagoProveedor.ManualReview, intento.Estado);
            Assert.Equal(EstadoSuscripcion.Pendiente, suscripcion.Estado);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldMarkUnknownRecurringPlanIdForManualReview()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Unknown Plan",
                Activo = true
            });

            context.Planes.Add(CreateRecurringPlan(
                planId,
                PlanCodes.Basic,
                "Basico",
                8000,
                1));

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                BuildPublicRecurringOptions(
                    PlanCodes.Basic,
                    recurringPlanId: 7001,
                    monthlyPrice: 8000,
                    maxFuncionarios: 1,
                    checkoutUrl: "https://tp.cr/l/basic"),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 9999,
                CustomerEmail = "owner@test.local",
                Amount = 8000m,
                ProviderOrderNumber = "PRE-UNKNOWN-PLAN",
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 9999,
                  "email": "owner@test.local",
                  "amount": 8000,
                  "orderNumber": "PRE-UNKNOWN-PLAN"
                }
                """,
                "corr-repeat-unknown-plan",
                "repeat_payment_success");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            Assert.False(result.IsProcessed);
            Assert.Equal("PendingManualReview", evento.EstadoProcesamiento);
            Assert.Contains("9999", evento.Error ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(EstadoPagoProveedor.Pendiente, intento.Estado);
            Assert.Equal(EstadoSuscripcion.Pendiente, suscripcion.Estado);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldMarkOfficialRepeatPaymentSuccessAsManualReviewWhenCorrelationIsAmbiguous()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var firstTenantId = Guid.NewGuid();
            var secondTenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.AddRange(
                new Tenant
                {
                    Id = firstTenantId,
                    Nombre = "Tenant Ambiguous 1",
                    Activo = true
                },
                new Tenant
                {
                    Id = secondTenantId,
                    Nombre = "Tenant Ambiguous 2",
                    Activo = true
                });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                BuildTestRecurringOptions(),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                firstTenantId,
                planId,
                "Owner 1",
                "shared@test.local");

            await service.CreateRecurringCheckoutAsync(
                secondTenantId,
                planId,
                "Owner 2",
                "shared@test.local");

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7099,
                CustomerEmail = "shared@test.local",
                Amount = 1000m,
                ProviderOrderNumber = "PRE-AMB-001",
                AuthorizationCode = "123456",
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7099,
                  "email": "shared@test.local",
                  "amount": 1000,
                  "auth": "123456",
                  "orderNumber": "PRE-AMB-001"
                }
                """,
                "corr-repeat-ambiguous",
                "repeat_payment_success");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var attempts = await context.PagosSuscripcion
                .IgnoreQueryFilters()
                .OrderBy(payment => payment.FechaCreacionUtc)
                .ToListAsync();

            Assert.False(result.IsProcessed);
            Assert.Equal("PendingManualReview", evento.EstadoProcesamiento);
            Assert.Contains("multiples signups pendientes", evento.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, attempts.Count);
            Assert.All(attempts, attempt => Assert.Equal(EstadoPagoProveedor.Pendiente, attempt.Estado));
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldMarkOfficialRepeatPaymentSuccessAsUnmatchedWhenPendingDoesNotExist()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var planId = Guid.NewGuid();

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider
            {
                WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventType = "tilopay.repeat.notification",
                    Reference = string.Empty,
                    RecurringPlanId = 7099,
                    CustomerEmail = "missing@test.local",
                    Amount = 1000m,
                    ProviderOrderNumber = "PRE-NO-PENDING",
                    IsRecurring = true
                }
            };

            var service = CreatePaymentService(
                context,
                BuildTestRecurringOptions(),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7099,
                  "email": "missing@test.local",
                  "amount": 1000,
                  "orderNumber": "PRE-NO-PENDING"
                }
                """,
                "corr-repeat-unmatched",
                "repeat_payment_success");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();

            Assert.False(result.IsProcessed);
            Assert.Equal("SinRelacion", evento.EstadoProcesamiento);
            Assert.Contains("pending recurrente vigente", evento.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await context.PagosSuscripcion.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await context.Suscripciones.IgnoreQueryFilters().ToListAsync());
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldMarkOfficialRepeatPaymentSuccessAsManualReviewWhenEmailDoesNotMatchPending()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Email Mismatch",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider
            {
                WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventType = "tilopay.repeat.notification",
                    Reference = string.Empty,
                    RecurringPlanId = 7099,
                    CustomerEmail = "billing-other@test.local",
                    Amount = 1000m,
                    ProviderOrderNumber = "PRE-EMAIL-MISMATCH",
                    IsRecurring = true
                }
            };

            var service = CreatePaymentService(
                context,
                BuildTestRecurringOptions(),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner mismatch",
                "owner@test.local");

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7099,
                  "email": "billing-other@test.local",
                  "amount": 1000,
                  "orderNumber": "PRE-EMAIL-MISMATCH"
                }
                """,
                "corr-repeat-email-mismatch",
                "repeat_payment_success");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();

            Assert.False(result.IsProcessed);
            Assert.Equal("PendingManualReview", evento.EstadoProcesamiento);
            Assert.Contains("correo", evento.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(intento.Id, evento.PagoSuscripcionId);
            Assert.Equal(EstadoPagoProveedor.ManualReview, intento.Estado);
            Assert.Contains("billing-other@test.local", intento.ProviderResultMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(EstadoSuscripcion.Pendiente, suscripcion.Estado);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldKeepSubscriptionPendingOnOfficialRepeatRegistrationWithoutFreeTrial()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var nextPaymentDateUtc = new DateTime(2026, 6, 26, 0, 0, 0, DateTimeKind.Utc);

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Registration",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                BuildTestRecurringOptions(),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7099,
                CustomerEmail = "owner@test.local",
                Amount = 1000m,
                ProviderSubscriberId = "subscriber-registered",
                NextBillingDateUtc = nextPaymentDateUtc,
                HasFreeTrial = false,
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7099,
                  "email": "owner@test.local",
                  "amount": 1000,
                  "free_trial": 0,
                  "next_payment_date": "2026-06-26"
                }
                """,
                "corr-repeat-registration",
                "repeat_registration");

            var evento = await context.EventosPago.IgnoreQueryFilters().SingleAsync();
            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var intento = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();

            Assert.True(result.IsProcessed);
            Assert.Equal("Procesado", evento.EstadoProcesamiento);
            Assert.Equal(EstadoSuscripcion.Pendiente, suscripcion.Estado);
            Assert.Equal(nextPaymentDateUtc, suscripcion.FechaProximoCobroUtc);
            Assert.Equal("subscriber-registered", suscripcion.ProviderSubscriptionId);
            Assert.Equal(EstadoPagoProveedor.Pendiente, intento.Estado);
            Assert.Equal("subscriber-registered", intento.ProviderSubscriberId);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldMarkSubscriptionAsMorosaOnOfficialRepeatPaymentFailed()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Failed Payment",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                BuildTestRecurringOptions(),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            await service.ApproveRecurringPaymentAsync(
                new RecurringPaymentApprovalRequest
                {
                    PaymentId = pending.Id,
                    ProviderTransactionId = "PRE-INITIAL-OK",
                    ProviderSubscriberId = "subscriber-active",
                    ApprovedAmount = 1000m,
                    Currency = "CRC",
                    Observation = "Activacion previa para prueba de rechazo."
                });

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7099,
                CustomerEmail = "owner@test.local",
                Amount = 1000m,
                ProviderSubscriberId = "subscriber-active",
                ProviderOrderNumber = "PRE-FAILED-001",
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7099,
                  "email": "owner@test.local",
                  "amount": 1000
                }
                """,
                "corr-repeat-failed",
                "repeat_payment_failed");

            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var failedAttempt = await context.PagosSuscripcion
                .IgnoreQueryFilters()
                .OrderByDescending(payment => payment.FechaCreacionUtc)
                .FirstAsync();
            var evento = await context.EventosPago
                .IgnoreQueryFilters()
                .OrderByDescending(paymentEvent => paymentEvent.FechaRecepcionUtc)
                .FirstAsync();

            Assert.True(result.IsProcessed);
            Assert.Equal(EstadoSuscripcion.Morosa, suscripcion.Estado);
            Assert.Equal(EstadoPagoProveedor.Fallido, failedAttempt.Estado);
            Assert.Equal("Procesado", evento.EstadoProcesamiento);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldCancelSubscriptionOnOfficialRepeatCancellation()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var expirationDateUtc = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Cancelled",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var service = CreatePaymentService(
                context,
                BuildTestRecurringOptions(),
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            await service.ApproveRecurringPaymentAsync(
                new RecurringPaymentApprovalRequest
                {
                    PaymentId = pending.Id,
                    ProviderTransactionId = "PRE-CANCEL-OK",
                    ProviderSubscriberId = "subscriber-cancelled",
                    ApprovedAmount = 1000m,
                    Currency = "CRC",
                    Observation = "Activacion previa para prueba de cancelacion."
                });

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7099,
                CustomerEmail = "owner@test.local",
                ProviderSubscriberId = "subscriber-cancelled",
                ExpirationDateUtc = expirationDateUtc,
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7099,
                  "email": "owner@test.local",
                  "expire": "2026-06-30"
                }
                """,
                "corr-repeat-cancelled",
                "repeat_subscription_cancelled");

            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var evento = await context.EventosPago
                .IgnoreQueryFilters()
                .OrderByDescending(paymentEvent => paymentEvent.FechaRecepcionUtc)
                .FirstAsync();

            Assert.True(result.IsProcessed);
            Assert.Equal(EstadoSuscripcion.Cancelada, suscripcion.Estado);
            Assert.Equal(expirationDateUtc, suscripcion.FechaFin);
            Assert.Equal("Procesado", evento.EstadoProcesamiento);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldReactivateSubscriptionOnOfficialRepeatReactivation()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var nextPaymentDateUtc = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Reactivated",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakeTilopayPaymentProvider();
            var repeatOptions = BuildTestRecurringOptions();
            var service = CreatePaymentService(
                context,
                repeatOptions,
                new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                },
                fakeProvider);

            await service.CreateRecurringCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local");

            var pending = await context.PagosSuscripcion.IgnoreQueryFilters().SingleAsync();
            await service.ApproveRecurringPaymentAsync(
                new RecurringPaymentApprovalRequest
                {
                    PaymentId = pending.Id,
                    ProviderTransactionId = "PRE-REACT-OK",
                    ProviderSubscriberId = "subscriber-reactivated",
                    ApprovedAmount = 1000m,
                    Currency = "CRC",
                    Observation = "Activacion previa para prueba de reactivacion."
                });

            await CreateSubscriptionService(context, repeatOptions).MarcarSuscripcionCanceladaRecurrenteAsync(
                tenantId,
                "subscriber-reactivated",
                isAddon: false);

            fakeProvider.WebhookData = new PaymentProviderWebhookData
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventType = "tilopay.repeat.notification",
                Reference = string.Empty,
                RecurringPlanId = 7099,
                CustomerEmail = "owner@test.local",
                ProviderSubscriberId = "subscriber-reactivated",
                NextBillingDateUtc = nextPaymentDateUtc,
                IsRecurring = true
            };

            var result = await service.ProcessTilopayWebhookAsync(
                """
                {
                  "id_plan": 7099,
                  "email": "owner@test.local",
                  "next_payment_date": "2026-07-05"
                }
                """,
                "corr-repeat-reactivated",
                "repeat_subscription_reactivated");

            var suscripcion = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();
            var evento = await context.EventosPago
                .IgnoreQueryFilters()
                .OrderByDescending(paymentEvent => paymentEvent.FechaRecepcionUtc)
                .FirstAsync();

            Assert.True(result.IsProcessed);
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Equal("subscriber-reactivated", suscripcion.ProviderSubscriptionId);
            Assert.Equal(nextPaymentDateUtc, suscripcion.FechaProximoCobroUtc);
            Assert.Equal("Procesado", evento.EstadoProcesamiento);
        }

        private static (ProyectoIdentity.Datos.ApplicationDbContext Context, IDisposable Connection) CreateSystemContext()
        {
            var tenantProvider = new TestTenantProvider();
            return TestDbContextFactory.CreateSqliteContext(tenantProvider);
        }

        private static Plan CreateRecurringPlan(
            Guid planId,
            string planCode,
            string planName,
            decimal monthlyPrice,
            int maxFuncionarios,
            bool isValidationPlan = false) =>
            new()
            {
                Id = planId,
                Codigo = planCode,
                Nombre = planName,
                PrecioMensual = monthlyPrice,
                Moneda = "CRC",
                MaxFuncionarios = maxFuncionarios,
                Activo = true,
                EsPlanValidacion = isValidationPlan
            };

        private static Plan CreateWhatsAppAddonPlan(
            Guid planId,
            string planCode,
            string planName,
            decimal monthlyPrice,
            int monthlyMessageLimit) =>
            new()
            {
                Id = planId,
                Codigo = planCode,
                Nombre = planName,
                PrecioMensual = monthlyPrice,
                Moneda = "CRC",
                LimiteMensajesMensual = monthlyMessageLimit,
                Activo = true
            };

        private static Suscripcion CreateActiveBaseSubscription(
            Guid tenantId,
            Guid planId,
            string planCode,
            decimal monthlyPrice,
            int maxFuncionarios,
            string providerSubscriptionId,
            string providerTransactionId) =>
            new()
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                CodigoPlan = planCode,
                Proveedor = PaymentProviderType.Tilopay,
                ProviderSubscriptionId = providerSubscriptionId,
                ProviderTransactionId = providerTransactionId,
                Estado = EstadoSuscripcion.Activa,
                FechaInicio = DateTime.UtcNow.AddDays(-3),
                FechaFin = DateTime.UtcNow.AddDays(27),
                FechaProximoCobroUtc = DateTime.UtcNow.AddDays(27),
                PrecioMensual = monthlyPrice,
                MaxFuncionarios = maxFuncionarios,
                FechaUltimaActualizacionUtc = DateTime.UtcNow
            };

        private static TenantSubscriptionAddon CreateActiveWhatsAppAddon(
            Guid tenantId,
            Guid planId,
            string addonCode,
            decimal monthlyPrice,
            int monthlyMessageLimit,
            string providerSubscriptionId,
            string providerTransactionId) =>
            new()
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                AddonCode = addonCode,
                Estado = EstadoSuscripcion.Activa,
                ProviderSubscriptionId = providerSubscriptionId,
                ProviderTransactionId = providerTransactionId,
                PrecioMensual = monthlyPrice,
                MonedaFacturacion = "CRC",
                MonthlyMessageLimit = monthlyMessageLimit,
                FechaInicio = DateTime.UtcNow.AddDays(-3),
                FechaFin = DateTime.UtcNow.AddDays(27),
                FechaProximoCobroUtc = DateTime.UtcNow.AddDays(27),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

        private static TilopayRepeatOptions BuildPublicRecurringOptions(
            string planCode,
            int recurringPlanId,
            decimal monthlyPrice,
            int maxFuncionarios,
            string checkoutUrl)
        {
            var options = new TilopayRepeatOptions
            {
                Enabled = true,
                UseHostedLinks = true,
                UseRecurringCheckoutForPublicPlans = true
            };

            var plan = new TilopayRepeatPlanOption
            {
                TilopayPlanId = recurringPlanId,
                Code = planCode,
                MonthlyPrice = monthlyPrice,
                Currency = "CRC",
                MaxFuncionarios = maxFuncionarios,
                CheckoutUrl = checkoutUrl
            };

            switch (planCode)
            {
                case PlanCodes.Basic:
                    options.Basic = plan;
                    break;
                case PlanCodes.Pro:
                    options.Pro = plan;
                    break;
                case PlanCodes.Business:
                    options.Business = plan;
                    break;
                default:
                    throw new InvalidOperationException($"Plan publico no soportado en test: {planCode}.");
            }

            return options;
        }

        private static TilopayRepeatOptions BuildAddonRecurringOptions(
            string addonCode,
            int recurringPlanId,
            decimal monthlyPrice,
            int monthlyMessageLimit,
            int dailyMessageLimit,
            string checkoutUrl)
        {
            var options = new TilopayRepeatOptions
            {
                Enabled = true,
                UseHostedLinks = true,
                UseRecurringCheckoutForPublicPlans = true
            };

            var plan = new TilopayRepeatPlanOption
            {
                TilopayPlanId = recurringPlanId,
                Code = addonCode,
                MonthlyPrice = monthlyPrice,
                Currency = "CRC",
                MonthlyMessageLimit = monthlyMessageLimit,
                DailyMessageLimit = dailyMessageLimit,
                CheckoutUrl = checkoutUrl,
                IsAddon = true
            };

            switch (addonCode)
            {
                case PlanCodes.WhatsApp400:
                    options.WhatsApp400 = plan;
                    break;
                case PlanCodes.WhatsApp800:
                    options.WhatsApp800 = plan;
                    break;
                case PlanCodes.WhatsApp1200:
                    options.WhatsApp1200 = plan;
                    break;
                default:
                    throw new InvalidOperationException($"Add-on WhatsApp no soportado en test: {addonCode}.");
            }

            return options;
        }

        private static SaaSPaymentService CreatePaymentService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TilopayRepeatOptions repeatOptions,
            OpcionesTilopay tilopayOptions,
            IPaymentProvider? provider = null)
        {
            return new SaaSPaymentService(
                context,
                new PaymentProviderResolver(provider is null ? Array.Empty<IPaymentProvider>() : new[] { provider }),
                CreateSubscriptionService(context, repeatOptions),
                new TenantExecutionContextAccessor(),
                Options.Create(new OpcionesPago
                {
                    ProveedorPredeterminado = PaymentProviderType.Tilopay
                }),
                Options.Create(tilopayOptions),
                Options.Create(repeatOptions),
                NullLogger<SaaSPaymentService>.Instance);
        }

        private static SuscripcionService CreateSubscriptionService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TilopayRepeatOptions repeatOptions)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            return new SuscripcionService(
                context,
                cache,
                new TenantCommercialAccessCache(cache),
                new FixedBusinessDateTimeProvider(),
                Options.Create(repeatOptions),
                NullLogger<SuscripcionService>.Instance);
        }

        private sealed class FakeTilopayPaymentProvider : IPaymentProvider
        {
            public PaymentProviderType ProviderType => PaymentProviderType.Tilopay;

            public PaymentProviderWebhookData WebhookData { get; set; } = new()
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventId = "evt-default",
                EventType = "tilopay.repeat.notification",
                Reference = "LXA-DEFAULT-REF",
                IsRecurring = true
            };

            public Task<PaymentCheckoutResult> CreateCheckoutAsync(
                PaymentCheckoutRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PaymentCheckoutResult
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    RedirectUrl = request.SuccessUrl
                });

            public PaymentProviderWebhookData ParseWebhook(string payload) => WebhookData;

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

        private static TilopayRepeatOptions BuildTestRecurringOptions() =>
            new()
            {
                Enabled = true,
                UseHostedLinks = true,
                EnableTestRecurringPlan = true,
                TestRecurring = new TilopayRepeatPlanOption
                {
                    TilopayPlanId = 7099,
                    Code = PlanCodes.TestRecurring,
                    MonthlyPrice = 1000,
                    Currency = "CRC",
                    MaxFuncionarios = 1,
                    CheckoutUrl = "https://tp.cr/l/test-link",
                    IsValidation = true
                }
            };
    }
}
