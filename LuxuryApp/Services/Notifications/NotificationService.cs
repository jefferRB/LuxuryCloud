using System.Globalization;
using System.Text.Json;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Notifications;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Notifications
{
    public sealed class NotificationService : INotificationService
    {
        private const int MaxLimit = 30;

        // Cultura es-CR para fechas/horas; cae a la cultura invariante si no existe en el SO.
        private static readonly CultureInfo SpanishCulture = ResolveSpanishCulture();

        private static readonly JsonSerializerOptions MetadataJsonOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ILogger<NotificationService> logger)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _logger = logger;
        }

        public async Task<NotificationSummary> GetSummaryAsync(int limit = 15, CancellationToken cancellationToken = default)
        {
            var take = Math.Clamp(limit <= 0 ? 15 : limit, 1, MaxLimit);

            var unreadCount = await _context.TenantNotifications
                .AsNoTracking()
                .CountAsync(n => !n.IsRead, cancellationToken);

            var notifications = await _context.TenantNotifications
                .AsNoTracking()
                .OrderByDescending(n => n.CreatedAtUtc)
                .ThenByDescending(n => n.Id)
                .Take(take)
                .Select(n => new
                {
                    n.Id,
                    n.Type,
                    n.Title,
                    n.Message,
                    n.ActionUrl,
                    n.IsRead,
                    n.CreatedAtUtc
                })
                .ToListAsync(cancellationToken);

            var offset = _businessDateTimeProvider.NowOffset().Offset;
            var nowUtc = DateTime.UtcNow;

            var items = notifications
                .Select(n => new NotificationItem
                {
                    Id = n.Id,
                    Type = n.Type,
                    Icon = ResolveIcon(n.Type),
                    Title = n.Title,
                    Message = n.Message,
                    ActionUrl = n.ActionUrl,
                    IsRead = n.IsRead,
                    CreatedAtLabel = BuildRelativeLabel(n.CreatedAtUtc, nowUtc, offset)
                })
                .ToList();

            return new NotificationSummary
            {
                UnreadCount = unreadCount,
                Notifications = items
            };
        }

        public async Task<int> MarkAllAsReadAsync(CancellationToken cancellationToken = default)
        {
            var pendientes = await _context.TenantNotifications
                .Where(n => !n.IsRead)
                .ToListAsync(cancellationToken);

            if (pendientes.Count == 0)
            {
                return 0;
            }

            var nowUtc = DateTime.UtcNow;
            foreach (var notificacion in pendientes)
            {
                notificacion.IsRead = true;
                notificacion.ReadAtUtc = nowUtc;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return pendientes.Count;
        }

        public async Task<bool> MarkAsReadAsync(int id, CancellationToken cancellationToken = default)
        {
            if (id <= 0)
            {
                return false;
            }

            var notificacion = await _context.TenantNotifications
                .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

            if (notificacion is null || notificacion.IsRead)
            {
                return false;
            }

            notificacion.IsRead = true;
            notificacion.ReadAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task CreateBookingRequestReceivedAsync(
            BookingRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request is null || request.Id <= 0)
            {
                return;
            }

            var servicioNombre = await _context.Servicios
                .AsNoTracking()
                .Where(s => s.Id == request.ServicioId)
                .Select(s => s.Nombre)
                .FirstOrDefaultAsync(cancellationToken) ?? "Servicio";

            string funcionarioNombre = "Cualquier funcionario";
            if (request.FuncionarioId.HasValue)
            {
                funcionarioNombre = await _context.Funcionarios
                    .AsNoTracking()
                    .Where(f => f.IdFuncionario == request.FuncionarioId.Value)
                    .Select(f => f.Nombre)
                    .FirstOrDefaultAsync(cancellationToken) ?? "Funcionario";
            }

            var fechaHora = FormatFechaHora(request.FechaHoraInicioSolicitada);
            var message =
                $"{request.NombreCliente} solicitó \"{servicioNombre}\" para el {fechaHora}.";

            var metadata = BuildMetadataJson(new Dictionary<string, object?>
            {
                ["telefono"] = request.TelefonoCliente,
                ["funcionario"] = funcionarioNombre,
                ["bookingRequestId"] = request.Id
            });

            await CreateIfNotExistsAsync(
                type: NotificationTypes.BookingRequestReceived,
                title: "Nueva solicitud de reserva",
                message: message,
                actionUrl: "/Reservas",
                entityType: NotificationEntityTypes.BookingRequest,
                entityId: request.Id,
                metadataJson: metadata,
                source: NotificationSources.PublicBooking,
                cancellationToken: cancellationToken);
        }

        public async Task CreateAppointmentCancelledViaWhatsAppAsync(
            Cita cita,
            CancellationToken cancellationToken = default)
        {
            if (cita is null || cita.Id <= 0)
            {
                return;
            }

            var servicioNombre = await ResolveServicioNombreAsync(cita, cancellationToken);
            var funcionarioNombre = await _context.Funcionarios
                .AsNoTracking()
                .Where(f => f.IdFuncionario == cita.FuncionarioId)
                .Select(f => f.Nombre)
                .FirstOrDefaultAsync(cancellationToken) ?? "Funcionario";

            var clienteNombre = await ResolveClienteNombreAsync(cita, cancellationToken);
            var fechaHora = FormatFechaHora(cita.FechaHoraCita);

            var message =
                $"{clienteNombre} canceló su cita de \"{servicioNombre}\" con {funcionarioNombre} el {fechaHora}.";

            var metadata = BuildMetadataJson(new Dictionary<string, object?>
            {
                ["citaId"] = cita.Id,
                ["telefono"] = cita.TelefonoCliente,
                ["clienteId"] = cita.ClienteId,
                ["funcionario"] = funcionarioNombre
            });

            await CreateIfNotExistsAsync(
                type: NotificationTypes.AppointmentCancelledViaWhatsApp,
                title: "Cita cancelada por WhatsApp",
                message: message,
                actionUrl: "/Calendar",
                entityType: NotificationEntityTypes.Cita,
                entityId: cita.Id,
                metadataJson: metadata,
                source: NotificationSources.WhatsApp,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Inserta la notificación solo si no existe ya una con la misma llave lógica
        /// (Type + EntityType + EntityId) dentro del tenant actual. Evita duplicados ante
        /// reintentos del webhook o doble submit, sin depender de excepciones de BD.
        /// </summary>
        private async Task CreateIfNotExistsAsync(
            string type,
            string title,
            string message,
            string? actionUrl,
            string entityType,
            int entityId,
            string? metadataJson,
            string source,
            CancellationToken cancellationToken)
        {
            var yaExiste = await _context.TenantNotifications
                .AsNoTracking()
                .AnyAsync(
                    n => n.Type == type && n.EntityType == entityType && n.EntityId == entityId,
                    cancellationToken);

            if (yaExiste)
            {
                return;
            }

            _context.TenantNotifications.Add(new TenantNotification
            {
                Type = type,
                Title = title,
                Message = message,
                ActionUrl = actionUrl,
                EntityType = entityType,
                EntityId = entityId,
                MetadataJson = metadataJson,
                IsRead = false,
                CreatedAtUtc = DateTime.UtcNow,
                Source = source
            });

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // No debe romper el flujo de negocio (crear reserva / cancelar cita) si la
                // notificación falla. Se registra y se continúa.
                _logger.LogWarning(
                    ex,
                    "No se pudo crear la notificación {Type} para {EntityType} {EntityId}.",
                    type,
                    entityType,
                    entityId);
            }
        }

        private async Task<string> ResolveServicioNombreAsync(Cita cita, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(cita.ServicioNombrePersonalizado))
            {
                return cita.ServicioNombrePersonalizado!;
            }

            if (cita.ServicioId.HasValue)
            {
                var nombre = await _context.Servicios
                    .AsNoTracking()
                    .Where(s => s.Id == cita.ServicioId.Value)
                    .Select(s => s.Nombre)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(nombre))
                {
                    return nombre!;
                }
            }

            return "Servicio";
        }

        private async Task<string> ResolveClienteNombreAsync(Cita cita, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(cita.NombreCliente))
            {
                return cita.NombreCliente!;
            }

            if (cita.ClienteId.HasValue)
            {
                var nombre = await _context.Clientes
                    .AsNoTracking()
                    .Where(c => c.Id == cita.ClienteId.Value)
                    .Select(c => c.Nombre)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(nombre))
                {
                    return nombre!;
                }
            }

            return "Un cliente";
        }

        private static string? BuildMetadataJson(IDictionary<string, object?> values)
        {
            var limpio = values
                .Where(kvp => kvp.Value is not null && !(kvp.Value is string s && string.IsNullOrWhiteSpace(s)))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return limpio.Count == 0 ? null : JsonSerializer.Serialize(limpio, MetadataJsonOptions);
        }

        private static string ResolveIcon(string type) => type switch
        {
            NotificationTypes.BookingRequestReceived => "calendar-plus",
            NotificationTypes.AppointmentCancelledViaWhatsApp => "calendar-x",
            _ => "bell"
        };

        private static string FormatFechaHora(DateTime fechaHoraLocal) =>
            fechaHoraLocal.ToString("dd MMM, HH:mm", SpanishCulture);

        private static string BuildRelativeLabel(DateTime createdAtUtc, DateTime nowUtc, TimeSpan offset)
        {
            var elapsed = nowUtc - createdAtUtc;

            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            if (elapsed.TotalMinutes < 1)
            {
                return "Hace un momento";
            }

            if (elapsed.TotalMinutes < 60)
            {
                var minutos = (int)Math.Floor(elapsed.TotalMinutes);
                return $"Hace {minutos} min";
            }

            // Costa Rica no usa horario de verano: el offset es constante, así que aplicarlo
            // directamente convierte UTC a hora local del negocio de forma exacta.
            var createdLocal = createdAtUtc + offset;
            var nowLocal = nowUtc + offset;

            if (elapsed.TotalHours < 24 && createdLocal.Date == nowLocal.Date)
            {
                return $"Hoy {createdLocal.ToString("HH:mm", SpanishCulture)}";
            }

            if (createdLocal.Date == nowLocal.Date.AddDays(-1))
            {
                return $"Ayer {createdLocal.ToString("HH:mm", SpanishCulture)}";
            }

            return createdLocal.ToString("dd MMM, HH:mm", SpanishCulture);
        }

        private static CultureInfo ResolveSpanishCulture()
        {
            try
            {
                return CultureInfo.GetCultureInfo("es-CR");
            }
            catch (CultureNotFoundException)
            {
                try
                {
                    return CultureInfo.GetCultureInfo("es");
                }
                catch (CultureNotFoundException)
                {
                    return CultureInfo.InvariantCulture;
                }
            }
        }
    }
}
