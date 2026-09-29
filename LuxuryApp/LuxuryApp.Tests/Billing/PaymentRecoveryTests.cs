using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Common;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Recuperación de pago (Fase 3): actualización de tarjeta (recurrentUrl on-demand + validación de
    /// dominio), incidentes de pago fallido con gracia (idempotentes, success-gana), y cierre de
    /// gracia por worker (dry-run salvo AutoSuspendAfterGrace). Nada de esto corta acceso salvo que
    /// el flag lo habilite; el gate vive en SuscripcionService.GetEffectiveStatus.
    /// </summary>
    public class PaymentRecoveryTests
    {
        private const int RecurringPlanId = 7102;

        // ── Actualización de método de pago (recurrentUrl) ───────────────────────────

        [Fact]
        public async Task UpdateUrl_HappyPath_ReturnsTilopayUrl_AndAudits()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(10), estado: EstadoSuscripcion.Morosa, paymentRecoveryStatus: "GraceActive");

            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.True(result.Succeeded);
            Assert.StartsWith("https://app.tilopay.com/", result.Url);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentMethodUpdateUrlGenerated));
        }

        [Fact]
        public async Task UpdateUrl_PrimaryContract_AuditsGeneratedNormally_NotFallback()
        {
            using var h = await Harness.CreateAsync();
            h.Admin.Contract = "id_plan";
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(10), estado: EstadoSuscripcion.Morosa, paymentRecoveryStatus: "GraceActive");

            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.True(result.Succeeded);
            Assert.False(result.UsedFallbackContract);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentMethodUpdateUrlGenerated));
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.PaymentMethodUpdateUrlGeneratedWithFallback));
        }

        [Fact]
        public async Task UpdateUrl_FallbackContract_AuditsWithFallback_AndFlagsResult()
        {
            using var h = await Harness.CreateAsync();
            h.Admin.Contract = "id_plan+aliases"; // solo funcionó el fallback: enlace sospechoso
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(10), estado: EstadoSuscripcion.Morosa, paymentRecoveryStatus: "GraceActive");

            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.True(result.Succeeded);
            Assert.True(result.UsedFallbackContract);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentMethodUpdateUrlGeneratedWithFallback));
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.PaymentMethodUpdateUrlGenerated));
        }

        [Fact]
        public async Task UpdateUrl_AdminDisabled_Fails()
        {
            using var h = await Harness.CreateAsync();
            h.Admin.IsEnabled = false;
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(10), estado: EstadoSuscripcion.Morosa, paymentRecoveryStatus: "GraceActive");

            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task UpdateUrl_NonTilopayDomain_RejectedAsUnsafe()
        {
            using var h = await Harness.CreateAsync();
            h.Admin.Url = "https://evil.example.com/steal"; // dominio NO TiloPay ⇒ open-redirect bloqueado
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(10), estado: EstadoSuscripcion.Morosa, paymentRecoveryStatus: "GraceActive");

            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.False(result.Succeeded);
            Assert.Null(result.Url);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentMethodUpdateUrlFailed));
        }

        [Fact]
        public async Task UpdateUrl_CancelAtPeriodEndActive_TellsToReactivateFirst()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(10),
                cancelAtPeriodEnd: true,
                cancellationEffectiveAtUtc: h.NowUtc.AddDays(10));

            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.False(result.Succeeded);
            Assert.False(result.RequiresNewCheckout);
            Assert.Contains("reactiv", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task UpdateUrl_NoRecurringSubscription_RequiresNewCheckout()
        {
            using var h = await Harness.CreateAsync();

            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresNewCheckout);
        }

        [Fact]
        public async Task UpdateUrl_ActiveSubscription_BlockedWithContactSupportMessage()
        {
            using var h = await Harness.CreateAsync();
            // Cuenta ACTIVA/vigente (sin recovery): url_renew de TiloPay COBRA, no es update-only.
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(10)); // estado Activa por defecto

            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.False(result.Succeeded);
            Assert.False(result.RequiresNewCheckout);
            Assert.Contains("soporte", result.Message!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task UpdateUrl_RecoverySubscription_GeneratesUrl()
        {
            using var h = await Harness.CreateAsync();
            // Cuenta en RECUPERACIÓN: url_renew se usa para regularizar/pagar ahora → sí se permite.
            await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(10),
                estado: EstadoSuscripcion.Morosa,
                paymentRecoveryStatus: "GraceActive");

            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.True(result.Succeeded);
            Assert.StartsWith("https://app.tilopay.com/", result.Url);
        }

        [Fact]
        public async Task UpdateUrl_OnlyReadsOwnTenant()
        {
            using var h = await Harness.CreateAsync();
            var other = Guid.NewGuid();
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(10), tenantId: other);

            // El tenant actual NO tiene suscripción recurrente: no debe usar la del otro tenant.
            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresNewCheckout);
        }

        // ── Incidente de pago fallido ────────────────────────────────────────────────

        [Fact]
        public async Task FailedPayment_OpensIncident_AndStartsGrace()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-1));

            await h.Recovery.RegisterFailedPaymentAsync(h.TenantId, RecurringPlanId, "900117", "05", "Fondos insuficientes");

            var incident = await h.GetOpenIncidentAsync();
            Assert.NotNull(incident);
            Assert.Equal(PaymentIncidentStatus.Open, incident!.Status);
            Assert.NotNull(incident.GraceEndsAtUtc);
            Assert.Equal(1, incident.FailureCount);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.NotNull(sub.LastPaymentFailedAtUtc);
            Assert.Equal("GraceActive", sub.PaymentRecoveryStatus);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionPaymentFailedGraceStarted));
        }

        [Fact]
        public async Task FailedPayment_Repeated_IncrementsFailureCount_WithoutDuplicating()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-1));

            await h.Recovery.RegisterFailedPaymentAsync(h.TenantId, RecurringPlanId, "900117", "05", "fallo 1");
            await h.Recovery.RegisterFailedPaymentAsync(h.TenantId, RecurringPlanId, "900117", "05", "fallo 2");

            Assert.Equal(1, await h.CountOpenIncidentsAsync());
            var incident = await h.GetOpenIncidentAsync();
            Assert.Equal(2, incident!.FailureCount);
        }

        [Fact]
        public async Task FailedPayment_OfOldPlan_DoesNotAffectCurrentSubscription()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-1)); // plan actual = RecurringPlanId

            await h.Recovery.RegisterFailedPaymentAsync(h.TenantId, 9999, "old-sub", "05", "plan viejo");

            Assert.Equal(0, await h.CountOpenIncidentsAsync());
        }

        [Fact]
        public async Task FailedPayment_WhenConfirmedIsNewer_SuccessWins_NoIncident()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20));
            // Un pago confirmado MÁS RECIENTE que cualquier fallo: el éxito ya ganó.
            await h.SeedPaymentAsync(EstadoPagoProveedor.Confirmado, confirmedAtUtc: h.NowUtc);

            await h.Recovery.RegisterFailedPaymentAsync(h.TenantId, RecurringPlanId, "900117", "05", "fallo tardío");

            Assert.Equal(0, await h.CountOpenIncidentsAsync());
        }

        [Fact]
        public async Task FailedPayment_CancelAtPeriodEndAndProviderDelete_NotActionable_NoIncident()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(-1),
                cancelAtPeriodEnd: true,
                providerStatusRaw: "Delete");

            await h.Recovery.RegisterFailedPaymentAsync(h.TenantId, RecurringPlanId, "900117", "05", "fallo");

            Assert.Equal(0, await h.CountOpenIncidentsAsync());
        }

        [Fact]
        public async Task FailedPayment_OnlyAffectsOwnTenant()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-1));
            var other = Guid.NewGuid();
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-1), tenantId: other);

            await h.Recovery.RegisterFailedPaymentAsync(h.TenantId, RecurringPlanId, "900117", "05", "fallo");

            var otherIncidents = await h.Db.SubscriptionPaymentIncidents.IgnoreQueryFilters()
                .CountAsync(i => i.TenantId == other);
            Assert.Equal(0, otherIncidents);
        }

        // ── Resolución por pago exitoso ──────────────────────────────────────────────

        [Fact]
        public async Task Success_ResolvesOpenIncident_AndClearsRecoveryFields()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20));
            await h.Recovery.RegisterFailedPaymentAsync(h.TenantId, RecurringPlanId, "900117", "05", "fallo");

            await h.Recovery.ResolveOnSuccessAsync(h.TenantId, RecurringPlanId);

            Assert.Equal(0, await h.CountOpenIncidentsAsync());
            var sub = await h.GetSubscriptionAsync(id);
            Assert.Null(sub.LastPaymentFailedAtUtc);
            Assert.Null(sub.PaymentRecoveryStatus);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionPaymentRecoveryResolved));
        }

        [Fact]
        public async Task Success_OfOtherPlan_DoesNotResolveCurrentIncident()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20));
            await h.Recovery.RegisterFailedPaymentAsync(h.TenantId, RecurringPlanId, "900117", "05", "fallo");

            await h.Recovery.ResolveOnSuccessAsync(h.TenantId, 9999); // éxito de OTRO plan

            Assert.Equal(1, await h.CountOpenIncidentsAsync());
        }

        // ── Cierre de gracia (worker) ────────────────────────────────────────────────

        [Fact]
        public async Task GraceExpiration_WhenAutoSuspendFalse_DryRun_DoesNotSuspend()
        {
            using var h = await Harness.CreateAsync(autoSuspend: false);
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-2), estado: EstadoSuscripcion.Morosa);
            await h.SeedIncidentAsync(id, graceEndsAtUtc: h.NowUtc.AddDays(-1));

            var processed = await h.Recovery.RunGraceExpirationPassAsync();

            Assert.Equal(1, processed);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.NotEqual(EstadoSuscripcion.Suspendida, sub.Estado); // NO se corta acceso
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionPaymentGraceExpiredDryRun));
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.SubscriptionSuspendedForNonPayment));
        }

        [Fact]
        public async Task GraceExpiration_WhenAutoSuspendTrue_Suspends()
        {
            using var h = await Harness.CreateAsync(autoSuspend: true);
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-2), estado: EstadoSuscripcion.Morosa);
            await h.SeedIncidentAsync(id, graceEndsAtUtc: h.NowUtc.AddDays(-1));

            await h.Recovery.RunGraceExpirationPassAsync();

            var sub = await h.GetSubscriptionAsync(id);
            Assert.Equal(EstadoSuscripcion.Suspendida, sub.Estado);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionSuspendedForNonPayment));
        }

        [Fact]
        public async Task GraceExpiration_MarksExpired_ButKeepsAccess_WhilePaidPeriodActive()
        {
            using var h = await Harness.CreateAsync(autoSuspend: true);
            // El período pagado (provider/local) todavía es futuro aunque la gracia del incidente venció:
            // se MARCA GraceExpired (es un estado) pero NO se suspende (no se quita acceso ya pagado).
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), estado: EstadoSuscripcion.Morosa);
            var incidentId = await h.SeedIncidentAsync(id, graceEndsAtUtc: h.NowUtc.AddDays(-1));

            var processed = await h.Recovery.RunGraceExpirationPassAsync();

            Assert.Equal(1, processed);
            var incident = await h.GetIncidentAsync(incidentId);
            Assert.Equal(PaymentIncidentStatus.GraceExpired, incident.Status);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.NotEqual(EstadoSuscripcion.Suspendida, sub.Estado);   // acceso NO cortado
            Assert.Equal("GraceExpired", sub.PaymentRecoveryStatus);
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.SubscriptionSuspendedForNonPayment));
        }

        [Fact]
        public async Task GraceExpiration_OpenExpired_MarksGraceExpired_WhenAutoSuspendFalse_EvenWithFuturePeriod()
        {
            using var h = await Harness.CreateAsync(autoSuspend: false);
            // Reproduce el caso reportado en prod: FechaFin futura pero la GRACIA ya venció.
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                estado: EstadoSuscripcion.Morosa,
                paymentRecoveryStatus: "GraceActive");
            var incidentId = await h.SeedIncidentAsync(id, graceEndsAtUtc: h.NowUtc.AddHours(-2));

            var processed = await h.Recovery.RunGraceExpirationPassAsync();

            Assert.Equal(1, processed);
            var incident = await h.GetIncidentAsync(incidentId);
            Assert.Equal(PaymentIncidentStatus.GraceExpired, incident.Status); // ya no queda Open
            var sub = await h.GetSubscriptionAsync(id);
            Assert.Equal("GraceExpired", sub.PaymentRecoveryStatus);           // UI deja de decir "en gracia"
            Assert.NotEqual(EstadoSuscripcion.Suspendida, sub.Estado);         // acceso conservado
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.SubscriptionPaymentGraceExpiredDryRun));
        }

        [Fact]
        public async Task GraceExpiration_WithCancelAtPeriodEnd_MarksIgnored_WithoutSuspending()
        {
            using var h = await Harness.CreateAsync(autoSuspend: true);
            var id = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(-2), estado: EstadoSuscripcion.Morosa, cancelAtPeriodEnd: true);
            var incidentId = await h.SeedIncidentAsync(id, graceEndsAtUtc: h.NowUtc.AddDays(-1));

            await h.Recovery.RunGraceExpirationPassAsync();

            var incident = await h.Db.SubscriptionPaymentIncidents.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(i => i.Id == incidentId);
            Assert.Equal(PaymentIncidentStatus.Ignored, incident.Status);
            var sub = await h.GetSubscriptionAsync(id);
            Assert.NotEqual(EstadoSuscripcion.Suspendida, sub.Estado);
        }

        [Fact]
        public async Task GraceExpiration_IsIdempotent()
        {
            using var h = await Harness.CreateAsync(autoSuspend: false);
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-2), estado: EstadoSuscripcion.Morosa);
            await h.SeedIncidentAsync(id, graceEndsAtUtc: h.NowUtc.AddDays(-1));

            await h.Recovery.RunGraceExpirationPassAsync();
            var afterFirst = await h.CountAuditAsync(PlatformAuditActions.SubscriptionPaymentGraceExpiredDryRun);
            await h.Recovery.RunGraceExpirationPassAsync();
            var afterSecond = await h.CountAuditAsync(PlatformAuditActions.SubscriptionPaymentGraceExpiredDryRun);

            Assert.Equal(1, afterFirst);
            Assert.Equal(afterFirst, afterSecond); // no re-procesa un incidente ya vencido
        }

        // ── Estado efectivo con AutoSuspend ──────────────────────────────────────────

        [Fact]
        public async Task EffectiveStatus_MorosaPastGrace_KeepsAccess_WhenAutoSuspendFalse()
        {
            using var h = await Harness.CreateAsync(autoSuspend: false);
            var sub = new Suscripcion
            {
                TenantId = h.TenantId,
                Estado = EstadoSuscripcion.Morosa,
                FechaFin = h.NowUtc.AddDays(-5),
                FechaFinGraciaUtc = h.NowUtc.AddDays(-1) // gracia ya vencida
            };

            var effective = h.SubscriptionService.GetEffectiveStatus(sub);

            Assert.Equal(EstadoSuscripcion.Morosa, effective); // NO suspende: mantiene acceso
            Assert.True(h.SubscriptionService.CanAccessApp(sub));
        }

        [Fact]
        public async Task EffectiveStatus_MorosaPastGrace_Suspends_WhenAutoSuspendTrue()
        {
            using var h = await Harness.CreateAsync(autoSuspend: true);
            var sub = new Suscripcion
            {
                TenantId = h.TenantId,
                Estado = EstadoSuscripcion.Morosa,
                FechaFin = h.NowUtc.AddDays(-5),
                FechaFinGraciaUtc = h.NowUtc.AddDays(-1)
            };

            var effective = h.SubscriptionService.GetEffectiveStatus(sub);

            Assert.Equal(EstadoSuscripcion.Suspendida, effective);
            Assert.False(h.SubscriptionService.CanAccessApp(sub));
        }

        // ── Health ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Health_ReportsPaymentRecoveryCounters()
        {
            using var h = await Harness.CreateAsync();
            var id = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-1), estado: EstadoSuscripcion.Morosa);
            await h.SeedIncidentAsync(id, graceEndsAtUtc: h.NowUtc.AddDays(2)); // gracia vigente

            var snapshot = await h.Health.BuildAsync();

            Assert.True(snapshot.OpenPaymentRecoveryIncidents >= 1);
            Assert.True(snapshot.ActiveGracePeriods >= 1);
        }

        // ── Notificaciones por email (flag SendEmailNotifications) ───────────────────

        [Fact]
        public async Task Notifications_InitialEmail_SentOnce_AndNotDuplicatedOnRestart()
        {
            using var h = await Harness.CreateAsync(sendEmails: true);
            var subId = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), estado: EstadoSuscripcion.Morosa);
            var incidentId = await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(4), clienteEmail: "buyer@example.com");

            await h.Notifications.RunPendingNotificationsAsync();

            Assert.Equal(1, h.Email.PaymentFailedCalls);
            Assert.Equal("buyer@example.com", Assert.Single(h.Email.Recipients));
            var incident = await h.GetIncidentAsync(incidentId);
            Assert.NotNull(incident.LastNotificationAtUtc);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryNotificationSent));

            // Reinicio del worker: no re-envía el inicial ni audita de nuevo.
            await h.Notifications.RunPendingNotificationsAsync();
            Assert.Equal(1, h.Email.PaymentFailedCalls);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryNotificationSent));
        }

        [Fact]
        public async Task Notifications_Initial_WhenSendEmailsFalse_NoEmail_OnlyDryRun_Once()
        {
            using var h = await Harness.CreateAsync(sendEmails: false);
            var subId = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), estado: EstadoSuscripcion.Morosa);
            await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(4), clienteEmail: "buyer@example.com");

            await h.Notifications.RunPendingNotificationsAsync();

            Assert.Equal(0, h.Email.TotalCalls); // NO se envía correo real
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryNotificationDryRun));
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryNotificationSent));

            // No spamea el dry-run al repetir el pase.
            await h.Notifications.RunPendingNotificationsAsync();
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryNotificationDryRun));
        }

        [Fact]
        public async Task Notifications_Reminder_SentOnce_WithinWindow()
        {
            using var h = await Harness.CreateAsync(sendEmails: true);
            var subId = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), estado: EstadoSuscripcion.Morosa);
            // Inicial ya enviado; la gracia vence en 10h (dentro de la ventana de 24h).
            var incidentId = await h.SeedIncidentAsync(
                subId,
                graceEndsAtUtc: h.NowUtc.AddHours(10),
                clienteEmail: "buyer@example.com",
                lastNotificationAtUtc: h.NowUtc.AddDays(-4));

            await h.Notifications.RunPendingNotificationsAsync();

            Assert.Equal(1, h.Email.GraceReminderCalls);
            Assert.Equal(0, h.Email.PaymentFailedCalls);
            var incident = await h.GetIncidentAsync(incidentId);
            Assert.NotNull(incident.LastReminderAtUtc);

            await h.Notifications.RunPendingNotificationsAsync();
            Assert.Equal(1, h.Email.GraceReminderCalls); // no se repite
        }

        [Fact]
        public async Task Notifications_Reminder_NotSent_OutsideWindow()
        {
            using var h = await Harness.CreateAsync(sendEmails: true);
            var subId = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), estado: EstadoSuscripcion.Morosa);
            // Inicial enviado, pero la gracia vence dentro de 5 días (fuera de la ventana de 24h).
            await h.SeedIncidentAsync(
                subId,
                graceEndsAtUtc: h.NowUtc.AddDays(5),
                clienteEmail: "buyer@example.com",
                lastNotificationAtUtc: h.NowUtc.AddHours(-1));

            await h.Notifications.RunPendingNotificationsAsync();

            Assert.Equal(0, h.Email.GraceReminderCalls);
        }

        [Fact]
        public async Task Notifications_SuspensionEmail_OnlyWhenSuspended_SentOnce()
        {
            using var h = await Harness.CreateAsync(autoSuspend: true, sendEmails: true);
            var subId = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-2), estado: EstadoSuscripcion.Morosa);
            await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(-1), clienteEmail: "buyer@example.com");

            // La gracia vence y suspende (AutoSuspend=true).
            await h.Recovery.RunGraceExpirationPassAsync();
            await h.Notifications.RunPendingNotificationsAsync();

            Assert.Equal(1, h.Email.SuspendedCalls);
            await h.Notifications.RunPendingNotificationsAsync();
            Assert.Equal(1, h.Email.SuspendedCalls); // idempotente
        }

        [Fact]
        public async Task Notifications_NoSuspensionEmail_WhenAutoSuspendFalse()
        {
            using var h = await Harness.CreateAsync(autoSuspend: false, sendEmails: true);
            var subId = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-2), estado: EstadoSuscripcion.Morosa);
            // Inicial ya enviado para que el pase solo pudiera mandar la suspensión (que NO debe ocurrir).
            await h.SeedIncidentAsync(
                subId, graceEndsAtUtc: h.NowUtc.AddDays(-1), clienteEmail: "buyer@example.com",
                lastNotificationAtUtc: h.NowUtc.AddDays(-3));

            await h.Recovery.RunGraceExpirationPassAsync(); // dry-run: NO suspende
            await h.Notifications.RunPendingNotificationsAsync();

            Assert.Equal(0, h.Email.SuspendedCalls);
        }

        // ── Acciones manuales de plataforma (SuperAdmin) ─────────────────────────────

        [Fact]
        public async Task ManualResolve_ClosesIncident_ClearsRecovery_Audits()
        {
            using var h = await Harness.CreateAsync();
            var subId = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                estado: EstadoSuscripcion.Morosa,
                paymentRecoveryStatus: "GraceActive",
                lastPaymentFailedAtUtc: h.NowUtc.AddDays(-1));
            var incidentId = await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(4));

            var result = await h.Recovery.ResolveManuallyAsync(incidentId, "admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            var incident = await h.GetIncidentAsync(incidentId);
            Assert.Equal(PaymentIncidentStatus.Resolved, incident.Status);
            Assert.NotNull(incident.ResolvedAtUtc);
            var sub = await h.GetSubscriptionAsync(subId);
            Assert.Null(sub.PaymentRecoveryStatus);
            Assert.Null(sub.LastPaymentFailedAtUtc);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryManuallyResolved));
        }

        [Fact]
        public async Task ManualIgnore_RequiresReason_ThenClosesIncident()
        {
            using var h = await Harness.CreateAsync();
            var subId = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), estado: EstadoSuscripcion.Morosa);
            var incidentId = await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(4));

            var noReason = await h.Recovery.IgnoreAsync(incidentId, "admin", "admin@luxurycloud.cr", reason: null);
            Assert.False(noReason.Succeeded);
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryIgnored));

            var ok = await h.Recovery.IgnoreAsync(incidentId, "admin", "admin@luxurycloud.cr", reason: "pago regularizado por transferencia");
            Assert.True(ok.Succeeded);
            var incident = await h.GetIncidentAsync(incidentId);
            Assert.Equal(PaymentIncidentStatus.Ignored, incident.Status);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentRecoveryIgnored));
        }

        [Fact]
        public async Task ManualResolve_Morosa_ReturnsToActiva_AndClearsAllRecoveryFields()
        {
            using var h = await Harness.CreateAsync();
            var subId = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),                 // período aún vigente
                estado: EstadoSuscripcion.Morosa,
                paymentRecoveryStatus: "GraceExpired",
                lastPaymentFailedAtUtc: h.NowUtc.AddDays(-3),
                fechaFinGraciaUtc: h.NowUtc.AddDays(-1),           // gracia vieja
                lastPaymentRecoveryNotificationAtUtc: h.NowUtc.AddDays(-2));
            var incidentId = await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(-1), status: PaymentIncidentStatus.GraceExpired);

            var result = await h.Recovery.ResolveManuallyAsync(incidentId, "admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(subId);
            Assert.Equal(EstadoSuscripcion.Activa, sub.Estado);        // Morosa (recovery) → Activa
            Assert.Null(sub.PaymentRecoveryStatus);
            Assert.Null(sub.LastPaymentFailedAtUtc);
            Assert.Null(sub.FechaFinGraciaUtc);                        // fecha vieja limpiada (bug 3)
            Assert.Null(sub.LastPaymentRecoveryNotificationAtUtc);
            Assert.True(h.SubscriptionService.CanAccessApp(sub));
        }

        [Fact]
        public async Task ManualResolve_DoesNotReactivate_RealSuspended()
        {
            using var h = await Harness.CreateAsync();
            var subId = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                estado: EstadoSuscripcion.Suspendida,
                paymentRecoveryStatus: "Suspended");
            var incidentId = await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(-1), status: PaymentIncidentStatus.GraceExpired);

            var result = await h.Recovery.ResolveManuallyAsync(incidentId, "admin", "admin@luxurycloud.cr");

            Assert.True(result.Succeeded);
            var sub = await h.GetSubscriptionAsync(subId);
            Assert.Equal(EstadoSuscripcion.Suspendida, sub.Estado);    // NO se reactiva un suspendido real
            Assert.Equal("Suspended", sub.PaymentRecoveryStatus);      // se conserva el contexto real
        }

        [Fact]
        public async Task ManualResolve_WithAnotherLiveIncident_DoesNotClearRecoveryFields()
        {
            using var h = await Harness.CreateAsync();
            var subId = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(20),
                estado: EstadoSuscripcion.Morosa,
                paymentRecoveryStatus: "GraceActive",
                lastPaymentFailedAtUtc: h.NowUtc.AddDays(-1),
                fechaFinGraciaUtc: h.NowUtc.AddDays(3));
            var first = await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(-1), status: PaymentIncidentStatus.GraceExpired);
            await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(3), status: PaymentIncidentStatus.Open); // otro vivo

            await h.Recovery.ResolveManuallyAsync(first, "admin", "admin@luxurycloud.cr");

            var sub = await h.GetSubscriptionAsync(subId);
            Assert.Equal("GraceActive", sub.PaymentRecoveryStatus);   // NO se limpia: queda otro incidente vivo
            Assert.NotNull(sub.LastPaymentFailedAtUtc);
            Assert.NotNull(sub.FechaFinGraciaUtc);
            Assert.Equal(EstadoSuscripcion.Morosa, sub.Estado);       // tampoco se reactiva
        }

        [Fact]
        public async Task UpdateUrl_Failure_DoesNotModifyRecoveryState()
        {
            using var h = await Harness.CreateAsync();
            h.Admin.Url = null; // fuerza fallo de recurrentUrl
            var subId = await h.SeedSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(10),
                estado: EstadoSuscripcion.Morosa,
                paymentRecoveryStatus: "GraceActive",
                lastPaymentFailedAtUtc: h.NowUtc.AddDays(-1),
                fechaFinGraciaUtc: h.NowUtc.AddDays(3));
            var incidentId = await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(3));

            var result = await h.MethodUpdate.GenerateUpdateUrlAsync(h.TenantId, "buyer@example.com", "user-1", "user@test.cr");

            Assert.False(result.Succeeded);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.PaymentMethodUpdateUrlFailed));
            var sub = await h.GetSubscriptionAsync(subId);
            Assert.Equal("GraceActive", sub.PaymentRecoveryStatus);   // recovery intacto
            Assert.NotNull(sub.LastPaymentFailedAtUtc);
            var incident = await h.GetIncidentAsync(incidentId);
            Assert.Equal(PaymentIncidentStatus.Open, incident.Status);
        }

        // ── Worker: ejecuta el pase al arranque ──────────────────────────────────────

        [Fact]
        public async Task Worker_RunsGraceAndNotificationPasses_OnStartup()
        {
            var recovery = new RecordingRecoveryService();
            var notifications = new RecordingNotificationService();
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddScoped<IPaymentRecoveryService>(_ => recovery);
            services.AddScoped<IPaymentRecoveryNotificationService>(_ => notifications);
            await using var provider = services.BuildServiceProvider();

            var options = new StaticOptionsMonitor<BillingPaymentRecoveryOptions>(new BillingPaymentRecoveryOptions
            {
                Enabled = true,
                WorkerInitialDelayMinutes = 0,
                WorkerIntervalMinutes = 5
            });
            var worker = new LuxuryApp.Workers.PaymentRecoveryWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                options,
                new NoOpHeartbeat(),
                NullLogger<LuxuryApp.Workers.PaymentRecoveryWorker>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await worker.StartAsync(cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((recovery.GraceCalls == 0 || notifications.Calls == 0) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cts.Token);
            }

            await worker.StopAsync(CancellationToken.None);

            Assert.True(recovery.GraceCalls >= 1, "el worker debe ejecutar el pase de expiración al arranque");
            Assert.True(notifications.Calls >= 1, "el worker debe ejecutar el pase de notificaciones al arranque");
        }

        private sealed class NoOpHeartbeat : LuxuryApp.Services.Platform.IWorkerHeartbeatService
        {
            public Task TryBeatAsync(string workerName, string? cycleSummary = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<IReadOnlyList<LuxuryApp.Models.Platform.PlatformWorkerHeartbeat>> GetAllAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<LuxuryApp.Models.Platform.PlatformWorkerHeartbeat>>(Array.Empty<LuxuryApp.Models.Platform.PlatformWorkerHeartbeat>());
        }

        private sealed class RecordingNotificationService : IPaymentRecoveryNotificationService
        {
            public int Calls;
            public Task<int> RunPendingNotificationsAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(0);
            }
        }

        private sealed class RecordingRecoveryService : IPaymentRecoveryService
        {
            public int GraceCalls;
            public Task RegisterFailedPaymentAsync(Guid tenantId, int? failedRecurringPlanId, string? providerSubscriberId, string? resultCode, string? resultMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ResolveOnSuccessAsync(Guid tenantId, int? paidRecurringPlanId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RegisterFailedAddonPaymentAsync(Guid tenantId, int? failedRecurringPlanId, string? providerSubscriberId, string? resultCode, string? resultMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ResolveAddonOnSuccessAsync(Guid tenantId, int? paidRecurringPlanId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<int> RunGraceExpirationPassAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref GraceCalls);
                return Task.FromResult(0);
            }
            public Task<IReadOnlyList<PaymentRecoveryConsoleItem>> ListConsoleIncidentsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PaymentRecoveryConsoleItem>>(Array.Empty<PaymentRecoveryConsoleItem>());
            public Task<PaymentRecoveryActionResult> ResolveManuallyAsync(Guid incidentId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default) =>
                Task.FromResult(PaymentRecoveryActionResult.Ok("ok"));
            public Task<PaymentRecoveryActionResult> IgnoreAsync(Guid incidentId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default) =>
                Task.FromResult(PaymentRecoveryActionResult.Ok("ok"));
            public Task<IReadOnlyList<UnverifiedRecoveryResolutionItem>> ListUnverifiedResolutionsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<UnverifiedRecoveryResolutionItem>>(Array.Empty<UnverifiedRecoveryResolutionItem>());
            public Task<PaymentRecoveryActionResult> ReopenUnverifiedResolutionAsync(Guid incidentId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default) =>
                Task.FromResult(PaymentRecoveryActionResult.Ok("ok"));
        }

        [Fact]
        public async Task Console_ListsLiveIncidents_NotResolvedOnes()
        {
            using var h = await Harness.CreateAsync();
            var subId = await h.SeedSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(20), estado: EstadoSuscripcion.Morosa);
            var openId = await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(4), clienteEmail: "buyer@example.com");
            await h.SeedIncidentAsync(subId, graceEndsAtUtc: h.NowUtc.AddDays(-1), status: PaymentIncidentStatus.Resolved);

            var list = await h.Recovery.ListConsoleIncidentsAsync();

            var item = Assert.Single(list);
            Assert.Equal(openId, item.IncidentId);
            Assert.Equal("buyer@example.com", item.ClienteEmail);
            Assert.Equal("Tenant Recovery", item.TenantName);
            Assert.False(string.IsNullOrEmpty(item.ProviderSubscriberSuffix));
            Assert.DoesNotContain("900117", item.ProviderSubscriberSuffix!); // enmascarado
        }

        // ── Fake del API admin de TiloPay para recurrentUrl ──────────────────────────

        private sealed class RecoveryFakeAdmin : ITilopayRepeatAdminService
        {
            public bool IsEnabled { get; set; } = true;
            public string? Url { get; set; } = "https://app.tilopay.com/recurrent/abc123";
            public bool Succeeds { get; set; } = true;

            /// <summary>Contrato que "funcionó": "id_plan" (primario) o "id_plan+aliases" (fallback).</summary>
            public string Contract { get; set; } = "id_plan";

            public Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(Succeeds && Url is not null
                    ? TilopayAdminOperationResult.Ok("ok", Url) with
                    {
                        Contract = Contract,
                        RecurrentDiagnostics = new RecurrentUrlDiagnostics
                        {
                            Contract = Contract,
                            HttpStatus = 200,
                            HasUrlRenew = true,
                            SelectedField = "url_renew",
                            UrlHostPathMasked = "app.tilopay.com/recurrent/*** (2 segs)"
                        }
                    }
                    : TilopayAdminOperationResult.Fail("no disponible"));

            public Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(int tilopayPlanId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<TilopaySubscriber>>(new List<TilopaySubscriber>());
            public Task<SubscriberResolutionResult> ResolveSubscriberAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(SubscriberResolutionResult.NotFound());
            public Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(TargetSubscriberAssessment.FromMatches(new List<TilopaySubscriber>(), tilopayPlanId));
            public Task<TilopayAdminOperationResult> PauseSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("ok"));
            public Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("ok"));
            public Task<TilopayAdminOperationResult> DeleteSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("ok"));
            public Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(string subscriberId, TilopaySubscriberStatus status, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("ok"));
        }

        /// <summary>Fake del envío de correo de recuperación: cuenta llamadas por etapa y puede fallar a voluntad.</summary>
        private sealed class FakeRecoveryEmailSender : IPaymentRecoveryEmailSender
        {
            public int PaymentFailedCalls;
            public int GraceReminderCalls;
            public int SuspendedCalls;
            public bool ShouldSucceed { get; set; } = true;
            public List<string> Recipients { get; } = new();

            public int TotalCalls => PaymentFailedCalls + GraceReminderCalls + SuspendedCalls;

            public Task<PaymentRecoveryEmailResult> SendAsync(
                PaymentRecoveryEmailKind kind, PaymentRecoveryEmailContext context, CancellationToken cancellationToken = default)
            {
                switch (kind)
                {
                    case PaymentRecoveryEmailKind.PaymentFailed: PaymentFailedCalls++; break;
                    case PaymentRecoveryEmailKind.GraceReminder: GraceReminderCalls++; break;
                    case PaymentRecoveryEmailKind.Suspended: SuspendedCalls++; break;
                }
                Recipients.Add(context.ToEmail);
                return Task.FromResult(ShouldSucceed
                    ? PaymentRecoveryEmailResult.Ok()
                    : PaymentRecoveryEmailResult.Fail("boom"));
            }
        }

        private sealed class Harness : IDisposable
        {
            private readonly IDisposable _connection;

            public ApplicationDbContext Db { get; private init; } = null!;
            public RecoveryFakeAdmin Admin { get; } = new();
            public FakeRecoveryEmailSender Email { get; } = new();
            public PaymentRecoveryService Recovery { get; private set; } = null!;
            public PaymentRecoveryNotificationService Notifications { get; private set; } = null!;
            public PaymentMethodUpdateService MethodUpdate { get; private set; } = null!;
            public SuscripcionService SubscriptionService { get; private set; } = null!;
            public BillingHealthService Health { get; private set; } = null!;
            public Guid TenantId { get; } = Guid.NewGuid();
            public Guid PlanId { get; private set; }
            public DateTime NowUtc { get; } = DateTime.UtcNow;

            private Harness(IDisposable connection) => _connection = connection;

            public static async Task<Harness> CreateAsync(bool autoSuspend = false, bool sendEmails = true)
            {
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
                var h = new Harness(connection) { Db = context };

                var tenantAccessor = new TenantExecutionContextAccessor();
                var cache = new MemoryCache(new MemoryCacheOptions());
                var accessCache = new TenantCommercialAccessCache(cache);
                var clock = new FixedBusinessDateTimeProvider(DateTime.SpecifyKind(h.NowUtc, DateTimeKind.Unspecified));
                var recoveryOptions = Options.Create(new BillingPaymentRecoveryOptions
                {
                    Enabled = true,
                    AutoSuspendAfterGrace = autoSuspend,
                    GraceDays = 5,
                    SendEmailNotifications = sendEmails,
                    ReminderBeforeGraceEndsHours = 24
                });

                h.SubscriptionService = new SuscripcionService(
                    context, cache, accessCache, clock,
                    Options.Create(CalculatorCatalog.BuildRepeatOptions()),
                    NullLogger<SuscripcionService>.Instance,
                    recoveryOptions);

                h.Recovery = new PaymentRecoveryService(
                    context, tenantAccessor, clock, recoveryOptions,
                    NullLogger<PaymentRecoveryService>.Instance,
                    new FakeTenantOwnerResolver(),
                    accessCache);

                h.Notifications = new PaymentRecoveryNotificationService(
                    context, tenantAccessor, clock, recoveryOptions, h.Email,
                    Options.Create(new PublicSiteOptions { PublicBaseUrl = "https://app.luxurycloud.app" }),
                    NullLogger<PaymentRecoveryNotificationService>.Instance,
                    new FakeTenantOwnerResolver());

                h.MethodUpdate = new PaymentMethodUpdateService(
                    context, h.Admin, clock, NullLogger<PaymentMethodUpdateService>.Instance,
                    new FakeTenantOwnerResolver());

                h.Health = new BillingHealthService(context, h.SubscriptionService);

                context.Tenants.Add(new Tenant { Id = h.TenantId, Nombre = "Tenant Recovery", Activo = true });
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
                EstadoSuscripcion estado = EstadoSuscripcion.Activa,
                bool cancelAtPeriodEnd = false,
                DateTime? cancellationEffectiveAtUtc = null,
                string? providerStatusRaw = null,
                string? paymentRecoveryStatus = null,
                DateTime? lastPaymentFailedAtUtc = null,
                DateTime? fechaFinGraciaUtc = null,
                DateTime? lastPaymentRecoveryNotificationAtUtc = null,
                Guid? tenantId = null)
            {
                var owner = tenantId ?? TenantId;
                if (owner != TenantId && !await Db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == owner))
                {
                    Db.Tenants.Add(new Tenant { Id = owner, Nombre = "Tenant Otro", Activo = true });
                }

                var id = Guid.NewGuid();
                Db.Suscripciones.Add(new Suscripcion
                {
                    Id = id,
                    TenantId = owner,
                    PlanId = PlanId,
                    CodigoPlan = "LC_M_02",
                    Estado = estado,
                    Proveedor = PaymentProviderType.Tilopay,
                    TilopayRecurringPlanId = RecurringPlanId,
                    ProviderSubscriptionId = "900117",
                    ProviderStatusRaw = providerStatusRaw,
                    PaymentRecoveryStatus = paymentRecoveryStatus,
                    LastPaymentFailedAtUtc = lastPaymentFailedAtUtc,
                    LastPaymentRecoveryNotificationAtUtc = lastPaymentRecoveryNotificationAtUtc,
                    FechaInicio = localEndUtc.AddMonths(-1),
                    FechaFin = localEndUtc,
                    FechaProximoCobroUtc = localEndUtc,
                    FechaFinGraciaUtc = fechaFinGraciaUtc,
                    CancelAtPeriodEnd = cancelAtPeriodEnd,
                    CancellationEffectiveAtUtc = cancellationEffectiveAtUtc
                });
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
                return id;
            }

            public async Task<Guid> SeedIncidentAsync(
                Guid subscriptionId,
                DateTime graceEndsAtUtc,
                PaymentIncidentStatus status = PaymentIncidentStatus.Open,
                string? clienteEmail = null,
                DateTime? lastNotificationAtUtc = null,
                DateTime? lastReminderAtUtc = null)
            {
                var id = Guid.NewGuid();
                Db.SubscriptionPaymentIncidents.Add(new SubscriptionPaymentIncident
                {
                    Id = id,
                    TenantId = TenantId,
                    SuscripcionId = subscriptionId,
                    PlanCode = "LC_M_02",
                    TilopayRecurringPlanId = RecurringPlanId,
                    ProviderSubscriptionId = "900117",
                    ClienteEmail = clienteEmail,
                    Status = status,
                    FailureDetectedAtUtc = graceEndsAtUtc.AddDays(-5),
                    GraceEndsAtUtc = graceEndsAtUtc,
                    LastNotificationAtUtc = lastNotificationAtUtc,
                    LastReminderAtUtc = lastReminderAtUtc,
                    NotificationCount = (lastNotificationAtUtc is null ? 0 : 1) + (lastReminderAtUtc is null ? 0 : 1),
                    FailureCount = 1,
                    CreatedAtUtc = graceEndsAtUtc.AddDays(-5),
                    UpdatedAtUtc = graceEndsAtUtc.AddDays(-5)
                });
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
                return id;
            }

            public Task<SubscriptionPaymentIncident> GetIncidentAsync(Guid id)
            {
                Db.ChangeTracker.Clear();
                return Db.SubscriptionPaymentIncidents.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == id);
            }

            public async Task SeedPaymentAsync(EstadoPagoProveedor estado, DateTime confirmedAtUtc)
            {
                Db.PagosSuscripcion.Add(new PagoSuscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = TenantId,
                    PlanId = PlanId,
                    Proveedor = PaymentProviderType.Tilopay,
                    Estado = estado,
                    TilopayRecurringPlanId = RecurringPlanId,
                    Monto = 15000m,
                    Moneda = "CRC",
                    ReferenciaInterna = $"ref-{Guid.NewGuid():N}",
                    FechaCreacionUtc = confirmedAtUtc.AddMinutes(-5),
                    FechaConfirmacionUtc = confirmedAtUtc,
                    FechaActualizacionUtc = confirmedAtUtc
                });
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public async Task<Suscripcion> GetSubscriptionAsync(Guid id)
            {
                Db.ChangeTracker.Clear();
                return await Db.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == id);
            }

            public Task<SubscriptionPaymentIncident?> GetOpenIncidentAsync() =>
                Db.SubscriptionPaymentIncidents.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.TenantId == TenantId && i.Status == PaymentIncidentStatus.Open)
                    .OrderByDescending(i => i.CreatedAtUtc)
                    .FirstOrDefaultAsync();

            public Task<int> CountOpenIncidentsAsync() =>
                Db.SubscriptionPaymentIncidents.IgnoreQueryFilters()
                    .CountAsync(i => i.TenantId == TenantId && i.Status == PaymentIncidentStatus.Open);

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
