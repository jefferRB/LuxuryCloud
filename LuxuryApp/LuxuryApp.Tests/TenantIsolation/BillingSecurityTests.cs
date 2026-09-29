using System.Security.Claims;
using LuxuryApp.Controllers;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Payments;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class BillingSecurityTests
    {
        [Fact]
        public async Task CreateCheckoutAsync_ShouldRedactWebhookTokenFromAuditPayload()
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
                Nombre = "Plan Pro",
                PrecioMensual = 25,
                Moneda = "CRC",
                Activo = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakePaymentProvider
            {
                CheckoutResult = new PaymentCheckoutResult
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    RedirectUrl = "https://pay.local/checkout/1",
                    ProviderCheckoutId = "checkout-1",
                    ProviderReference = "LXA-ABCDEF-1234567890",
                    RawResponse = "{\"ok\":true}"
                }
            };

            var service = CreatePaymentService(context, fakeProvider);

            await service.CreateCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local",
                "https://app.local/Billing/Exito",
                "https://app.local/Billing/Cancelado",
                "https://app.local/api/webhooks/tilopay?access_token=secret-token");

            var intento = context.PagosSuscripcion.Single();

            Assert.DoesNotContain("secret-token", intento.UltimoPayloadProveedor);
            Assert.DoesNotContain("access_token=", intento.UltimoPayloadProveedor, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CreateCheckoutAsync_WithTenantContext_ShouldPersistPendingRecordsUsingGlobalPlan()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
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
                Nombre = "Plan Pro",
                PrecioMensual = 25,
                Moneda = "CRC",
                Activo = true
            });

            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantId;

            var fakeProvider = new FakePaymentProvider
            {
                CheckoutResult = new PaymentCheckoutResult
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    RedirectUrl = "https://pay.local/checkout/tenant",
                    ProviderCheckoutId = "checkout-tenant",
                    ProviderReference = "LXA-ABCDEF-1234567890",
                    RawResponse = "{\"ok\":true}"
                }
            };

            var service = CreatePaymentService(context, fakeProvider);

            await service.CreateCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local",
                "https://app.local/Billing/Exito",
                "https://app.local/Billing/Cancelado",
                "https://app.local/api/webhooks/tilopay?access_token=secret-token");

            var intento = context.PagosSuscripcion.IgnoreQueryFilters().Single();
            var suscripcion = context.Suscripciones.IgnoreQueryFilters().Single();

            Assert.Equal(tenantId, intento.TenantId);
            Assert.Equal(planId, intento.PlanId);
            Assert.Equal(EstadoPagoProveedor.Pendiente, intento.Estado);
            Assert.Equal(tenantId, suscripcion.TenantId);
            Assert.Equal(planId, suscripcion.PlanId);
            Assert.Equal(EstadoSuscripcion.Pendiente, suscripcion.Estado);
        }

        [Fact]
        public async Task CreateCheckoutAsync_ShouldReuseRecentPendingCheckoutForSameTenantPlanAndEmail()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Reuse Checkout",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Pro",
                PrecioMensual = 25,
                Moneda = "CRC",
                Activo = true
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakePaymentProvider
            {
                CheckoutResult = new PaymentCheckoutResult
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    RedirectUrl = "https://pay.local/checkout/reuse",
                    ProviderCheckoutId = "checkout-reuse",
                    ProviderReference = "LXA-REUSE-1234567890",
                    RawResponse = "{\"ok\":true}"
                }
            };

            var service = CreatePaymentService(context, fakeProvider);

            var first = await service.CreateCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local",
                "https://app.local/Billing/Exito",
                "https://app.local/Billing/Cancelado",
                "https://app.local/api/webhooks/tilopay?access_token=secret-token");

            var second = await service.CreateCheckoutAsync(
                tenantId,
                planId,
                "Owner",
                "owner@test.local",
                "https://app.local/Billing/Exito",
                "https://app.local/Billing/Cancelado",
                "https://app.local/api/webhooks/tilopay?access_token=secret-token");

            var attempts = await context.PagosSuscripcion
                .IgnoreQueryFilters()
                .ToListAsync();

            Assert.Single(attempts);
            Assert.Equal(EstadoPagoProveedor.Pendiente, attempts[0].Estado);
            Assert.Equal(first.RedirectUrl, second.RedirectUrl);
            Assert.Equal(first.ProviderCheckoutId, second.ProviderCheckoutId);
            Assert.Equal(1, fakeProvider.CreateCheckoutCalls);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldRejectCheckoutIdMismatch()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            const string reference = "LXA-ABCDEF-1234567890";

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant A",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Pro",
                PrecioMensual = 40,
                Moneda = "CRC",
                Activo = true
            });

            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = reference,
                ProviderReference = reference,
                ProviderCheckoutId = "checkout-expected",
                Estado = EstadoPagoProveedor.Pendiente,
                Descripcion = "Checkout seguro",
                Monto = 40,
                Moneda = "CRC"
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakePaymentProvider
            {
                WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventId = "evt-1",
                    EventType = "tilopay.link.completed",
                    Reference = reference,
                    ProviderCheckoutId = "checkout-malicious",
                    ProviderTransactionId = "tx-1"
                },
                VerificationResult = new PaymentVerificationResult
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    Exists = true,
                    IsSuccess = true,
                    Reference = reference,
                    ProviderOrderNumber = reference,
                    ProviderTransactionId = "tx-1",
                    Amount = 40,
                    Currency = "CRC",
                    StatusCode = "1",
                    StatusDescription = "Aprobado",
                    RawResponse = "{\"approved\":true}"
                }
            };

            var service = CreatePaymentService(context, fakeProvider);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ProcessTilopayWebhookAsync("{}", "corr-1"));

            Assert.Contains("checkout", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(context.Facturas.Any());

            var evento = context.EventosPago.Single();
            Assert.False(evento.Procesado);
            Assert.Equal("Error", evento.EstadoProcesamiento);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldAcceptProviderOrderNumberDifferentFromInternalReference()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            const string internalReference = "LXA-ABCDEF-1234567890";
            const string providerOrderNumber = "TYP4447_203709";

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant A",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Pro",
                PrecioMensual = 40,
                Moneda = "CRC",
                Activo = true
            });

            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = internalReference,
                ProviderReference = internalReference,
                ProviderCheckoutId = "203709",
                Estado = EstadoPagoProveedor.Pendiente,
                Descripcion = "Checkout seguro",
                Monto = 40,
                Moneda = "CRC"
            });

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ProviderReference = internalReference,
                Estado = EstadoSuscripcion.Pendiente,
                FechaInicio = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakePaymentProvider
            {
                WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventId = "evt-1",
                    EventType = "tilopay.link.completed",
                    Reference = internalReference,
                    ProviderOrderNumber = providerOrderNumber,
                    ProviderCheckoutId = "203709",
                    ProviderTransactionId = "4747076",
                    AuthorizationCode = "430677"
                },
                VerificationResult = new PaymentVerificationResult
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    Exists = true,
                    IsSuccess = true,
                    Reference = internalReference,
                    ProviderOrderNumber = $"PFC000001-{providerOrderNumber}",
                    ProviderTransactionId = "4747076",
                    Amount = 40,
                    Currency = "CRC",
                    StatusCode = "1",
                    StatusDescription = "Aprobado",
                    RawResponse = "{\"approved\":true}"
                }
            };

            var service = CreatePaymentService(context, fakeProvider);

            var result = await service.ProcessTilopayWebhookAsync("{}", "corr-1");

            Assert.True(result.IsProcessed);
            Assert.Equal(EstadoPagoProveedor.Confirmado, result.EstadoPago);

            var pago = context.PagosSuscripcion.IgnoreQueryFilters().Single();
            var suscripcion = context.Suscripciones.IgnoreQueryFilters().Single();
            var factura = context.Facturas.IgnoreQueryFilters().Single();
            var evento = context.EventosPago.IgnoreQueryFilters().Single();

            Assert.Equal(EstadoPagoProveedor.Confirmado, pago.Estado);
            Assert.Equal(providerOrderNumber, pago.ProviderReference);
            Assert.Equal("4747076", pago.ProviderTransactionId);
            Assert.Equal(EstadoSuscripcion.Activa, suscripcion.Estado);
            Assert.Equal(providerOrderNumber, suscripcion.ProviderReference);
            Assert.Equal("203709", suscripcion.ProviderPaymentLinkId);
            Assert.Equal(providerOrderNumber, factura.ProviderReference);
            Assert.True(evento.Procesado);
            Assert.Equal("Procesado", evento.EstadoProcesamiento);
            Assert.Equal(providerOrderNumber, evento.ReferenciaExterna);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldActivateLinkPaymentForExactlyOneMonth()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            const string internalReference = "LXA-ABCDEF-1234567890";
            const string providerOrderNumber = "TYP4447_203710";
            const string checkoutId = "203710";

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant A",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Pro",
                PrecioMensual = 40,
                Moneda = "CRC",
                Activo = true
            });

            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = internalReference,
                ProviderReference = internalReference,
                ProviderCheckoutId = checkoutId,
                Estado = EstadoPagoProveedor.Pendiente,
                Descripcion = "Checkout seguro",
                Monto = 40,
                Moneda = "CRC"
            });

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ProviderReference = internalReference,
                Estado = EstadoSuscripcion.Pendiente,
                FechaInicio = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakePaymentProvider
            {
                WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventId = "evt-one-month",
                    EventType = "tilopay.link.completed",
                    Reference = internalReference,
                    ProviderOrderNumber = providerOrderNumber,
                    ProviderCheckoutId = checkoutId,
                    ProviderTransactionId = "4747077",
                    AuthorizationCode = "430678"
                },
                VerificationResult = new PaymentVerificationResult
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    Exists = true,
                    IsSuccess = true,
                    Reference = internalReference,
                    ProviderOrderNumber = $"PFC000001-{providerOrderNumber}",
                    ProviderTransactionId = "4747077",
                    Amount = 40,
                    Currency = "CRC",
                    StatusCode = "1",
                    StatusDescription = "Aprobado",
                    RawResponse = "{\"approved\":true}"
                }
            };

            var service = CreatePaymentService(context, fakeProvider);

            var result = await service.ProcessTilopayWebhookAsync("{}", "corr-one-month");

            Assert.True(result.IsProcessed);

            var expectedStartUtc = new DateTimeOffset(
                new DateTime(2026, 5, 26, 10, 30, 0),
                TimeSpan.FromHours(-6)).UtcDateTime;
            var subscription = context.Suscripciones.IgnoreQueryFilters().Single();

            Assert.Equal(EstadoSuscripcion.Activa, subscription.Estado);
            Assert.Equal(expectedStartUtc, subscription.FechaInicio);
            Assert.Equal(expectedStartUtc.AddMonths(1), subscription.FechaFin);
            Assert.Equal(subscription.FechaFin, subscription.FechaProximoCobroUtc);
            Assert.Equal(checkoutId, subscription.ProviderPaymentLinkId);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldIgnoreDuplicateLinkPaymentTransactionWithoutExtendingAgain()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var currentPeriodEndUtc = new DateTime(2026, 6, 26, 16, 30, 0, DateTimeKind.Utc);
            const string internalReference = "LXA-ABCDEF-1234567890";
            const string providerOrderNumber = "TYP4447_203711";

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant A",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Pro",
                PrecioMensual = 40,
                Moneda = "CRC",
                Activo = true
            });

            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = internalReference,
                ProviderReference = providerOrderNumber,
                ProviderTransactionId = "4747078",
                ProviderCheckoutId = "203711",
                Estado = EstadoPagoProveedor.Confirmado,
                Descripcion = "Checkout seguro",
                Monto = 40,
                Moneda = "CRC",
                FechaConfirmacionUtc = currentPeriodEndUtc.AddMonths(-1)
            });

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ProviderReference = providerOrderNumber,
                ProviderTransactionId = "4747078",
                ProviderPaymentLinkId = "203711",
                Estado = EstadoSuscripcion.Activa,
                FechaInicio = currentPeriodEndUtc.AddMonths(-1),
                FechaFin = currentPeriodEndUtc,
                FechaProximoCobroUtc = currentPeriodEndUtc
            });

            await context.SaveChangesAsync();

            var fakeProvider = new FakePaymentProvider
            {
                WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventId = "evt-link-duplicate-second-event",
                    EventType = "tilopay.link.completed",
                    Reference = internalReference,
                    ProviderOrderNumber = providerOrderNumber,
                    ProviderCheckoutId = "203711",
                    ProviderTransactionId = "4747078"
                },
                VerificationResult = new PaymentVerificationResult
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    Exists = true,
                    IsSuccess = true,
                    Reference = internalReference,
                    ProviderOrderNumber = $"PFC000001-{providerOrderNumber}",
                    ProviderTransactionId = "4747078",
                    Amount = 40,
                    Currency = "CRC",
                    StatusCode = "1",
                    StatusDescription = "Aprobado",
                    RawResponse = "{\"approved\":true}"
                }
            };

            var service = CreatePaymentService(context, fakeProvider);

            var result = await service.ProcessTilopayWebhookAsync("{}", "corr-link-duplicate");

            var subscription = context.Suscripciones.IgnoreQueryFilters().Single();
            var facturas = context.Facturas.IgnoreQueryFilters().ToList();
            var evento = context.EventosPago.IgnoreQueryFilters().Single();

            Assert.True(result.IsDuplicate);
            Assert.True(result.IsProcessed);
            Assert.Equal(currentPeriodEndUtc, subscription.FechaFin);
            Assert.Empty(facturas);
            Assert.True(evento.Procesado);
        }

        [Fact]
        public async Task ProcessTilopayWebhookAsync_ShouldRejectUnrecognizedReferenceBeforeVerification()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            var fakeProvider = new FakePaymentProvider
            {
                WebhookData = new PaymentProviderWebhookData
                {
                    ProviderType = PaymentProviderType.Tilopay,
                    EventId = "evt-1",
                    EventType = "tilopay.link.completed",
                    Reference = "provider-reference-raw"
                }
            };

            var service = CreatePaymentService(context, fakeProvider);

            await Assert.ThrowsAsync<PaymentWebhookValidationException>(() =>
                service.ProcessTilopayWebhookAsync("{}", "corr-2"));

            Assert.Equal(0, fakeProvider.VerifyCalls);
            Assert.False(context.EventosPago.Any());
        }

        [Fact]
        public async Task BillingSuccess_ShouldNotExposePaymentFromAnotherTenant()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var currentTenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            const string reference = "LXA-FOREIGN-1234567890";

            context.Tenants.AddRange(
                new Tenant
                {
                    Id = currentTenantId,
                    Nombre = "Tenant A",
                    Activo = true
                },
                new Tenant
                {
                    Id = foreignTenantId,
                    Nombre = "Tenant B",
                    Activo = true
                });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Premium",
                PrecioMensual = 55,
                Moneda = "CRC",
                Activo = true
            });

            var currentUser = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "owner-a@test.local",
                NormalizedUserName = "OWNER-A@TEST.LOCAL",
                Email = "owner-a@test.local",
                NormalizedEmail = "OWNER-A@TEST.LOCAL",
                TenantId = currentTenantId,
                State = true
            };

            context.Users.Add(currentUser);
            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = foreignTenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = reference,
                ProviderReference = reference,
                Estado = EstadoPagoProveedor.Confirmado,
                Descripcion = "Pago de otro tenant",
                Monto = 55,
                Moneda = "CRC"
            });

            await context.SaveChangesAsync();

            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(currentUser.Id, currentTenantId)
                }
            };

            var result = await controller.Exito(reference, code: null, description: null);

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ResultadoCheckoutViewModel>(view.Model);
            Assert.Equal(StatusCodes.Status403Forbidden, controller.Response.StatusCode);
            Assert.Equal(reference, model.Referencia);
            Assert.True(model.AccesoRestringido);
            Assert.Null(model.NombrePlan);
            Assert.Null(model.EstadoPago);
            Assert.Contains("no pertenece", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task BillingSuccess_ShouldResolveTilopayOrderNumberUsingCheckoutIdWithinTenant()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var currentTenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            const string providerOrderNumber = "TYP4447_203709";

            context.Tenants.Add(new Tenant
            {
                Id = currentTenantId,
                Nombre = "Tenant A",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Premium",
                PrecioMensual = 55,
                Moneda = "CRC",
                Activo = true
            });

            var currentUser = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "owner-a@test.local",
                NormalizedUserName = "OWNER-A@TEST.LOCAL",
                Email = "owner-a@test.local",
                NormalizedEmail = "OWNER-A@TEST.LOCAL",
                TenantId = currentTenantId,
                State = true
            };

            context.Users.Add(currentUser);
            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = currentTenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = "LXA-ABCDEF-1234567890",
                ProviderReference = "LXA-ABCDEF-1234567890",
                ProviderCheckoutId = "203709",
                Estado = EstadoPagoProveedor.Pendiente,
                Descripcion = "Pago del tenant",
                Monto = 55,
                Moneda = "CRC"
            });

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = currentTenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ProviderReference = "LXA-ABCDEF-1234567890",
                ProviderPaymentLinkId = "203709",
                Estado = EstadoSuscripcion.Pendiente,
                FechaInicio = DateTime.UtcNow,
                FechaUltimaActualizacionUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(currentUser.Id, currentTenantId)
                }
            };

            var result = await controller.Exito(providerOrderNumber, code: "1", description: "Transaction is approved");

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ResultadoCheckoutViewModel>(view.Model);
            Assert.Equal(providerOrderNumber, model.Referencia);
            Assert.Equal("Plan Premium", model.NombrePlan);
            Assert.Equal(EstadoPagoProveedor.Pendiente, model.EstadoPago);
            Assert.True(model.PagoAprobadoPorProveedor);
            Assert.False(model.ConfirmadoPorWebhook);
            Assert.False(model.SuscripcionActiva);
            Assert.True(model.DebeAutoActualizar);
            Assert.Equal(EstadoSuscripcion.Pendiente, model.EstadoSuscripcion);
            Assert.NotNull(model.UrlActualizacion);
            Assert.DoesNotContain("No encontramos", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task BillingSuccess_ShouldShowActiveStateWhenTenantSubscriptionIsAlreadyActive()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var currentTenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            const string providerOrderNumber = "TYP5550_203888";

            context.Tenants.Add(new Tenant
            {
                Id = currentTenantId,
                Nombre = "Tenant A",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Elite",
                PrecioMensual = 75,
                Moneda = "CRC",
                Activo = true
            });

            var currentUser = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "owner-active@test.local",
                NormalizedUserName = "OWNER-ACTIVE@TEST.LOCAL",
                Email = "owner-active@test.local",
                NormalizedEmail = "OWNER-ACTIVE@TEST.LOCAL",
                TenantId = currentTenantId,
                State = true
            };

            context.Users.Add(currentUser);
            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = currentTenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = "LXA-ABCDEF-7777777777",
                ProviderReference = providerOrderNumber,
                ProviderCheckoutId = "203888",
                Estado = EstadoPagoProveedor.Pendiente,
                Descripcion = "Pago activo",
                Monto = 75,
                Moneda = "CRC"
            });

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = currentTenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ProviderReference = providerOrderNumber,
                ProviderPaymentLinkId = "203888",
                Estado = EstadoSuscripcion.Activa,
                FechaInicio = DateTime.UtcNow,
                FechaUltimaActualizacionUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(currentUser.Id, currentTenantId)
                }
            };

            var result = await controller.Exito(providerOrderNumber, code: "1", description: "Transaction is approved");

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ResultadoCheckoutViewModel>(view.Model);
            Assert.True(model.PagoAprobadoPorProveedor);
            Assert.True(model.ConfirmadoPorWebhook);
            Assert.True(model.SuscripcionActiva);
            Assert.False(model.DebeAutoActualizar);
            Assert.Equal(EstadoSuscripcion.Activa, model.EstadoSuscripcion);
            Assert.Contains("suscripcion activa", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CheckoutReturn_WithoutQueryString_ShouldRenderExplicitSuccessViewWhenSubscriptionIsAlreadyActive()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var currentPeriodEndUtc = new DateTime(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc);

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Return Active",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Prueba Tilopay",
                Codigo = PlanCodes.TestRecurring,
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true
            });

            var currentUser = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "return-active@test.local",
                NormalizedUserName = "RETURN-ACTIVE@TEST.LOCAL",
                Email = "return-active@test.local",
                NormalizedEmail = "RETURN-ACTIVE@TEST.LOCAL",
                TenantId = tenantId,
                State = true
            };

            context.Users.Add(currentUser);
            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoSuscripcion.Activa,
                FechaInicio = currentPeriodEndUtc.AddMonths(-1),
                FechaFin = currentPeriodEndUtc,
                FechaProximoCobroUtc = currentPeriodEndUtc,
                MaxFuncionarios = 1,
                FechaUltimaActualizacionUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(currentUser.Id, tenantId)
                }
            };

            var result = await controller.CheckoutReturn();

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ResultadoCheckoutViewModel>(view.Model);
            Assert.Equal("Exito", view.ViewName);
            Assert.True(model.SuscripcionActiva);
            Assert.Equal("Prueba Tilopay", model.NombrePlan);
            Assert.Equal(currentPeriodEndUtc, model.VigenciaHastaUtc);
            Assert.Equal(currentPeriodEndUtc, model.ProximoCobroUtc);
            Assert.Equal(1, model.MaxFuncionarios);
            Assert.Equal("/Dashboard", model.PrimaryActionUrl);
            Assert.Contains("suscripcion activa", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(PlanCodes.Basic, "Basico", 1, 8000)]
        [InlineData(PlanCodes.Pro, "Pro", 3, 20000)]
        [InlineData(PlanCodes.Business, "Business", 7, 35000)]
        public async Task CheckoutReturn_WithoutQueryString_ShouldRenderActiveSubscriptionForPublicRecurringPlans(
            string planCode,
            string planName,
            int maxFuncionarios,
            decimal monthlyPrice)
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var currentPeriodEndUtc = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = $"Tenant Return {planCode}",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = planName,
                Codigo = planCode,
                PrecioMensual = monthlyPrice,
                Moneda = "CRC",
                MaxFuncionarios = maxFuncionarios,
                Activo = true
            });

            var currentUser = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = $"return-{planCode.ToLowerInvariant()}@test.local",
                NormalizedUserName = $"RETURN-{planCode.ToUpperInvariant()}@TEST.LOCAL",
                Email = $"return-{planCode.ToLowerInvariant()}@test.local",
                NormalizedEmail = $"RETURN-{planCode.ToUpperInvariant()}@TEST.LOCAL",
                TenantId = tenantId,
                State = true
            };

            context.Users.Add(currentUser);
            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                CodigoPlan = planCode,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoSuscripcion.Activa,
                FechaInicio = currentPeriodEndUtc.AddMonths(-1),
                FechaFin = currentPeriodEndUtc,
                FechaProximoCobroUtc = currentPeriodEndUtc,
                PrecioMensual = monthlyPrice,
                MaxFuncionarios = maxFuncionarios,
                FechaUltimaActualizacionUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(currentUser.Id, tenantId)
                }
            };

            var result = await controller.CheckoutReturn();

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ResultadoCheckoutViewModel>(view.Model);
            Assert.Equal("Exito", view.ViewName);
            Assert.True(model.SuscripcionActiva);
            Assert.Equal(planName, model.NombrePlan);
            Assert.Equal(currentPeriodEndUtc, model.VigenciaHastaUtc);
            Assert.Equal(currentPeriodEndUtc, model.ProximoCobroUtc);
            Assert.Equal(maxFuncionarios, model.MaxFuncionarios);
            Assert.Equal("/Dashboard", model.PrimaryActionUrl);
            Assert.Contains("suscripcion activa", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CheckoutReturn_WithoutQueryString_ShouldShowFriendlyPendingMessageForRecurringPayment()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Return Pending",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Prueba Tilopay",
                Codigo = PlanCodes.TestRecurring,
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true
            });

            var currentUser = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "return-pending@test.local",
                NormalizedUserName = "RETURN-PENDING@TEST.LOCAL",
                Email = "return-pending@test.local",
                NormalizedEmail = "RETURN-PENDING@TEST.LOCAL",
                TenantId = tenantId,
                State = true
            };

            context.Users.Add(currentUser);
            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = "LXA-RETURN-PENDING",
                CorrelationToken = "corr-return-pending",
                ProviderReference = "corr-return-pending",
                Estado = EstadoPagoProveedor.Pendiente,
                Descripcion = "Pago recurrente pendiente",
                ClienteEmail = currentUser.Email,
                Monto = 1000,
                Moneda = "CRC",
                TilopayRecurringPlanId = 7099,
                FechaCreacionUtc = DateTime.UtcNow
            });

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoSuscripcion.Pendiente,
                FechaInicio = DateTime.UtcNow,
                FechaUltimaActualizacionUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(currentUser.Id, tenantId)
                }
            };

            var result = await controller.CheckoutReturn();

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ResultadoCheckoutViewModel>(view.Model);
            Assert.Equal("Exito", view.ViewName);
            Assert.False(model.SuscripcionActiva);
            Assert.Equal(EstadoPagoProveedor.Pendiente, model.EstadoPago);
            Assert.Equal("corr-return-pending", model.Referencia);
            Assert.Equal("Ya pague, revisar mi suscripcion", model.PrimaryActionLabel);
            Assert.Equal(model.UrlActualizacion, model.PrimaryActionUrl);
            Assert.Contains("aun no recibimos confirmacion automatica", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CheckoutReturn_WithoutQueryString_ShouldShowFriendlyFallbackWhenNoPaymentOrSubscriptionExists()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Return Empty",
                Activo = true
            });

            var currentUser = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "return-empty@test.local",
                NormalizedUserName = "RETURN-EMPTY@TEST.LOCAL",
                Email = "return-empty@test.local",
                NormalizedEmail = "RETURN-EMPTY@TEST.LOCAL",
                TenantId = tenantId,
                State = true
            };

            context.Users.Add(currentUser);
            await context.SaveChangesAsync();

            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(currentUser.Id, tenantId)
                }
            };

            var result = await controller.CheckoutReturn();

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ResultadoCheckoutViewModel>(view.Model);
            Assert.Equal("Exito", view.ViewName);
            Assert.False(model.SuscripcionActiva);
            // Tras el split de suscripcion, un tenant autenticado vuelve a su pagina privada
            // /Billing/Suscripcion (no al pricing publico /Billing/Planes).
            Assert.Equal("/Billing/Suscripcion", model.PrimaryActionUrl);
            Assert.Contains("No pudimos confirmar automaticamente este pago", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task BillingSuccess_ShouldKeepPollingWhenProviderApprovedButWebhookIsStillPending()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var currentTenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            const string providerOrderNumber = "TYP6601_203889";

            context.Tenants.Add(new Tenant
            {
                Id = currentTenantId,
                Nombre = "Tenant A",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Pro",
                PrecioMensual = 55,
                Moneda = "CRC",
                Activo = true
            });

            var currentUser = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "owner-pending@test.local",
                NormalizedUserName = "OWNER-PENDING@TEST.LOCAL",
                Email = "owner-pending@test.local",
                NormalizedEmail = "OWNER-PENDING@TEST.LOCAL",
                TenantId = currentTenantId,
                State = true
            };

            context.Users.Add(currentUser);
            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = currentTenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = "LXA-ABCDEF-8888888888",
                ProviderReference = providerOrderNumber,
                ProviderCheckoutId = "203889",
                Estado = EstadoPagoProveedor.Pendiente,
                Descripcion = "Pago pendiente de webhook",
                Monto = 55,
                Moneda = "CRC"
            });

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = currentTenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ProviderReference = providerOrderNumber,
                ProviderPaymentLinkId = "203889",
                Estado = EstadoSuscripcion.Pendiente,
                FechaInicio = DateTime.UtcNow,
                FechaUltimaActualizacionUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(currentUser.Id, currentTenantId)
                }
            };

            var result = await controller.Exito(providerOrderNumber, code: "1", description: "Transaction is approved");

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ResultadoCheckoutViewModel>(view.Model);
            Assert.True(model.PagoAprobadoPorProveedor);
            Assert.False(model.ConfirmadoPorWebhook);
            Assert.False(model.SuscripcionActiva);
            Assert.True(model.DebeAutoActualizar);
            Assert.Equal(4, model.SegundosAutoActualizacion);
            Assert.Equal(EstadoSuscripcion.Pendiente, model.EstadoSuscripcion);
            Assert.NotNull(model.UrlActualizacion);
            Assert.Contains("activando", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task BillingSuccess_ShouldRejectForeignTilopayOrderNumberWithoutChangingAuthenticatedUser()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var currentTenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            const string providerOrderNumber = "TYP9930_203729";

            context.Tenants.AddRange(
                new Tenant
                {
                    Id = currentTenantId,
                    Nombre = "Tenant actual",
                    Activo = true
                },
                new Tenant
                {
                    Id = foreignTenantId,
                    Nombre = "Tenant pago",
                    Activo = true
                });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Plan Premium",
                PrecioMensual = 55,
                Moneda = "CRC",
                Activo = true
            });

            var currentUser = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "owner@demo-salon.example",
                NormalizedUserName = "OWNER@DEMO-SALON.EXAMPLE",
                Email = "owner@demo-salon.example",
                NormalizedEmail = "OWNER@DEMO-SALON.EXAMPLE",
                TenantId = currentTenantId,
                State = true
            };

            context.Users.Add(currentUser);
            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = foreignTenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                ReferenciaInterna = "LXA-F95A5B-51EAAA7673",
                ProviderReference = "LXA-F95A5B-51EAAA7673",
                ProviderCheckoutId = "203729",
                Estado = EstadoPagoProveedor.Pendiente,
                Descripcion = "Pago del tenant correcto",
                Monto = 55,
                Moneda = "CRC",
                FechaCreacionUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            var principal = BuildPrincipal(currentUser.Id, currentTenantId);
            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = principal
                }
            };

            var result = await controller.Exito(providerOrderNumber, code: "1", description: "Transaction is approved");

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ResultadoCheckoutViewModel>(view.Model);

            Assert.Equal(StatusCodes.Status403Forbidden, controller.Response.StatusCode);
            Assert.True(model.AccesoRestringido);
            Assert.Null(model.NombrePlan);
            Assert.Null(model.EstadoPago);
            Assert.Equal(currentUser.Id, controller.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.Contains("no pertenece", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(providerOrderNumber, model.Referencia);
        }

        [Fact]
        public async Task CheckoutCalculadora_WhenUserCannotBeResolved_ShouldChallenge()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(Guid.NewGuid().ToString(), Guid.NewGuid())
                }
            };

            var result = await controller.CheckoutCalculadora(1, "Monthly", CancellationToken.None);

            Assert.IsType<ChallengeResult>(result);
        }

        [Theory]
        [InlineData(0, "Monthly")]
        [InlineData(12, "Monthly")]
        [InlineData(99, "Annual")]
        public async Task CheckoutCalculadora_OutOfRangeWorkers_ShouldRedirectWithoutCharging(int workers, string cycle)
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = Guid.NewGuid();
            var user = await SeedAuthenticatedUserAsync(context, userManager, tenantId, "calc-range@test.local");

            var controller = CreateBillingController(context, userManager, CreateCalculatorCatalog());
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = BuildPrincipal(user.Id, tenantId) }
            };
            controller.TempData = new TempDataDictionary(controller.HttpContext, Mock_TempDataProvider());

            var result = await controller.CheckoutCalculadora(workers, cycle, CancellationToken.None);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(BillingController.Suscripcion), redirect.ActionName);
            Assert.True(controller.TempData.ContainsKey("BillingError"));
        }

        [Fact]
        public async Task CheckoutCalculadora_UnconfiguredCombination_ShouldRedirectWithError()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = Guid.NewGuid();
            var user = await SeedAuthenticatedUserAsync(context, userManager, tenantId, "calc-unconfigured@test.local");

            // Catalogo vacio: ninguna combinacion configurada => no comprable.
            var controller = CreateBillingController(context, userManager);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = BuildPrincipal(user.Id, tenantId) }
            };
            controller.TempData = new TempDataDictionary(controller.HttpContext, Mock_TempDataProvider());

            var result = await controller.CheckoutCalculadora(9, "Monthly", CancellationToken.None);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(BillingController.Suscripcion), redirect.ActionName);
            Assert.True(controller.TempData.ContainsKey("BillingError"));
        }

        [Fact]
        public async Task CheckoutCalculadora_BelowActiveFuncionarios_ShouldBlock()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = Guid.NewGuid();
            tenantProvider.TenantId = tenantId;

            var user = await SeedAuthenticatedUserAsync(context, userManager, tenantId, "calc-floor@test.local");
            await SeedActiveFuncionariosAsync(context, count: 3);

            var controller = CreateBillingController(context, userManager, CreateCalculatorCatalog());
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = BuildPrincipal(user.Id, tenantId) }
            };
            controller.TempData = new TempDataDictionary(controller.HttpContext, Mock_TempDataProvider());

            // Tiene 3 funcionarios activos: no puede elegir un plan de 2.
            var result = await controller.CheckoutCalculadora(2, "Monthly", CancellationToken.None);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(BillingController.Suscripcion), redirect.ActionName);
            Assert.True(controller.TempData.ContainsKey("BillingError"));
        }

        [Fact]
        public async Task CheckoutCalculadora_WhenCancelAtPeriodEnd_ShouldBlockWithoutCharging()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = Guid.NewGuid();
            tenantProvider.TenantId = tenantId;

            var user = await SeedAuthenticatedUserAsync(context, userManager, tenantId, "cancel-block@test.local");

            // Suscripción con la renovación ya cancelada: NO debe abrirse un checkout de cambio de plan.
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Codigo = "LC_M_02",
                Nombre = "LC_M_02",
                PrecioMensual = 15000m,
                BillingCycle = BillingCycle.Monthly,
                Moneda = "CRC",
                MaxFuncionarios = 2,
                Activo = true
            };
            context.Planes.Add(plan);
            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = plan.Id,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = 7102,
                ProviderSubscriptionId = "sub-x",
                CancelAtPeriodEnd = true,
                FechaInicio = DateTime.UtcNow.AddMonths(-1),
                FechaFin = DateTime.UtcNow.AddDays(20)
            });
            await context.SaveChangesAsync();

            var controller = CreateBillingController(context, userManager, CreateCalculatorCatalog());
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = BuildPrincipal(user.Id, tenantId) }
            };
            controller.TempData = new TempDataDictionary(controller.HttpContext, Mock_TempDataProvider());

            var result = await controller.CheckoutCalculadora(2, "Monthly", CancellationToken.None);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(BillingController.Suscripcion), redirect.ActionName);
            Assert.True(controller.TempData.ContainsKey("BillingError"));
        }

        private static async Task<AppUsuario> SeedAuthenticatedUserAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            UserManager<AppUsuario> userManager,
            Guid tenantId,
            string email)
        {
            if (!await context.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
            {
                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Calc", Activo = true });
                await context.SaveChangesAsync();
            }

            var user = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                Name = "Owner",
                TenantId = tenantId,
                State = true
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user;
        }

        private static async Task SeedActiveFuncionariosAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int count)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                Detalle = "Operativo",
                Activo = true
            };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            for (var i = 0; i < count; i++)
            {
                context.Funcionarios.Add(new Funcionario
                {
                    Nombre = $"Func {i}",
                    IdPuesto = puesto.IdPuesto,
                    ColorCalendario = "#333333",
                    PorcentajeGanancia = 30m,
                    PorcentajeProducto = 10m,
                    FechaIngreso = new DateTime(2026, 1, 1),
                    Activo = true
                });
            }
            await context.SaveChangesAsync();
        }

        private static ITempDataProvider Mock_TempDataProvider() => new NullTempDataProvider();

        private sealed class NullTempDataProvider : ITempDataProvider
        {
            public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
            public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
        }

        private static (ProyectoIdentity.Datos.ApplicationDbContext Context, IDisposable Connection) CreateSystemContext()
        {
            var tenantProvider = new TestTenantProvider();
            return TestDbContextFactory.CreateSqliteContext(tenantProvider);
        }

        private static SaaSPaymentService CreatePaymentService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            FakePaymentProvider fakeProvider)
        {
            var subscriptionService = CreateSubscriptionService(context);

            return new SaaSPaymentService(
                context,
                new PaymentProviderResolver(new[] { fakeProvider }),
                subscriptionService,
                new TenantExecutionContextAccessor(),
                Options.Create(new OpcionesPago
                {
                    ProveedorPredeterminado = PaymentProviderType.Tilopay
                }),
                Options.Create(new OpcionesTilopay
                {
                    MerchantId = "merchant-1"
                }),
                Options.Create(new TilopayRepeatOptions()),
                NullLogger<SaaSPaymentService>.Instance);
        }

        private static BillingController CreateBillingController(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            UserManager<AppUsuario> userManager,
            LuxuryApp.Services.SaaS.ISubscriptionPricingCatalog? pricingCatalog = null)
        {
            var subscriptionService = CreateSubscriptionService(context);
            var summaryCache = new MemoryCache(new MemoryCacheOptions());
            var summaryAccessCache = new TenantCommercialAccessCache(summaryCache);
            var summaryClock = new FixedBusinessDateTimeProvider();
            var summaryAccessResolver = new TenantCommercialAccessResolver(
                context,
                summaryCache,
                summaryAccessCache,
                subscriptionService,
                summaryClock);

            return new BillingController(
                NullLogger<BillingController>.Instance,
                context,
                null!,
                subscriptionService,
                null!,
                null!,
                null!,
                null!,
                userManager,
                null!,
                new LuxuryApp.Services.SaaS.SubscriptionSummaryService(context, subscriptionService, null!, summaryAccessResolver),
                pricingCatalog ?? new LuxuryApp.Services.SaaS.SubscriptionPricingCatalog(Options.Create(new TilopayRepeatOptions())),
                new LuxuryApp.Services.SaaS.PlanChangeService(context, NullLogger<LuxuryApp.Services.SaaS.PlanChangeService>.Instance),
                new LuxuryApp.Services.SaaS.PlanChangeDecisionService(context, subscriptionService),
                new LuxuryApp.Tests.Support.TestWebHostEnvironment(),
                Options.Create(new OpcionesTilopay()),
                Options.Create(new OpcionesPago()),
                Options.Create(new TilopayRepeatOptions()),
                new DisabledTilopayRepeatAdminService(),
                new LuxuryApp.Tests.Support.NoOpProviderSubscriptionManager(),
                new LuxuryApp.Tests.Support.NoOpAddonSubscriptionManager(),
                new LuxuryApp.Services.Billing.PaymentMethodUpdateService(
                    context,
                    new DisabledTilopayRepeatAdminService(),
                    new FixedBusinessDateTimeProvider(),
                    NullLogger<LuxuryApp.Services.Billing.PaymentMethodUpdateService>.Instance,
                    new FakeTenantOwnerResolver()));
        }

        private static LuxuryApp.Services.SaaS.ISubscriptionPricingCatalog CreateCalculatorCatalog()
        {
            var options = new TilopayRepeatOptions { Enabled = true, UseHostedLinks = true };
            options.Calculator.AddRange(new[]
            {
                CalculatorOption(1, 7101, 8000m),
                CalculatorOption(2, 7102, 15000m),
                CalculatorOption(3, 7103, 20000m)
            });
            return new LuxuryApp.Services.SaaS.SubscriptionPricingCatalog(Options.Create(options));
        }

        private static TilopayRepeatPlanOption CalculatorOption(int workers, int planId, decimal charge) => new()
        {
            Code = PlanCodes.BuildCalculatorCode(workers, BillingCycle.Monthly)!,
            TilopayPlanId = planId,
            MonthlyPrice = charge,
            BillingCycle = BillingCycle.Monthly,
            Currency = "CRC",
            MaxFuncionarios = workers,
            CheckoutUrl = $"https://tp.cr/l/calc-{workers}",
            UsesRecurringCheckout = true,
            IsPublic = true
        };

        private static SuscripcionService CreateSubscriptionService(
            ProyectoIdentity.Datos.ApplicationDbContext context)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            return new SuscripcionService(
                context,
                cache,
                new TenantCommercialAccessCache(cache),
                new FixedBusinessDateTimeProvider(),
                Options.Create(new TilopayRepeatOptions()),
                NullLogger<SuscripcionService>.Instance);
        }

        private static UserManager<AppUsuario> CreateUserManager(ProyectoIdentity.Datos.ApplicationDbContext context)
        {
            var store = new UserStore<AppUsuario>(context);

            return new UserManager<AppUsuario>(
                store,
                Options.Create(new IdentityOptions()),
                new PasswordHasher<AppUsuario>(),
                Array.Empty<IUserValidator<AppUsuario>>(),
                Array.Empty<IPasswordValidator<AppUsuario>>(),
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                new ServiceCollection().BuildServiceProvider(),
                NullLogger<UserManager<AppUsuario>>.Instance);
        }

        private static ClaimsPrincipal BuildPrincipal(string userId, Guid tenantId) =>
            new(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId),
                    new Claim("tenant_id", tenantId.ToString())
                },
                "TestAuth"));

        private sealed class FakePaymentProvider : IPaymentProvider
        {
            public PaymentProviderType ProviderType => PaymentProviderType.Tilopay;

            public PaymentCheckoutResult CheckoutResult { get; set; } = new()
            {
                ProviderType = PaymentProviderType.Tilopay,
                RedirectUrl = "https://pay.local/default"
            };

            public PaymentProviderWebhookData WebhookData { get; set; } = new()
            {
                ProviderType = PaymentProviderType.Tilopay,
                EventId = "evt-default",
                EventType = "tilopay.link.completed",
                Reference = "LXA-ABCDEF-1234567890"
            };

            public PaymentVerificationResult VerificationResult { get; set; } = new()
            {
                ProviderType = PaymentProviderType.Tilopay,
                Exists = true,
                IsSuccess = true,
                Reference = "LXA-ABCDEF-1234567890",
                ProviderOrderNumber = "LXA-ABCDEF-1234567890",
                Amount = 10,
                Currency = "CRC"
            };

            public int VerifyCalls { get; private set; }

            public int CreateCheckoutCalls { get; private set; }

            public Task<PaymentCheckoutResult> CreateCheckoutAsync(
                PaymentCheckoutRequest request,
                CancellationToken cancellationToken = default)
            {
                CreateCheckoutCalls++;
                return Task.FromResult(CheckoutResult);
            }

            public PaymentProviderWebhookData ParseWebhook(string payload) => WebhookData;

            public Task<PaymentVerificationResult> VerifyPaymentAsync(
                PaymentVerificationRequest request,
                CancellationToken cancellationToken = default)
            {
                VerifyCalls++;
                return Task.FromResult(VerificationResult);
            }
        }
    }
}
