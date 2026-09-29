using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Payments;
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
    /// Transiciones ENCADENADAS del add-on de WhatsApp (WA400→WA800→WA400 / →WA1200) y las defensas
    /// que faltaban cuando un downgrade WA800→WA400 falló:
    ///
    ///  - un "aprobada" del proveedor sin captura, anulado o con total debitado 0 NO es un cobro;
    ///  - rechazar el webhook protege lo LOCAL pero no deshace lo que TiloPay ya hizo: hay que
    ///    preguntarle al proveedor y alertar si quedaron dos add-ons cobrables;
    ///  - ProviderCancellation=Cancelled en la fila ACTIVA se refería al suscriptor VIEJO y hacía
    ///    que la cascada del plan base no diera de baja el suscriptor vigente (doble cobro eterno);
    ///  - el retorno del checkout jamás puede mostrar el plan base como éxito de una compra de add-on.
    ///
    /// El plan base y los accesos manuales (canje/cortesía) nunca se tocan en ninguno de estos flujos.
    /// </summary>
    public class WhatsAppAddonChainedTransitionTests
    {
        private const int Wa400PlanId = 7011;
        private const int Wa800PlanId = 7012;
        private const int Wa1200PlanId = 7013;
        private const int BasePlanId = 7103; // LC_M_03

        private const string CustomerEmail = "buyer2@example.com";

        // ══ 1-4. Transiciones encadenadas: siempre 1 solo suscriptor cobrable al final ══

        [Fact]
        public async Task Wa400ToWa800_ActivatesNew_CancelsOld_LeavesSingleChargeableSubscriber()
        {
            var harness = await AddonHarness.CreateAsync();

            await harness.ActivateAsync(PlanCodes.WhatsApp400, Wa400PlanId, "sub-400");
            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            await harness.RunPendingCancellationAsync();

            var addon = await harness.GetAddonAsync();
            Assert.Equal(PlanCodes.WhatsApp800, addon.AddonCode);
            Assert.Equal("sub-800", addon.ProviderSubscriptionId);
            Assert.Contains("sub-400", harness.Admin.DeletedSubscriberIds);
            Assert.Equal(1, await harness.CountChargeableAsync());
        }

        [Fact]
        public async Task Wa800ToWa400_Downgrade_ActivatesTarget_CancelsOld_LeavesSingleChargeableSubscriber()
        {
            var harness = await AddonHarness.CreateAsync();

            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            await harness.ActivateAsync(PlanCodes.WhatsApp400, Wa400PlanId, "sub-400");
            await harness.RunPendingCancellationAsync();

            var addon = await harness.GetAddonAsync();
            Assert.Equal(PlanCodes.WhatsApp400, addon.AddonCode);
            Assert.Equal("sub-400", addon.ProviderSubscriptionId);
            Assert.Equal(Wa400PlanId, addon.TilopayRecurringPlanId);
            Assert.Contains("sub-800", harness.Admin.DeletedSubscriberIds);
            Assert.Equal(1, await harness.CountChargeableAsync());
        }

        [Fact]
        public async Task Wa400ToWa800ToWa400_Chained_LeavesOnlyTargetChargeable()
        {
            var harness = await AddonHarness.CreateAsync();

            await harness.ActivateAsync(PlanCodes.WhatsApp400, Wa400PlanId, "sub-400-a");
            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            await harness.RunPendingCancellationAsync();

            // Segundo salto de la cadena: es justo el que falló en producción.
            await harness.ActivateAsync(PlanCodes.WhatsApp400, Wa400PlanId, "sub-400-b");
            await harness.RunPendingCancellationAsync();

            var addon = await harness.GetAddonAsync();
            Assert.Equal(PlanCodes.WhatsApp400, addon.AddonCode);
            Assert.Equal("sub-400-b", addon.ProviderSubscriptionId);
            Assert.Contains("sub-400-a", harness.Admin.DeletedSubscriberIds);
            Assert.Contains("sub-800", harness.Admin.DeletedSubscriberIds);
            Assert.Equal(1, await harness.CountChargeableAsync());
            Assert.False(await harness.HasProviderDoubleActiveAsync());
        }

        [Fact]
        public async Task Wa400ToWa800ToWa1200_Chained_LeavesOnlyTargetChargeable()
        {
            var harness = await AddonHarness.CreateAsync();

            await harness.ActivateAsync(PlanCodes.WhatsApp400, Wa400PlanId, "sub-400");
            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            await harness.RunPendingCancellationAsync();
            await harness.ActivateAsync(PlanCodes.WhatsApp1200, Wa1200PlanId, "sub-1200");
            await harness.RunPendingCancellationAsync();

            var addon = await harness.GetAddonAsync();
            Assert.Equal(PlanCodes.WhatsApp1200, addon.AddonCode);
            Assert.Equal("sub-1200", addon.ProviderSubscriptionId);
            Assert.Equal(1, await harness.CountChargeableAsync());
        }

        // ══ 10-11. Semántica de ProviderCancellation en la fila ACTIVA ══

        [Fact]
        public async Task AfterUpgrade_ActiveRow_IsNotMarkedAsCancelledProvider()
        {
            var harness = await AddonHarness.CreateAsync();

            await harness.ActivateAsync(PlanCodes.WhatsApp400, Wa400PlanId, "sub-400");
            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            await harness.RunPendingCancellationAsync();

            var addon = await harness.GetAddonAsync();

            // La baja verificada fue del suscriptor VIEJO: la fila activa NO puede quedar "cancelada".
            Assert.Equal(ProviderCancellationState.NotRequired, addon.ProviderCancellation);
            Assert.Null(addon.ProviderCancelledAtUtc);
            Assert.Null(addon.ProviderCancellationSubscriptionId);
            Assert.False(AddonSubscriptionManager.IsCurrentSubscriberAlreadyCancelled(addon));

            // …y queda la auditoría de a quién se reemplazó.
            Assert.Equal("sub-400", addon.PreviousProviderSubscriptionId);
            Assert.NotNull(addon.PreviousProviderCancelledAtUtc);
        }

        [Fact]
        public async Task LegacyRow_CancelledWithoutSubscriberScope_IsNotTreatedAsCurrentCancellation()
        {
            var addon = new TenantSubscriptionAddon
            {
                ProviderSubscriptionId = "sub-800",
                ProviderCancellation = ProviderCancellationState.Cancelled,
                ProviderCancelledAtUtc = DateTime.UtcNow,
                ProviderCancellationSubscriptionId = null // fila anterior a la migración
            };

            // NULL = "no consta que el actual esté de baja": la cascada debe volver a intentarlo.
            Assert.False(AddonSubscriptionManager.IsCurrentSubscriberAlreadyCancelled(addon));

            addon.ProviderCancellationSubscriptionId = "sub-800";
            Assert.True(AddonSubscriptionManager.IsCurrentSubscriberAlreadyCancelled(addon));

            addon.ProviderCancellationSubscriptionId = "sub-400"; // era el viejo
            Assert.False(AddonSubscriptionManager.IsCurrentSubscriberAlreadyCancelled(addon));
        }

        [Fact]
        public async Task BaseCascade_AfterPackageChange_StillCancelsCurrentSubscriber()
        {
            // Regresión money-critical: con ProviderCancellation=Cancelled heredado del suscriptor
            // VIEJO, la cascada del plan base se saltaba la baja del vigente y TiloPay seguía
            // cobrando el add-on para siempre.
            var harness = await AddonHarness.CreateAsync();

            await harness.ActivateAsync(PlanCodes.WhatsApp400, Wa400PlanId, "sub-400");
            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            await harness.RunPendingCancellationAsync();

            await harness.Manager.ScheduleAddonCancellationForBaseCancellationAsync(
                harness.TenantId, "system", reason: "plan base cancelado", immediate: false);

            Assert.Contains("sub-800", harness.Admin.DeletedSubscriberIds);
            Assert.Equal(0, await harness.CountChargeableAsync());

            var addon = await harness.GetAddonAsync();
            Assert.Equal(ProviderCancellationState.Cancelled, addon.ProviderCancellation);
            Assert.Equal("sub-800", addon.ProviderCancellationSubscriptionId);
        }

        [Fact]
        public async Task PendingCancellation_TargetsCurrentSubscriber_NotStaleData()
        {
            var harness = await AddonHarness.CreateAsync();

            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            await harness.ActivateAsync(PlanCodes.WhatsApp400, Wa400PlanId, "sub-400");

            var beforeCancel = await harness.GetAddonAsync();
            Assert.Equal("sub-800", beforeCancel.PendingCancellationProviderSubscriptionId);
            Assert.Equal(Wa800PlanId, beforeCancel.PendingCancellationTilopayRecurringPlanId);

            await harness.RunPendingCancellationAsync();

            // Se dio de baja el VIEJO, jamás el recién pagado.
            Assert.Contains("sub-800", harness.Admin.DeletedSubscriberIds);
            Assert.DoesNotContain("sub-400", harness.Admin.DeletedSubscriberIds);
        }

        // ══ 7. Captura: "aprobada" no es "cobrada" ══

        [Fact]
        public void Settlement_ApprovedButNotCaptured_IsNotSettled()
        {
            var webhook = BuildWebhook(amount: 459m);
            webhook.StatusDescription = "Aprobada no capturada";

            var result = RecurringPaymentSettlementRules.Evaluate(webhook);

            Assert.False(result.IsSettled);
            Assert.Equal(RecurringSettlementVerdict.NotCaptured, result.Verdict);
        }

        [Fact]
        public void Settlement_ReversalOrderNumber_IsVoided()
        {
            var webhook = BuildWebhook(amount: 459m);
            webhook.ProviderOrderNumber = "Re-PFC000001-PRE123456";

            var result = RecurringPaymentSettlementRules.Evaluate(webhook);

            Assert.False(result.IsSettled);
            Assert.Equal(RecurringSettlementVerdict.VoidedOrReversed, result.Verdict);
        }

        [Fact]
        public void Settlement_TotalDebitedZero_IsNotSettled()
        {
            var webhook = BuildWebhook(amount: 459m);
            webhook.CapturedAmount = 0m;

            var result = RecurringPaymentSettlementRules.Evaluate(webhook);

            Assert.False(result.IsSettled);
            Assert.Equal(RecurringSettlementVerdict.NotCaptured, result.Verdict);
        }

        [Fact]
        public void Settlement_ExplicitCapturedFalse_IsNotSettled()
        {
            var webhook = BuildWebhook(amount: 6000m);
            webhook.IsCaptured = false;

            Assert.False(RecurringPaymentSettlementRules.Evaluate(webhook).IsSettled);
        }

        [Fact]
        public void Settlement_NoCaptureSignals_StaysSettled()
        {
            // Sin evidencia en contra NO se rechaza: el flujo WA400→WA800 que hoy funciona llega así.
            var webhook = BuildWebhook(amount: 12000m);

            Assert.True(RecurringPaymentSettlementRules.Evaluate(webhook).IsSettled);
        }

        [Fact]
        public void Settlement_PreAuthOrderMarker_AloneDoesNotReject()
        {
            // "-PRE" aparece también en cobros reales: sola no puede tumbar una venta.
            var webhook = BuildWebhook(amount: 6000m);
            webhook.ProviderOrderNumber = "PFC000001-PRE123456";

            Assert.True(RecurringPaymentSettlementRules.Evaluate(webhook).IsSettled);
            Assert.True(RecurringPaymentSettlementRules.LooksLikePreAuthorizationOrder(webhook.ProviderOrderNumber));
        }

        // ══ Parseo de monto: el nivel raíz manda ══

        [Fact]
        public void ParseWebhook_PrefersRootAmount_OverNestedAmount()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new TilopayService(
                new HttpClient(),
                cache,
                Options.Create(new OpcionesTilopay()),
                NullLogger<TilopayService>.Instance);

            var payload = """
            {
              "detalle": { "amount": "459.00" },
              "id_plan": 7011,
              "amount": "6000.00",
              "currency": "CRC",
              "orderNumber": "PFC000001",
              "code": "1",
              "response": "Transaccion aprobada"
            }
            """;

            var webhook = service.ParseWebhook(payload);

            Assert.Equal(6000.00m, webhook.Amount);
        }

        // ══ 6 + 12. Sondeo del proveedor tras el rechazo y health sin falso verde ══

        [Fact]
        public async Task ProviderAudit_TwoChargeableAddons_RaisesCriticalIncidentAndSnapshot()
        {
            var harness = await AddonHarness.CreateAsync();
            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");

            // Estado tras el intento fallido: WA400 y WA800 activos a la vez.
            harness.Admin.AddSubscriber(Wa400PlanId, "sub-400", CustomerEmail);

            var audit = await harness.AuditProviderAsync();

            Assert.True(audit.Executed);
            Assert.True(audit.HasDoubleActive);
            Assert.Equal(2, audit.ChargeableCount);

            var snapshot = await harness.Context.ProviderAddonAuditSnapshots
                .SingleAsync(s => s.TenantId == harness.TenantId);
            Assert.True(snapshot.HasDoubleActive);
            Assert.Equal(2, snapshot.ActiveAddonSubscriberCount);

            var incident = await harness.Context.SubscriptionPaymentIncidents.IgnoreQueryFilters()
                .SingleAsync(i => i.TenantId == harness.TenantId);
            Assert.Equal(PaymentIncidentScope.WhatsAppAddon, incident.Scope);
            Assert.Equal(AddonProviderAuditService.DoubleActiveResultCode, incident.ProviderResultCode);
            Assert.Equal(PaymentIncidentStatus.ManualReview, incident.Status);

            Assert.True(await harness.Context.PlatformAuditLogs.AnyAsync(log =>
                log.Action == PlatformAuditActions.AddonProviderDoubleActiveAfterRejectedWebhook));
        }

        [Fact]
        public async Task ProviderAudit_UnknownStatus_IsInconclusive_AndCountsAsChargeable()
        {
            var harness = await AddonHarness.CreateAsync();
            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            harness.Admin.AddSubscriber(Wa400PlanId, "sub-400", CustomerEmail, status: "Something New");

            var audit = await harness.AuditProviderAsync();

            Assert.True(audit.IsInconclusive);
            Assert.True(audit.HasDoubleActive); // lo desconocido NUNCA se asume libre
        }

        [Fact]
        public async Task ProviderAudit_InactiveOldSubscriber_IsNotDoubleActive()
        {
            var harness = await AddonHarness.CreateAsync();
            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            harness.Admin.AddSubscriber(Wa400PlanId, "sub-400", CustomerEmail, status: "Delete");

            var audit = await harness.AuditProviderAsync();

            Assert.False(audit.HasDoubleActive);
            Assert.Equal(1, audit.ChargeableCount);
        }

        [Fact]
        public async Task ProviderAudit_IgnoresOtherTenantsEmail()
        {
            var harness = await AddonHarness.CreateAsync();
            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            harness.Admin.AddSubscriber(Wa400PlanId, "sub-otro", "otrotenant@example.com");

            var audit = await harness.AuditProviderAsync();

            Assert.False(audit.HasDoubleActive);
            Assert.Equal(1, audit.ChargeableCount);
        }

        // ══ 8-9. Retorno del checkout: nunca un éxito falso ══

        [Fact]
        public void ReturnPage_AddonManualReview_ShowsManualReview_NotSuccess()
        {
            var model = new ResultadoCheckoutViewModel
            {
                EsAddon = true,
                EnRevisionManual = true,
                EstadoPago = EstadoPagoProveedor.ManualReview,
                NombrePlan = "LuxuryCloud WhatsApp 400 Mensajes Mensual",
                SuscripcionActiva = false
            };

            CheckoutReturnMessaging.ApplyAddon(model);

            Assert.Contains("revisión manual", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No aplicamos el cambio", model.MensajeSecundario!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("activo", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReturnPage_AddonSuccess_ShowsAddon_NotBasePlan()
        {
            var model = new ResultadoCheckoutViewModel
            {
                EsAddon = true,
                SuscripcionActiva = true,
                MensajesMensuales = 400,
                NombrePlan = "LuxuryCloud WhatsApp 400 Mensajes Mensual"
            };

            CheckoutReturnMessaging.ApplyAddon(model);

            Assert.Contains("WhatsApp", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
            Assert.Null(model.MaxFuncionarios);
        }

        [Fact]
        public void ReturnPage_BasePlanActive_ButPaymentInManualReview_IsNotSuccess()
        {
            // El fallo reproducido: base Activa del ciclo anterior + pago en revisión manual.
            var model = new ResultadoCheckoutViewModel
            {
                EnRevisionManual = true,
                EstadoPago = EstadoPagoProveedor.ManualReview,
                EstadoSuscripcion = EstadoSuscripcion.Activa,
                SuscripcionActiva = false,
                NombrePlan = "LuxuryCloud Mensual 3 funcionarios"
            };

            CheckoutReturnMessaging.ApplyBasePlan(model, hasLocalPayment: true, requestedReference: "PFC000001");

            Assert.DoesNotContain("Pago confirmado", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("revisión manual", model.MensajePrincipal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReturnPage_UncorrelatedPayment_IsExplicitError_NotBaseSuccess()
        {
            var model = new ResultadoCheckoutViewModel
            {
                CorrelacionFallida = true,
                EstadoSuscripcion = EstadoSuscripcion.Activa,
                SuscripcionActiva = false
            };

            CheckoutReturnMessaging.ApplyBasePlan(model, hasLocalPayment: false, requestedReference: "PFC000001-PRE123");

            Assert.Contains("No pudimos identificar este pago", model.MensajePrincipal);
            Assert.Contains("soporte", model.MensajeSecundario!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReturnView_RendersManualReviewState()
        {
            var view = File.ReadAllText(TestProjectPaths.ProjectPath("Views", "Billing", "Exito.cshtml"));

            Assert.Contains("Model.EnRevisionManual", view);
            Assert.Contains("Model.CorrelacionFallida", view);
            Assert.Contains("No se aplicó el cambio", view);
        }

        [Fact]
        public void BillingHealthView_ShowsProviderAuditSection()
        {
            var view = File.ReadAllText(TestProjectPaths.ProjectPath("Views", "PlatformBillingHealth", "Index.cshtml"));

            Assert.Contains("ProviderDoubleActiveWhatsAppAddonTenants", view);
            Assert.Contains("LastProviderAddonAuditUtc", view);
        }

        // ══ 13-14. Nada de esto toca el plan base ni los accesos manuales ══

        [Fact]
        public async Task ChainedTransition_DoesNotTouchBaseSubscription()
        {
            var harness = await AddonHarness.CreateAsync();

            await harness.ActivateAsync(PlanCodes.WhatsApp400, Wa400PlanId, "sub-400");
            await harness.ActivateAsync(PlanCodes.WhatsApp800, Wa800PlanId, "sub-800");
            await harness.RunPendingCancellationAsync();
            await harness.ActivateAsync(PlanCodes.WhatsApp400, Wa400PlanId, "sub-400-b");
            await harness.RunPendingCancellationAsync();

            var baseSubscription = await harness.Context.Suscripciones.IgnoreQueryFilters()
                .SingleAsync(s => s.TenantId == harness.TenantId);

            Assert.Equal(EstadoSuscripcion.Activa, baseSubscription.Estado);
            Assert.Equal(BasePlanId, baseSubscription.TilopayRecurringPlanId);
            Assert.Equal("base-sub", baseSubscription.ProviderSubscriptionId);
            Assert.False(baseSubscription.CancelAtPeriodEnd);
            Assert.DoesNotContain("base-sub", harness.Admin.DeletedSubscriberIds);
        }

        [Fact]
        public async Task ProviderAudit_ManualGrantTenant_IsNotAudited_NorAltered()
        {
            // Un acceso manual (canje/cortesía) no tiene suscriptor recurrente: nada que auditar ni cancelar.
            var harness = await AddonHarness.CreateAsync();
            var plan = await harness.SeedPlanAsync(PlanCodes.WhatsApp1200, 1200);

            harness.Context.TenantSubscriptionAddons.Add(new TenantSubscriptionAddon
            {
                Id = Guid.NewGuid(),
                TenantId = harness.TenantId,
                PlanId = plan.Id,
                AddonCode = PlanCodes.WhatsApp1200,
                Estado = EstadoSuscripcion.Activa,
                BillingSource = WhatsAppAddonBillingSource.ManualGrant,
                ManualGrantType = ManualWhatsAppGrantType.Barter,
                IsManualGrantIndefinite = true,
                MonthlyMessageLimit = 1200
            });
            await harness.Context.SaveChangesAsync();
            harness.Context.ChangeTracker.Clear();

            var audit = await harness.AuditProviderAsync();

            Assert.False(audit.HasDoubleActive);

            var addon = await harness.GetAddonAsync();
            Assert.Equal(WhatsAppAddonBillingSource.ManualGrant, addon.BillingSource);
            Assert.Equal(ManualWhatsAppGrantType.Barter, addon.ManualGrantType);
            Assert.True(addon.IsManualGrantIndefinite);
            Assert.Empty(harness.Admin.DeletedSubscriberIds);
        }

        // ══ Helpers ══

        private static PaymentProviderWebhookData BuildWebhook(decimal amount) => new()
        {
            ProviderType = PaymentProviderType.Tilopay,
            EventId = Guid.NewGuid().ToString(),
            EventType = "repeat_payment_success",
            StatusCode = "1",
            StatusDescription = "Transaccion aprobada",
            Amount = amount,
            Currency = "CRC",
            IsRecurring = true,
            RecurringPlanId = Wa400PlanId,
            CustomerEmail = CustomerEmail
        };

        /// <summary>
        /// Monta un tenant con plan base LC_M_03 activo y los tres paquetes de WhatsApp, y expone
        /// el activador, el cancelador saliente y el auditor del proveedor sobre el MISMO fake de
        /// TiloPay, para que "cuántos suscriptores cobran" sea una pregunta real y no un mock aparte.
        /// </summary>
        private sealed class AddonHarness
        {
            public required ApplicationDbContext Context { get; init; }
            public required Guid TenantId { get; init; }
            public required FakeAddonProviderAdmin Admin { get; init; }
            public required SuscripcionService Suscripciones { get; init; }
            public required AddonSubscriptionManager Manager { get; init; }
            public required AddonProviderAuditService ProviderAudit { get; init; }

            public static async Task<AddonHarness> CreateAsync()
            {
                var tenantId = Guid.NewGuid();
                var (context, _) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider { TenantId = tenantId });

                var admin = new FakeAddonProviderAdmin();
                var accessor = new TenantExecutionContextAccessor();
                var cache = new MemoryCache(new MemoryCacheOptions());
                var clock = new FixedBusinessDateTimeProvider();

                var repeatOptions = Options.Create(new TilopayRepeatOptions
                {
                    Enabled = true,
                    WhatsApp400 = new TilopayRepeatPlanOption { Code = PlanCodes.WhatsApp400, TilopayPlanId = Wa400PlanId, MonthlyPrice = 6000m, IsAddon = true },
                    WhatsApp800 = new TilopayRepeatPlanOption { Code = PlanCodes.WhatsApp800, TilopayPlanId = Wa800PlanId, MonthlyPrice = 12000m, IsAddon = true },
                    WhatsApp1200 = new TilopayRepeatPlanOption { Code = PlanCodes.WhatsApp1200, TilopayPlanId = Wa1200PlanId, MonthlyPrice = 18000m, IsAddon = true }
                });

                var harness = new AddonHarness
                {
                    Context = context,
                    TenantId = tenantId,
                    Admin = admin,
                    Suscripciones = new SuscripcionService(
                        context, cache, new TenantCommercialAccessCache(cache), clock,
                        repeatOptions, NullLogger<SuscripcionService>.Instance),
                    Manager = new AddonSubscriptionManager(
                        context, admin, accessor, clock, NullLogger<AddonSubscriptionManager>.Instance),
                    ProviderAudit = new AddonProviderAuditService(
                        context, admin, repeatOptions, accessor, clock,
                        NullLogger<AddonProviderAuditService>.Instance)
                };

                await harness.SeedTenantAndBaseAsync();
                return harness;
            }

            public async Task ActivateAsync(string addonCode, int recurringPlanId, string subscriberId)
            {
                var plan = await SeedPlanAsync(addonCode, ResolveLimit(addonCode));

                // El fake refleja lo que hace TiloPay: el nuevo suscriptor queda cobrando el plan.
                Admin.AddSubscriber(recurringPlanId, subscriberId, CustomerEmail);

                await Suscripciones.ActivarAddonWhatsAppRecurrenteAsync(
                    TenantId, plan, recurringPlanId, subscriberId, $"txn-{subscriberId}");
                Context.ChangeTracker.Clear();
            }

            public async Task RunPendingCancellationAsync()
            {
                await Manager.TryCancelPendingAddonSubscriberAsync(TenantId);
                Context.ChangeTracker.Clear();
            }

            public Task<AddonProviderAuditResult> AuditProviderAsync() =>
                ProviderAudit.AuditAsync(
                    TenantId,
                    CustomerEmail,
                    source: "webhook-rejected",
                    auditAction: PlatformAuditActions.AddonProviderDoubleActiveAfterRejectedWebhook);

            public Task<TenantSubscriptionAddon> GetAddonAsync() =>
                Context.TenantSubscriptionAddons.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(a => a.TenantId == TenantId);

            /// <summary>Cuántos suscriptores del tenant siguen COBRABLES en el fake de TiloPay.</summary>
            public async Task<int> CountChargeableAsync()
            {
                var total = 0;
                foreach (var planId in new[] { Wa400PlanId, Wa800PlanId, Wa1200PlanId })
                {
                    var subscribers = await Admin.GetSuscriptorRepeatAsync(planId);
                    total += subscribers.Count(s =>
                        string.Equals(s.Email, CustomerEmail, StringComparison.OrdinalIgnoreCase) &&
                        ProviderSubscriberStatusRules.MayStillCharge(s.Status));
                }

                return total;
            }

            public async Task<bool> HasProviderDoubleActiveAsync()
            {
                var audit = await AuditProviderAsync();
                Context.ChangeTracker.Clear();
                return audit.HasDoubleActive;
            }

            public async Task<Plan> SeedPlanAsync(string code, int monthlyLimit)
            {
                var existing = await Context.Planes.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Codigo == code);
                if (existing is not null)
                {
                    return existing;
                }

                var plan = new Plan
                {
                    Id = Guid.NewGuid(),
                    Codigo = code,
                    Nombre = $"LuxuryCloud WhatsApp {monthlyLimit} Mensajes Mensual",
                    Moneda = "CRC",
                    PrecioMensual = monthlyLimit * 15m,
                    LimiteMensajesMensual = monthlyLimit,
                    Activo = true
                };

                Context.Planes.Add(plan);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
                return plan;
            }

            private static int ResolveLimit(string addonCode) => addonCode switch
            {
                PlanCodes.WhatsApp400 => 400,
                PlanCodes.WhatsApp800 => 800,
                _ => 1200
            };

            private async Task SeedTenantAndBaseAsync()
            {
                Context.Tenants.Add(new Tenant { Id = TenantId, Nombre = "Tenant Beta", Activo = true });

                var basePlanId = Guid.NewGuid();
                Context.Planes.Add(new Plan
                {
                    Id = basePlanId,
                    Codigo = "LC_M_03",
                    Nombre = "LuxuryCloud Mensual 3 funcionarios",
                    Moneda = "CRC",
                    PrecioMensual = 20000m,
                    MaxFuncionarios = 3,
                    Activo = true
                });

                Context.Suscripciones.Add(new Suscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = TenantId,
                    PlanId = basePlanId,
                    CodigoPlan = "LC_M_03",
                    Estado = EstadoSuscripcion.Activa,
                    Proveedor = PaymentProviderType.Tilopay,
                    TilopayRecurringPlanId = BasePlanId,
                    ProviderSubscriptionId = "base-sub",
                    FechaInicio = DateTime.UtcNow.AddDays(-10),
                    FechaFin = DateTime.UtcNow.AddDays(47),
                    FechaProximoCobroUtc = DateTime.UtcNow.AddDays(47),
                    FechaUltimaActualizacionUtc = DateTime.UtcNow
                });

                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();

                // El plan base también vive en TiloPay: ninguna operación de add-on puede tocarlo.
                Admin.AddSubscriber(BasePlanId, "base-sub", CustomerEmail);
            }
        }

        /// <summary>Fake de TiloPay Repeat con estado real por plan (email incluido).</summary>
        private sealed class FakeAddonProviderAdmin : ITilopayRepeatAdminService
        {
            private readonly Dictionary<int, List<TilopaySubscriber>> _byPlan = new();

            public bool IsEnabled { get; set; } = true;
            public List<string> DeletedSubscriberIds { get; } = new();

            public void AddSubscriber(int planId, string subscriberId, string email, string status = "Active")
            {
                if (!_byPlan.TryGetValue(planId, out var list))
                {
                    list = new List<TilopaySubscriber>();
                    _byPlan[planId] = list;
                }

                list.RemoveAll(s => string.Equals(s.SubscriberId, subscriberId, StringComparison.OrdinalIgnoreCase));
                list.Add(new TilopaySubscriber { SubscriberId = subscriberId, Email = email, Status = status });
            }

            public Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(
                int tilopayPlanId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<TilopaySubscriber>>(
                    _byPlan.TryGetValue(tilopayPlanId, out var list) ? list.ToList() : new List<TilopaySubscriber>());

            public Task<TilopayAdminOperationResult> DeleteSubscriberAsync(
                string subscriberId, CancellationToken cancellationToken = default)
            {
                DeletedSubscriberIds.Add(subscriberId);
                foreach (var list in _byPlan.Values)
                {
                    list.RemoveAll(s => string.Equals(s.SubscriberId, subscriberId, StringComparison.OrdinalIgnoreCase));
                }

                return Task.FromResult(TilopayAdminOperationResult.Ok("deleted"));
            }

            public Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(
                string subscriberId, TilopaySubscriberStatus status, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("edited"));

            public Task<SubscriberResolutionResult> ResolveSubscriberAsync(
                int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();

            public Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(
                int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();

            public Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(
                int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();

            public Task<TilopayAdminOperationResult> PauseSubscriberAsync(
                string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("paused"));

            public Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(
                string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("reactivated"));
        }
    }
}
