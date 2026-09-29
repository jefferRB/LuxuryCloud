using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class CalendarCommandServiceTests
    {
        [Fact]
        public async Task CreateAsync_ShouldPersistNormalizedAppointment()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var response = await service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "  Ana   Maria  ",
                TelefonoCliente = " 8888-9999 ",
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 10, 15, 42),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "cita"
            });

            context.ChangeTracker.Clear();

            var cita = await context.Citas.AsNoTracking().SingleAsync();

            Assert.Equal("Ana Maria", cita.NombreCliente);
            Assert.Equal("8888-9999", cita.TelefonoCliente);
            Assert.Equal(new DateTime(2026, 4, 24, 10, 15, 0), cita.FechaHoraCita);
            Assert.Equal("CITA", cita.Tipo);
            Assert.Equal(funcionario.IdFuncionario, cita.FuncionarioId);
            Assert.Equal(servicio.Id, cita.ServicioId);
            Assert.Equal(cita.Id, response.Id);
            Assert.Equal(60, response.DuracionMinutos);
        }

        [Fact]
        public async Task CreateAsync_ShouldUseSelectedClienteSnapshot_AndIgnoreManualConsent()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 45);
            var cliente = await SeedClienteAsync(context, "Cliente Registrado", "72223333", aceptaMensajesWhatsApp: false);
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Nombre Manual",
                TelefonoCliente = "79990000",
                ClienteId = cliente.Id,
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 11, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                WhatsAppConsentAtCreation = true,
                WhatsAppConsentSource = "CitaManual",
                WhatsAppConsentCapturedAtUtc = new DateTime(2026, 4, 20, 8, 0, 0, DateTimeKind.Utc)
            });

            var cita = await context.Citas.AsNoTracking().SingleAsync();

            Assert.Equal(cliente.Id, cita.ClienteId);
            Assert.Equal("Cliente Registrado", cita.NombreCliente);
            Assert.Equal("72223333", cita.TelefonoCliente);
            Assert.False(cita.WhatsAppConsentAtCreation);
            Assert.Equal("ClienteRegistrado", cita.WhatsAppConsentSource);
            Assert.NotNull(cita.WhatsAppConsentCapturedAtUtc);
        }

        [Fact]
        public async Task CreateAsync_ShouldPersistManualConsent_WhenClienteIdIsMissing()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Masaje", 50);
            var service = ControllerTestSupport.CreateCalendarCommandService(context);
            var capturedAt = new DateTime(2026, 4, 21, 9, 30, 0, DateTimeKind.Utc);

            await service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Manual Consent",
                TelefonoCliente = "73334444",
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 12, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                WhatsAppConsentAtCreation = true,
                WhatsAppConsentSource = "CitaManual",
                WhatsAppConsentCapturedAtUtc = capturedAt
            });

            var cita = await context.Citas.AsNoTracking().SingleAsync();

            Assert.Null(cita.ClienteId);
            Assert.True(cita.WhatsAppConsentAtCreation);
            Assert.Equal("CitaManual", cita.WhatsAppConsentSource);
            Assert.Equal(capturedAt, cita.WhatsAppConsentCapturedAtUtc);
        }

        [Fact]
        public async Task CreateAsync_ShouldRejectClienteIdFromAnotherTenant()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteExterno = await SeedClienteAsync(context, "Cliente Externo", "74445555", aceptaMensajesWhatsApp: true);

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 30);
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var exception = await Assert.ThrowsAsync<CalendarValidationException>(() => service.CreateAsync(new CalendarUpsertRequest
            {
                ClienteId = clienteExterno.Id,
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 13, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            }));

            Assert.Contains("cliente", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await context.Citas.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task CreateAsync_ShouldPersistValidDescanso()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Luis");
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await service.CreateAsync(new CalendarUpsertRequest
            {
                FechaHoraCita = new DateTime(2026, 4, 24, 12, 0, 33),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "DESCANSO",
                DuracionMinutos = 45
            });

            var cita = await context.Citas.AsNoTracking().SingleAsync();

            Assert.Equal("DESCANSO", cita.Tipo);
            Assert.Equal(45, cita.DuracionMinutos);
            Assert.Null(cita.ServicioId);
            Assert.Equal("DESCANSO", cita.NombreCliente);
            Assert.Equal(new DateTime(2026, 4, 24, 12, 0, 0), cita.FechaHoraCita);
        }

        [Fact]
        public async Task CreateAsync_ShouldReject_WhenCitaHasNoServicio()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Paola");
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var exception = await Assert.ThrowsAsync<CalendarValidationException>(() => service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Cliente sin servicio",
                FechaHoraCita = new DateTime(2026, 4, 24, 13, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            }));

            Assert.Contains("servicio", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await context.Citas.ToListAsync());
        }

        [Fact]
        public async Task CreateAsync_ShouldReject_WhenFuncionarioIsInactive()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Inactivo", activo: false);
            var servicio = await SeedServicioAsync(context, "Lavado", 30);
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var exception = await Assert.ThrowsAsync<CalendarValidationException>(() => service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Cliente",
                FechaHoraCita = new DateTime(2026, 4, 24, 14, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Tipo = "CITA"
            }));

            Assert.Contains("inactivo", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await context.Citas.ToListAsync());
        }

        [Fact]
        public async Task CreateAsync_ShouldRejectOverlap()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Mario");
            var servicio = await SeedServicioAsync(context, "Color", 60);
            await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                new DateTime(2026, 4, 24, 10, 0, 0),
                servicioId: servicio.Id);

            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var exception = await Assert.ThrowsAsync<CalendarValidationException>(() => service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Traslape",
                FechaHoraCita = new DateTime(2026, 4, 24, 10, 30, 0),
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Tipo = "CITA"
            }));

            Assert.Contains("horario", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await context.Citas.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task UpdateAsync_ShouldRejectOverlap()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Carla");
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                new DateTime(2026, 4, 24, 9, 0, 0),
                servicioId: servicio.Id);
            var citaEditable = await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                new DateTime(2026, 4, 24, 11, 0, 0),
                servicioId: servicio.Id);

            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var exception = await Assert.ThrowsAsync<CalendarValidationException>(() => service.UpdateAsync(citaEditable.Id, new CalendarUpsertRequest
            {
                NombreCliente = "Cliente editado",
                FechaHoraCita = new DateTime(2026, 4, 24, 9, 30, 0),
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Tipo = "CITA"
            }));

            Assert.Contains("horario", exception.Message, StringComparison.OrdinalIgnoreCase);

            context.ChangeTracker.Clear();
            var persisted = await context.Citas.AsNoTracking().SingleAsync(c => c.Id == citaEditable.Id);
            Assert.Equal(new DateTime(2026, 4, 24, 11, 0, 0), persisted.FechaHoraCita);
        }

        [Fact]
        public async Task MoveAsync_ShouldRejectOverlap()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Jose");
            var servicio = await SeedServicioAsync(context, "Peinado", 45);
            await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                new DateTime(2026, 4, 24, 15, 0, 0),
                servicioId: servicio.Id);
            var citaMovible = await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                new DateTime(2026, 4, 24, 17, 0, 0),
                servicioId: servicio.Id);

            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var exception = await Assert.ThrowsAsync<CalendarValidationException>(() => service.MoveAsync(citaMovible.Id, new CalendarMoveRequest
            {
                FechaHoraCita = new DateTime(2026, 4, 24, 15, 15, 0),
                FuncionarioId = funcionario.IdFuncionario
            }));

            Assert.Contains("horario", exception.Message, StringComparison.OrdinalIgnoreCase);

            context.ChangeTracker.Clear();
            var persisted = await context.Citas.AsNoTracking().SingleAsync(c => c.Id == citaMovible.Id);
            Assert.Equal(new DateTime(2026, 4, 24, 17, 0, 0), persisted.FechaHoraCita);
        }

        [Fact]
        public async Task CreateAsync_ShouldDuplicateAppointments_WhenDatesAreAvailable()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Diana");
            var servicio = await SeedServicioAsync(context, "Spa", 50);
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Cliente duplicado",
                TelefonoCliente = "70000000",
                FechaHoraCita = new DateTime(2026, 4, 24, 9, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Tipo = "CITA",
                Duplicar = true,
                FechasDuplicadas = ["2026-04-25", "2026-04-26"]
            });

            var citas = await context.Citas
                .AsNoTracking()
                .OrderBy(c => c.FechaHoraCita)
                .ToListAsync();

            Assert.Equal(3, citas.Count);
            Assert.Equal(new[]
            {
                new DateTime(2026, 4, 24, 9, 0, 0),
                new DateTime(2026, 4, 25, 9, 0, 0),
                new DateTime(2026, 4, 26, 9, 0, 0)
            }, citas.Select(c => c.FechaHoraCita).ToArray());
        }

        [Fact]
        public async Task CreateAsync_ShouldRejectDuplicateBatch_WhenAnyDateOverlaps_AndNotCreatePartialRows()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Lucia");
            var servicio = await SeedServicioAsync(context, "Masaje", 60);
            await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                new DateTime(2026, 4, 25, 10, 0, 0),
                servicioId: servicio.Id);

            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var exception = await Assert.ThrowsAsync<CalendarValidationException>(() => service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Cliente",
                TelefonoCliente = "80000000",
                FechaHoraCita = new DateTime(2026, 4, 24, 10, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Tipo = "CITA",
                Duplicar = true,
                FechasDuplicadas = ["2026-04-25", "2026-04-26"]
            }));

            Assert.Contains("horario", exception.Message, StringComparison.OrdinalIgnoreCase);

            var citas = await context.Citas
                .AsNoTracking()
                .OrderBy(c => c.FechaHoraCita)
                .ToListAsync();

            Assert.Single(citas);
            Assert.Equal(new DateTime(2026, 4, 25, 10, 0, 0), citas[0].FechaHoraCita);
        }

        [Fact]
        public async Task DeleteAsync_ShouldRespectTenantIsolation()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var foreignFuncionario = await SeedFuncionarioAsync(context, "Externo");
            var foreignServicio = await SeedServicioAsync(context, "Servicio Externo", 30);
            var foreignCita = await SeedCitaAsync(
                context,
                foreignFuncionario.IdFuncionario,
                new DateTime(2026, 4, 24, 9, 0, 0),
                servicioId: foreignServicio.Id);

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(foreignCita.Id));

            Assert.Contains("no existe", exception.Message, StringComparison.OrdinalIgnoreCase);

            var remaining = await context.Citas
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(c => c.Id == foreignCita.Id);

            Assert.Equal(1, remaining);
        }

        [Fact]
        public async Task CreateAsync_ShouldPersistAppointment_WhenNotificationThrows()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Andrea");
            var servicio = await SeedServicioAsync(context, "Corte", 45);
            var service = ControllerTestSupport.CreateCalendarCommandService(context, new ThrowingCalendarWhatsAppNotificationService());

            await service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Cliente WhatsApp",
                TelefonoCliente = "88889999",
                FechaHoraCita = new DateTime(2026, 4, 24, 16, 20, 59),
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Tipo = "CITA"
            });

            var cita = await context.Citas.AsNoTracking().SingleAsync();

            Assert.Equal("Cliente WhatsApp", cita.NombreCliente);
            Assert.False(cita.ConfirmacionEnviada);
            Assert.Equal(new DateTime(2026, 4, 24, 16, 20, 0), cita.FechaHoraCita);
        }

        [Fact]
        public async Task CreateAsync_ShouldPersistCustomService()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Sofia");
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var response = await service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Cliente Personalizado",
                TelefonoCliente = "70001111",
                EsServicioPersonalizado = true,
                ServicioNombrePersonalizado = "  Tratamiento   especial  ",
                DuracionMinutos = 90,
                FechaHoraCita = new DateTime(2026, 4, 24, 10, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            });

            var cita = await context.Citas.AsNoTracking().SingleAsync();

            Assert.Null(cita.ServicioId);
            Assert.Equal("Tratamiento especial", cita.ServicioNombrePersonalizado);
            Assert.Equal(90, cita.DuracionMinutos);
            Assert.Equal("CITA", cita.Tipo);

            Assert.True(response.EsServicioPersonalizado);
            Assert.Equal("Tratamiento especial", response.ServicioNombre);
            Assert.Equal(90, response.DuracionMinutos);
        }

        [Fact]
        public async Task CreateAsync_ShouldReject_WhenCustomServiceNameIsEmpty()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Marta");
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var exception = await Assert.ThrowsAsync<CalendarValidationException>(() => service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Cliente",
                EsServicioPersonalizado = true,
                ServicioNombrePersonalizado = "   ",
                DuracionMinutos = 60,
                FechaHoraCita = new DateTime(2026, 4, 24, 11, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            }));

            Assert.Contains("personalizado", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await context.Citas.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task CreateAsync_ShouldReject_WhenCustomServiceDurationIsOutOfRange()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Pedro");
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            var exception = await Assert.ThrowsAsync<CalendarValidationException>(() => service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Cliente",
                EsServicioPersonalizado = true,
                ServicioNombrePersonalizado = "Servicio largo",
                DuracionMinutos = 600,
                FechaHoraCita = new DateTime(2026, 4, 24, 12, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            }));

            Assert.Contains("duracion", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await context.Citas.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task CreateAsync_CustomService_ShouldRejectOverlap_UsingCustomDuration()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Nadia");
            var servicio = await SeedServicioAsync(context, "Corte", 30);
            await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                new DateTime(2026, 4, 24, 10, 0, 0),
                servicioId: servicio.Id);

            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            // El servicio personalizado de 120 min iniciando 09:30 invade la cita de las 10:00.
            var exception = await Assert.ThrowsAsync<CalendarValidationException>(() => service.CreateAsync(new CalendarUpsertRequest
            {
                NombreCliente = "Cliente Personalizado",
                EsServicioPersonalizado = true,
                ServicioNombrePersonalizado = "Sesion larga",
                DuracionMinutos = 120,
                FechaHoraCita = new DateTime(2026, 4, 24, 9, 30, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            }));

            Assert.Contains("horario", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await context.Citas.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task UpdateAsync_ShouldConvertCatalogServiceToCustom()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Karla");
            var servicio = await SeedServicioAsync(context, "Corte", 30);
            var cita = await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                new DateTime(2026, 4, 24, 10, 0, 0),
                servicioId: servicio.Id);

            context.ChangeTracker.Clear();
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await service.UpdateAsync(cita.Id, new CalendarUpsertRequest
            {
                NombreCliente = "Cliente",
                EsServicioPersonalizado = true,
                ServicioNombrePersonalizado = "Servicio a medida",
                DuracionMinutos = 75,
                FechaHoraCita = new DateTime(2026, 4, 24, 10, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            });

            context.ChangeTracker.Clear();
            var updated = await context.Citas.AsNoTracking().SingleAsync(c => c.Id == cita.Id);

            Assert.Null(updated.ServicioId);
            Assert.Equal("Servicio a medida", updated.ServicioNombrePersonalizado);
            Assert.Equal(75, updated.DuracionMinutos);
        }

        // ─────────── Autorización de WhatsApp del cliente desde el formulario de cita ───────────

        [Fact]
        public async Task CreateAsync_ClienteNoAutorizado_SinCheckbox_MantieneClienteSinAutorizacion()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 45);
            var cliente = await SeedClienteAsync(context, "Cliente Sin Consentimiento", "72220000", aceptaMensajesWhatsApp: false);
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await service.CreateAsync(new CalendarUpsertRequest
            {
                ClienteId = cliente.Id,
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 9, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                AutorizarWhatsAppAlGuardar = false
            });

            context.ChangeTracker.Clear();

            var clienteActual = await context.Clientes.AsNoTracking().SingleAsync(c => c.Id == cliente.Id);
            Assert.False(clienteActual.AceptaMensajesWhatsApp);
            // La auditoría del cliente no cambia si no hubo nueva autorización.
            Assert.Equal(new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc), clienteActual.WhatsAppConsentUpdatedAtUtc);

            var cita = await context.Citas.AsNoTracking().SingleAsync();
            Assert.False(cita.WhatsAppConsentAtCreation);
        }

        [Fact]
        public async Task CreateAsync_ClienteNoAutorizado_ConCheckbox_AutorizaClienteYDisparaUnaConfirmacion()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 45);
            var cliente = await SeedClienteAsync(context, "Cliente Autoriza", "72221111", aceptaMensajesWhatsApp: false);
            var notifications = new RecordingCalendarWhatsAppNotificationService();
            var service = ControllerTestSupport.CreateCalendarCommandService(context, notifications);

            var response = await service.CreateAsync(new CalendarUpsertRequest
            {
                ClienteId = cliente.Id,
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 9, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                AutorizarWhatsAppAlGuardar = true,
                WhatsAppConsentCapturedByUserId = "user-123"
            });

            context.ChangeTracker.Clear();

            var clienteActual = await context.Clientes.AsNoTracking().SingleAsync(c => c.Id == cliente.Id);
            Assert.True(clienteActual.AceptaMensajesWhatsApp);
            Assert.Equal("CitaManual", clienteActual.WhatsAppConsentSource);
            Assert.Equal("wa_optin_v1", clienteActual.WhatsAppConsentTextVersion);
            Assert.Equal("user-123", clienteActual.WhatsAppConsentCapturedByUserId);
            Assert.True(clienteActual.WhatsAppConsentUpdatedAtUtc > new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc));

            var cita = await context.Citas.AsNoTracking().SingleAsync();
            Assert.True(cita.WhatsAppConsentAtCreation);

            // El pipeline de confirmación se dispara una sola vez para la cita creada.
            Assert.Equal(new[] { response.Id }, notifications.ConfirmationCitaIds);
        }

        [Fact]
        public async Task CreateAsync_ClienteNoAutorizado_ConCheckbox_SiFallaLaCita_NoPersisteAutorizacion()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var cliente = await SeedClienteAsync(context, "Cliente Rollback", "72222222", aceptaMensajesWhatsApp: false);

            // Cita existente que provoca un traslape → la creación falla dentro de la transacción.
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 24, 10, 0, 0), servicioId: servicio.Id);
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await Assert.ThrowsAsync<CalendarValidationException>(() => service.CreateAsync(new CalendarUpsertRequest
            {
                ClienteId = cliente.Id,
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 10, 30, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                AutorizarWhatsAppAlGuardar = true,
                WhatsAppConsentCapturedByUserId = "user-123"
            }));

            context.ChangeTracker.Clear();

            // La autorización NO debe quedar persistida si la cita no se guardó (atomicidad).
            var clienteActual = await context.Clientes.AsNoTracking().SingleAsync(c => c.Id == cliente.Id);
            Assert.False(clienteActual.AceptaMensajesWhatsApp);
            Assert.Equal(new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc), clienteActual.WhatsAppConsentUpdatedAtUtc);
        }

        [Fact]
        public async Task CreateAsync_ClienteYaAutorizado_NoPierdeAutorizacion_NiReSellaAuditoria()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 45);
            var cliente = await SeedClienteAsync(context, "Cliente Autorizado", "72223333", aceptaMensajesWhatsApp: true);
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await service.CreateAsync(new CalendarUpsertRequest
            {
                ClienteId = cliente.Id,
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 9, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                // Incluso si por overposting llegara true, no debe re-sellar la auditoría existente.
                AutorizarWhatsAppAlGuardar = true,
                WhatsAppConsentCapturedByUserId = "otro-user"
            });

            context.ChangeTracker.Clear();

            var clienteActual = await context.Clientes.AsNoTracking().SingleAsync(c => c.Id == cliente.Id);
            Assert.True(clienteActual.AceptaMensajesWhatsApp);
            Assert.Equal(new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc), clienteActual.WhatsAppConsentUpdatedAtUtc);
            Assert.Equal("ClienteForm", clienteActual.WhatsAppConsentSource);

            var cita = await context.Citas.AsNoTracking().SingleAsync();
            Assert.True(cita.WhatsAppConsentAtCreation);
        }

        [Fact]
        public async Task CreateAsync_ClienteDeOtroTenant_ConCheckbox_RechazaYNoModificaDatos()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteExterno = await SeedClienteAsync(context, "Cliente Externo", "74445555", aceptaMensajesWhatsApp: false);

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 30);
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await Assert.ThrowsAsync<CalendarValidationException>(() => service.CreateAsync(new CalendarUpsertRequest
            {
                ClienteId = clienteExterno.Id,
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 13, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                AutorizarWhatsAppAlGuardar = true,
                WhatsAppConsentCapturedByUserId = "user-123"
            }));

            context.ChangeTracker.Clear();

            // El cliente de otro tenant no debe quedar modificado.
            var clienteActual = await context.Clientes
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(c => c.Id == clienteExterno.Id);
            Assert.False(clienteActual.AceptaMensajesWhatsApp);
            Assert.Empty(await context.Citas.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task CreateAsync_ClienteSinTelefonoValido_ConCheckbox_NoAutoriza()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 45);
            var cliente = await SeedClienteAsync(context, "Cliente Sin Telefono", "   ", aceptaMensajesWhatsApp: false);
            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await service.CreateAsync(new CalendarUpsertRequest
            {
                ClienteId = cliente.Id,
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 9, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                AutorizarWhatsAppAlGuardar = true,
                WhatsAppConsentCapturedByUserId = "user-123"
            });

            context.ChangeTracker.Clear();

            var clienteActual = await context.Clientes.AsNoTracking().SingleAsync(c => c.Id == cliente.Id);
            Assert.False(clienteActual.AceptaMensajesWhatsApp);

            var cita = await context.Citas.AsNoTracking().SingleAsync();
            Assert.False(cita.WhatsAppConsentAtCreation);
        }

        [Fact]
        public async Task UpdateAsync_ClienteNoAutorizado_SinCheckbox_NoSobrescribeConsentimiento()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 45);
            var cliente = await SeedClienteAsync(context, "Cliente Edicion", "72224444", aceptaMensajesWhatsApp: false);
            var cita = await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 24, 10, 0, 0), servicioId: servicio.Id);
            cita.ClienteId = cliente.Id;
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await service.UpdateAsync(cita.Id, new CalendarUpsertRequest
            {
                ClienteId = cliente.Id,
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 11, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                AutorizarWhatsAppAlGuardar = false
            });

            context.ChangeTracker.Clear();

            var clienteActual = await context.Clientes.AsNoTracking().SingleAsync(c => c.Id == cliente.Id);
            Assert.False(clienteActual.AceptaMensajesWhatsApp);
            Assert.Equal(new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc), clienteActual.WhatsAppConsentUpdatedAtUtc);
        }

        [Fact]
        public async Task UpdateAsync_ClienteNoAutorizado_ConCheckbox_RegistraAutorizacion()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 45);
            var cliente = await SeedClienteAsync(context, "Cliente Edicion Autoriza", "72225555", aceptaMensajesWhatsApp: false);
            var cita = await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 24, 10, 0, 0), servicioId: servicio.Id);
            cita.ClienteId = cliente.Id;
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateCalendarCommandService(context);

            await service.UpdateAsync(cita.Id, new CalendarUpsertRequest
            {
                ClienteId = cliente.Id,
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 11, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                AutorizarWhatsAppAlGuardar = true,
                WhatsAppConsentCapturedByUserId = "editor-user"
            });

            context.ChangeTracker.Clear();

            var clienteActual = await context.Clientes.AsNoTracking().SingleAsync(c => c.Id == cliente.Id);
            Assert.True(clienteActual.AceptaMensajesWhatsApp);
            Assert.Equal("CitaManual", clienteActual.WhatsAppConsentSource);
            Assert.Equal("editor-user", clienteActual.WhatsAppConsentCapturedByUserId);
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            bool activo = true)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                Detalle = "Calendario",
                Activo = true
            };

            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#123456",
                PorcentajeGanancia = 40m,
                PorcentajeProducto = 10m,
                FechaIngreso = new DateTime(2026, 4, 1),
                Activo = activo
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task<Servicio> SeedServicioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            int duracionMinutos,
            bool activo = true)
        {
            var servicio = new Servicio
            {
                Nombre = nombre,
                Precio = 25m,
                DuracionMinutos = duracionMinutos,
                Activo = activo
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<ClientesModel> SeedClienteAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            string telefono,
            bool aceptaMensajesWhatsApp)
        {
            var cliente = new ClientesModel
            {
                Nombre = nombre,
                NumeroTelefono = telefono,
                AceptaMensajesWhatsApp = aceptaMensajesWhatsApp,
                WhatsAppConsentUpdatedAtUtc = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc),
                WhatsAppConsentSource = "ClienteForm",
                WhatsAppConsentTextVersion = "wa_optin_v1",
                FrecuenciaVisita = 30,
                FechaUltimaVisita = new DateTime(2026, 4, 1)
            };

            context.Clientes.Add(cliente);
            await context.SaveChangesAsync();
            return cliente;
        }

        private static async Task<Cita> SeedCitaAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            DateTime fechaHora,
            int? servicioId = null,
            string tipo = "CITA",
            int? duracionMinutos = null)
        {
            var cita = new Cita
            {
                NombreCliente = tipo == "DESCANSO" ? "DESCANSO" : $"Cliente {Guid.NewGuid():N}",
                TelefonoCliente = tipo == "DESCANSO" ? null : "80000000",
                ServicioId = tipo == "DESCANSO" ? null : servicioId,
                FechaHoraCita = fechaHora,
                FuncionarioId = funcionarioId,
                Tipo = tipo,
                DuracionMinutos = tipo == "DESCANSO" ? duracionMinutos : null
            };

            context.Citas.Add(cita);
            await context.SaveChangesAsync();
            return cita;
        }

        private sealed class ThrowingCalendarWhatsAppNotificationService : ICalendarWhatsAppNotificationService
        {
            public Task SendAppointmentConfirmationAsync(int citaId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Meta WhatsApp no disponible");

            public Task SendAppointmentReminderAsync(int citaId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Meta WhatsApp no disponible");

            public Task<LuxuryApp.Services.Calendar.WhatsAppConfirmationSendResult> SendConfirmationNowAsync(int citaId, string source, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Meta WhatsApp no disponible");

            public Task QueueAppointmentConfirmationAsync(int citaId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Meta WhatsApp no disponible");

            public Task QueueAppointmentReminderAsync(int citaId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Meta WhatsApp no disponible");

            public Task QueueImmediateReminderOnCreateAsync(int citaId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Meta WhatsApp no disponible");

            public Task ProcessInboundReplyAsync(System.Text.Json.JsonElement payload, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task ProcessStatusUpdateAsync(System.Text.Json.JsonElement payload, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task ScheduleDueRemindersAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task GenerateDailyBatchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task ProcessPendingNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task RescheduleConfirmationIfPendingAsync(int citaId, DateTime newFechaHoraCita, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Meta WhatsApp no disponible");

            public Task CancelPendingNotificationsAsync(int citaId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Meta WhatsApp no disponible");
        }

        // Registra las llamadas al pipeline de confirmación para comprobar que se dispara una sola vez.
        private sealed class RecordingCalendarWhatsAppNotificationService : ICalendarWhatsAppNotificationService
        {
            public List<int> ConfirmationCitaIds { get; } = new();

            public Task QueueAppointmentConfirmationAsync(int citaId, CancellationToken cancellationToken = default)
            {
                ConfirmationCitaIds.Add(citaId);
                return Task.CompletedTask;
            }

            public Task SendAppointmentConfirmationAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task SendAppointmentReminderAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<LuxuryApp.Services.Calendar.WhatsAppConfirmationSendResult> SendConfirmationNowAsync(int citaId, string source, CancellationToken cancellationToken = default) =>
                Task.FromResult(new LuxuryApp.Services.Calendar.WhatsAppConfirmationSendResult(LuxuryApp.Services.Calendar.WhatsAppConfirmationOutcome.Sent, "ok"));
            public Task QueueAppointmentReminderAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task QueueImmediateReminderOnCreateAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ProcessInboundReplyAsync(System.Text.Json.JsonElement payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ProcessStatusUpdateAsync(System.Text.Json.JsonElement payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ScheduleDueRemindersAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task GenerateDailyBatchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ProcessPendingNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RescheduleConfirmationIfPendingAsync(int citaId, DateTime newFechaHoraCita, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task CancelPendingNotificationsAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
