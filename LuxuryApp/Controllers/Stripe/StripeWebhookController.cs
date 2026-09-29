using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.SaaS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;
using Stripe;

namespace LuxuryApp.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("Webhook")]
    [Route("api/webhooks/stripe")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly ILogger<StripeWebhookController> _logger;
        private readonly IConfiguration _config;
        private readonly IServiceProvider _serviceProvider;
        private readonly SuscripcionService _suscripcionService;

        public StripeWebhookController(
            ILogger<StripeWebhookController> logger,
            IConfiguration config,
            IServiceProvider serviceProvider,
            SuscripcionService suscripcionService)
        {
            _logger = logger;
            _config = config;
            _serviceProvider = serviceProvider;
            _suscripcionService = suscripcionService;
        }

        [HttpPost]
        public async Task<IActionResult> Handle()
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

            Event stripeEvent;

            try
            {
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    _config["Stripe:WebhookSecret"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Firma Stripe invalida.");
                return BadRequest();
            }

            using var scopeLog = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["Provider"] = PaymentProviderType.Stripe,
                ["EventId"] = stripeEvent.Id,
                ["Type"] = stripeEvent.Type
            });

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var evento = await GetOrCreateEventAsync(db, stripeEvent, json);
                if (evento is null)
                {
                    _logger.LogWarning("Evento Stripe duplicado ignorado.");
                    return Ok();
                }

                evento.Tipo = stripeEvent.Type;
                evento.Payload = json;
                evento.Procesado = false;
                evento.EstadoProcesamiento = "Recibido";
                evento.FechaRecepcionUtc = DateTime.UtcNow;
                evento.FechaProcesamientoUtc = null;
                evento.Error = null;

                await db.SaveChangesAsync();

                switch (stripeEvent.Type)
                {
                    case "checkout.session.completed":
                        await HandleCheckoutCompleted(stripeEvent);
                        break;

                    case "invoice.payment_succeeded":
                        await HandlePaymentSucceeded(stripeEvent);
                        break;

                    case "invoice.payment_failed":
                        await HandlePaymentFailed(stripeEvent);
                        break;

                    case "customer.subscription.updated":
                        await HandleSubscriptionUpdated(stripeEvent);
                        break;

                    case "customer.subscription.deleted":
                        await HandleSubscriptionDeleted(stripeEvent);
                        break;

                    default:
                        _logger.LogWarning("Evento Stripe no manejado.");
                        break;
                }

                evento.Procesado = true;
                evento.EstadoProcesamiento = "Procesado";
                evento.FechaProcesamientoUtc = DateTime.UtcNow;
                evento.Error = null;
                await db.SaveChangesAsync();

                return Ok();
            }
            catch (Exception ex)
            {
                await PersistStripeErrorAsync(stripeEvent.Id, ex.Message);
                _logger.LogError(ex, "Error procesando webhook Stripe.");
                return StatusCode(500);
            }
        }

        private async Task<EventoPago?> GetOrCreateEventAsync(
            ApplicationDbContext db,
            Event stripeEvent,
            string payload)
        {
            var evento = await db.EventosPago
                .FirstOrDefaultAsync(e =>
                    e.Proveedor == PaymentProviderType.Stripe &&
                    e.ProveedorEventId == stripeEvent.Id);

            if (evento is not null)
            {
                return evento.Procesado ? null : evento;
            }

            evento = new EventoPago
            {
                Id = Guid.NewGuid(),
                Proveedor = PaymentProviderType.Stripe,
                ProveedorEventId = stripeEvent.Id,
                Tipo = stripeEvent.Type,
                Payload = payload,
                Procesado = false,
                EstadoProcesamiento = "Recibido",
                FechaRecepcionUtc = DateTime.UtcNow
            };

            db.EventosPago.Add(evento);

            try
            {
                await db.SaveChangesAsync();
                return evento;
            }
            catch (DbUpdateException)
            {
                db.Entry(evento).State = EntityState.Detached;

                var existingEvent = await db.EventosPago
                    .FirstOrDefaultAsync(e =>
                        e.Proveedor == PaymentProviderType.Stripe &&
                        e.ProveedorEventId == stripeEvent.Id);

                if (existingEvent is null)
                {
                    throw;
                }

                return existingEvent.Procesado ? null : existingEvent;
            }
        }

        private async Task PersistStripeErrorAsync(string eventId, string errorMessage)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var existingEvent = await db.EventosPago
                    .FirstOrDefaultAsync(e =>
                        e.Proveedor == PaymentProviderType.Stripe &&
                        e.ProveedorEventId == eventId);

                if (existingEvent is null)
                {
                    return;
                }

                existingEvent.Procesado = false;
                existingEvent.EstadoProcesamiento = "Error";
                existingEvent.Error = Trim(errorMessage);
                existingEvent.FechaProcesamientoUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx, "No fue posible persistir el error del evento Stripe.");
            }
        }

        private async Task HandleCheckoutCompleted(Event stripeEvent)
        {
            var session = stripeEvent.Data.Object as Stripe.Checkout.Session;
            if (session?.Metadata is null ||
                !session.Metadata.ContainsKey("TenantId") ||
                !session.Metadata.ContainsKey("PlanId"))
            {
                return;
            }

            if (!Guid.TryParse(session.Metadata["TenantId"], out var tenantId) ||
                !Guid.TryParse(session.Metadata["PlanId"], out var planId))
            {
                return;
            }

            await _suscripcionService.ActivarSuscripcionAsync(
                tenantId,
                planId,
                session.SubscriptionId ?? string.Empty,
                session.CustomerId ?? string.Empty);
        }

        private async Task HandlePaymentSucceeded(Event stripeEvent)
        {
            var invoice = stripeEvent.Data.Object as Invoice;
            var subscriptionId = invoice?.Lines?.Data?.FirstOrDefault()?.Subscription?.Id;

            if (string.IsNullOrWhiteSpace(subscriptionId) || invoice is null)
            {
                return;
            }

            await _suscripcionService.RegistrarPagoAsync(
                subscriptionId,
                invoice.Id,
                invoice.AmountPaid / 100m,
                invoice.Currency);
        }

        private async Task HandlePaymentFailed(Event stripeEvent)
        {
            var invoice = stripeEvent.Data.Object as Invoice;
            var subscriptionId = invoice?.Lines?.Data?.FirstOrDefault()?.Subscription?.Id;

            if (string.IsNullOrWhiteSpace(subscriptionId) || invoice is null)
            {
                return;
            }

            await _suscripcionService.MarcarPagoFallidoAsync(
                subscriptionId,
                invoice.Id,
                invoice.AmountDue / 100m,
                invoice.Currency);
        }

        private async Task HandleSubscriptionUpdated(Event stripeEvent)
        {
            var sub = stripeEvent.Data.Object as Subscription;
            if (sub is null)
            {
                return;
            }

            var nuevoEstado = sub.Status switch
            {
                "trialing" => EstadoSuscripcion.Trial,
                "active" => EstadoSuscripcion.Activa,
                "past_due" => EstadoSuscripcion.Morosa,
                "unpaid" => EstadoSuscripcion.Morosa,
                "canceled" => EstadoSuscripcion.Cancelada,
                _ => EstadoSuscripcion.Activa
            };

            await _suscripcionService.ActualizarEstadoDesdeStripeAsync(
                sub.Id,
                nuevoEstado,
                sub.CancelAtPeriodEnd);
        }

        private async Task HandleSubscriptionDeleted(Event stripeEvent)
        {
            var sub = stripeEvent.Data.Object as Subscription;
            if (sub is null)
            {
                return;
            }

            await _suscripcionService.CancelarSuscripcionAsync(sub.Id, false);
        }

        private static string Trim(string value) =>
            value.Length <= 500 ? value : value[..500];
    }
}
