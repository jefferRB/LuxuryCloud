using System.Reflection;
using System.Text.Json;
using LuxuryApp.Controllers.Reservas;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Notifications;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Horarios;
using LuxuryApp.Services.Notifications;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Una solicitud de reserva PENDIENTE ocupa agenda igual que una cita confirmada.
    ///
    /// <para>
    /// La regla vive en la fuente única de disponibilidad (<see cref="IFuncionarioAvailabilityService"/>),
    /// así que estas pruebas la ejercitan desde todos los consumidores: disponibilidad del día,
    /// próximos espacios, creación manual de citas y el propio envío público.
    /// </para>
    /// </summary>
    public class BookingPendingHoldTests
    {
        // Jornada del negocio en los tests: 2026-05-27, 08:00 a 18:00, todos los días laborales.
        private static readonly DateOnly Fecha = new(2026, 5, 27);

        // ── 1. Pending bloquea exactamente su intervalo ────────────────────────────────────

        [Fact]
        public async Task PendingRequest_OccupiesItsFullInterval()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Color", 90);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            await SeedPendingAsync(context, servicio.Id, funcionario.IdFuncionario, At(12, 0), 90);

            var availability = ControllerTestSupport.CreateAvailabilityService(context);
            var ocupados = await availability.GetBusyIntervalsAsync(
                new[] { funcionario.IdFuncionario }, Fecha, Fecha);

            var intervalo = Assert.Single(ocupados[funcionario.IdFuncionario]);
            Assert.Equal(BusyIntervalSources.SolicitudPendiente, intervalo.Origen);
            Assert.Equal(At(12, 0), intervalo.Inicio);
            Assert.Equal(At(13, 30), intervalo.Fin);
        }

        // ── 2. El solapamiento parcial también bloquea (no se compara sólo la hora exacta) ──

        [Theory]
        [InlineData(11, 30, 60, false)] // 11:30-12:30 → pisa el arranque
        [InlineData(13, 0, 30, false)]  // 13:00-13:30 → pisa el final
        [InlineData(12, 30, 30, false)] // 12:30-13:00 → totalmente dentro
        [InlineData(11, 0, 60, true)]   // 11:00-12:00 → termina justo cuando arranca: libre
        [InlineData(13, 30, 30, true)]  // 13:30-14:00 → arranca justo al terminar: libre
        public async Task PendingRequest_BlocksAnyOverlappingInterval(
            int hora,
            int minuto,
            int duracion,
            bool esperaDisponible)
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Color", 90);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            await SeedPendingAsync(context, servicio.Id, funcionario.IdFuncionario, At(12, 0), 90);

            var availability = ControllerTestSupport.CreateAvailabilityService(context);
            var resultado = await availability.CheckAsync(
                funcionario.IdFuncionario, At(hora, minuto), duracion);

            Assert.Equal(esperaDisponible, resultado.Disponible);

            if (!esperaDisponible)
            {
                Assert.Equal(BusyIntervalSources.SolicitudPendiente, resultado.Conflicto!.Origen);
                Assert.Contains("solicitud de reserva", resultado.Motivo!, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── 3. Rechazada libera de inmediato ───────────────────────────────────────────────

        [Theory]
        [InlineData(BookingRequestStates.Rejected)]
        [InlineData(BookingRequestStates.Expired)]
        [InlineData(BookingRequestStates.CancelledByClient)]
        public async Task NonPendingRequest_DoesNotOccupyAgenda(string estado)
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            await SeedPendingAsync(context, servicio.Id, funcionario.IdFuncionario, At(12, 0), 60, estado: estado);

            var availability = ControllerTestSupport.CreateAvailabilityService(context);
            var ocupados = await availability.GetBusyIntervalsAsync(
                new[] { funcionario.IdFuncionario }, Fecha, Fecha);

            Assert.False(ocupados.ContainsKey(funcionario.IdFuncionario));
        }

        // ── 4. Confirmada NO se cuenta dos veces: ocupa la cita creada ─────────────────────

        [Fact]
        public async Task ConfirmedRequest_DoesNotDoubleCount_OnlyTheCitaOccupies()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            var cita = new Cita
            {
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                FechaHoraCita = At(12, 0),
                Tipo = "CITA",
                NombreCliente = "Cliente"
            };
            context.Citas.Add(cita);
            await context.SaveChangesAsync();

            var solicitud = await SeedPendingAsync(
                context, servicio.Id, funcionario.IdFuncionario, At(12, 0), 60,
                estado: BookingRequestStates.Confirmed);
            solicitud.ConvertedCitaId = cita.Id;
            await context.SaveChangesAsync();

            var availability = ControllerTestSupport.CreateAvailabilityService(context);
            var ocupados = await availability.GetBusyIntervalsAsync(
                new[] { funcionario.IdFuncionario }, Fecha, Fecha);

            var intervalo = Assert.Single(ocupados[funcionario.IdFuncionario]);
            Assert.Equal(BusyIntervalSources.Cita, intervalo.Origen);
        }

        // ── 5 y 6. "Cualquier profesional" reserva UNO concreto; los demás siguen libres ───

        [Fact]
        public async Task Submit_AnyProfessional_ReservesOneConcreteFuncionarioOnly()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var jamie = await SeedFuncionarioAsync(context, "Jamie");
            var drew = await SeedFuncionarioAsync(context, "Drew");

            var service = BuildPublicService(context);
            var resultado = await service.SubmitAsync(
                BuildContext(tenantProvider.TenantId),
                NewInput(servicio.Id, "12:00", "88880001", funcionarioId: null));

            Assert.True(resultado.Success);

            var solicitud = await context.BookingRequests.AsNoTracking().SingleAsync();

            // Lo que pidió el cliente se conserva: "cualquiera".
            Assert.Null(solicitud.FuncionarioId);
            // El servidor reservó un recurso concreto para poder sostener el hold.
            Assert.NotNull(solicitud.FuncionarioAsignadoId);

            var reservado = solicitud.FuncionarioAsignadoId!.Value;
            var libre = reservado == jamie.IdFuncionario
                ? drew.IdFuncionario
                : jamie.IdFuncionario;

            var availability = ControllerTestSupport.CreateAvailabilityService(context);

            var ocupadoReservado = await availability.CheckAsync(reservado, At(12, 0), 60);
            Assert.False(ocupadoReservado.Disponible);

            // El otro profesional NO queda bloqueado: no se bloquea a todo el equipo.
            var ocupadoLibre = await availability.CheckAsync(libre, At(12, 0), 60);
            Assert.True(ocupadoLibre.Disponible);

            // Y la hora sigue ofreciéndose porque queda capacidad con el otro profesional.
            var booking = ControllerTestSupport.CreateBookingAvailabilityService(
                context, new FixedBusinessDateTimeProvider());
            var horas = await booking.GetAvailableSlotsAsync(servicio.Id, Fecha, funcionarioId: null);
            Assert.Contains("12:00", horas);
        }

        // ── 7. Profesional específico: se reserva esa agenda ──────────────────────────────

        [Fact]
        public async Task Submit_SpecificProfessional_HoldsThatAgenda()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var jamie = await SeedFuncionarioAsync(context, "Jamie");
            var drew = await SeedFuncionarioAsync(context, "Drew");

            var service = BuildPublicService(context);
            var resultado = await service.SubmitAsync(
                BuildContext(tenantProvider.TenantId),
                NewInput(servicio.Id, "12:00", "88880002", funcionarioId: jamie.IdFuncionario));

            Assert.True(resultado.Success);

            var solicitud = await context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(jamie.IdFuncionario, solicitud.FuncionarioId);
            Assert.Equal(jamie.IdFuncionario, solicitud.FuncionarioAsignadoId);

            var availability = ControllerTestSupport.CreateAvailabilityService(context);
            Assert.False((await availability.CheckAsync(jamie.IdFuncionario, At(12, 0), 60)).Disponible);
            Assert.True((await availability.CheckAsync(drew.IdFuncionario, At(12, 0), 60)).Disponible);
        }

        // ── 8. Los próximos espacios no ofrecen un intervalo pendiente ────────────────────

        [Fact]
        public async Task NextAvailableSlots_SkipPendingInterval()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            await SeedPendingAsync(context, servicio.Id, funcionario.IdFuncionario, At(12, 0), 60);

            var booking = ControllerTestSupport.CreateBookingAvailabilityService(
                context, new FixedBusinessDateTimeProvider());

            var sugerencias = await booking.GetNextAvailableSlotsAsync(
                servicio.Id, Fecha, funcionario.IdFuncionario, maxSuggestions: 10);

            // Ningún espacio sugerido puede solapar 12:00-13:00.
            Assert.DoesNotContain(sugerencias, s =>
                s.Fecha == Fecha &&
                s.Hora.ToTimeSpan() < new TimeSpan(13, 0, 0) &&
                s.Hora.ToTimeSpan().Add(TimeSpan.FromMinutes(60)) > new TimeSpan(12, 0, 0));
        }

        // ── 9. La disponibilidad diaria tampoco lo ofrece ─────────────────────────────────

        [Fact]
        public async Task DailyAvailability_SkipsPendingInterval()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            await SeedPendingAsync(context, servicio.Id, funcionario.IdFuncionario, At(12, 0), 60);

            var booking = ControllerTestSupport.CreateBookingAvailabilityService(
                context, new FixedBusinessDateTimeProvider());

            var horas = await booking.GetAvailableSlotsAsync(servicio.Id, Fecha, funcionario.IdFuncionario);

            Assert.DoesNotContain("11:30", horas); // 11:30-12:30 solapa
            Assert.DoesNotContain("12:00", horas);
            Assert.DoesNotContain("12:30", horas);
            Assert.Contains("11:00", horas);       // 11:00-12:00 pega justo antes
            Assert.Contains("13:00", horas);       // 13:00-14:00 pega justo después
        }

        // ── 10. La creación manual desde el panel tampoco puede pisarlo ──────────────────

        [Fact]
        public async Task ManualAppointment_CannotOverlapPendingRequest()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            await SeedPendingAsync(context, servicio.Id, funcionario.IdFuncionario, At(12, 0), 60);

            var calendar = ControllerTestSupport.CreateCalendarCommandService(context);

            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                calendar.CreateAsync(new CalendarUpsertRequest
                {
                    Tipo = "CITA",
                    ServicioId = servicio.Id,
                    FuncionarioId = funcionario.IdFuncionario,
                    FechaHoraCita = At(12, 30),
                    NombreCliente = "Walk in",
                    TelefonoCliente = "88887777"
                }));

            Assert.Contains("solicitud de reserva", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await context.Citas.ToListAsync());
        }

        // ── 11. Rechazar libera el espacio ───────────────────────────────────────────────

        [Fact]
        public async Task Reject_ReleasesTheSlot()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            var solicitud = await SeedPendingAsync(
                context, servicio.Id, funcionario.IdFuncionario, At(12, 0), 60);

            var availability = ControllerTestSupport.CreateAvailabilityService(context);
            Assert.False((await availability.CheckAsync(funcionario.IdFuncionario, At(12, 0), 60)).Disponible);

            var service = BuildRequestService(context);
            var resultado = await service.RejectAsync(solicitud.Id, "Sin cupo", "admin");

            Assert.True(resultado.Success);
            Assert.True((await availability.CheckAsync(funcionario.IdFuncionario, At(12, 0), 60)).Disponible);
        }

        // ── 12. Confirmar convierte el hold en una cita real ─────────────────────────────

        [Fact]
        public async Task Confirm_TurnsTheHoldIntoARealAppointment()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            // Solicitud "cualquiera" ya asignada por el servidor, tal como la deja SubmitAsync.
            var solicitud = await SeedPendingAsync(
                context, servicio.Id, funcionario.IdFuncionario, At(12, 0), 60, solicitoCualquiera: true);

            var service = BuildRequestService(context);
            var resultado = await service.ConfirmAsync(solicitud.Id, funcionarioIdOverride: null, "admin");

            Assert.True(resultado.Success);
            Assert.NotNull(resultado.CitaId);

            var actualizada = await context.BookingRequests.AsNoTracking().SingleAsync(r => r.Id == solicitud.Id);
            Assert.Equal(BookingRequestStates.Confirmed, actualizada.Estado);
            Assert.Equal(resultado.CitaId, actualizada.ConvertedCitaId);
            Assert.Equal(funcionario.IdFuncionario, actualizada.FuncionarioAsignadoId);
            // Lo que pidió el cliente NO se reescribe: seguía siendo "cualquiera".
            Assert.Null(actualizada.FuncionarioId);

            var cita = await context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal(At(12, 0), cita.FechaHoraCita);
            Assert.Equal(funcionario.IdFuncionario, cita.FuncionarioId);

            // El intervalo lo ocupa la cita, no la solicitud: un único bloque.
            var availability = ControllerTestSupport.CreateAvailabilityService(context);
            var ocupados = await availability.GetBusyIntervalsAsync(
                new[] { funcionario.IdFuncionario }, Fecha, Fecha);

            var intervalo = Assert.Single(ocupados[funcionario.IdFuncionario]);
            Assert.Equal(BusyIntervalSources.Cita, intervalo.Origen);
        }

        // ── 13. Dos envíos SIMULTÁNEOS incompatibles: sólo uno consigue el intervalo ─────

        [Fact]
        public async Task TwoConcurrentSubmits_ForOverlappingIntervals_OnlyOneWins()
        {
            var tenantId = Guid.NewGuid();

            // Base en archivo: la concurrencia real necesita DOS conexiones distintas, así que no
            // sirve la base en memoria por conexión de los demás tests.
            var dbPath = Path.Combine(
                Path.GetTempPath(),
                $"luxury-booking-hold-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={dbPath};Pooling=False;Default Timeout=5";

            try
            {
                int servicioId;
                int funcionarioId;

                await using (var seed = CreateFileContext(connectionString, tenantId))
                {
                    await seed.Database.EnsureCreatedAsync();
                    await EnsureTenantAsync(seed, tenantId);
                    await SeedSettingsAsync(seed);
                    servicioId = (await SeedServicioAsync(seed, "Color", 60)).Id;
                    funcionarioId = (await SeedFuncionarioAsync(seed, "Jamie")).IdFuncionario;
                }

                await using var contextA = CreateFileContext(connectionString, tenantId);
                await using var contextB = CreateFileContext(connectionString, tenantId);

                var serviceA = BuildPublicService(contextA);
                var serviceB = BuildPublicService(contextB);
                var tenantContext = BuildContext(tenantId);

                // 12:00-13:00 y 12:30-13:30 sobre el MISMO profesional: incompatibles.
                var arranque = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                async Task<PublicBookingSubmitResult> Enviar(
                    IPublicBookingService service, string hora, string telefono)
                {
                    await arranque.Task;
                    try
                    {
                        return await service.SubmitAsync(
                            tenantContext,
                            NewInput(servicioId, hora, telefono, funcionarioId));
                    }
                    catch (Exception ex)
                    {
                        // Un fallo de base de datos también significa "este no ganó".
                        return PublicBookingSubmitResult.Fail(ex.Message);
                    }
                }

                var envioA = Task.Run(() => Enviar(serviceA, "12:00", "88880011"));
                var envioB = Task.Run(() => Enviar(serviceB, "12:30", "88880022"));

                arranque.SetResult();
                var resultados = await Task.WhenAll(envioA, envioB);

                await using var verificacion = CreateFileContext(connectionString, tenantId);
                var pendientes = await verificacion.BookingRequests
                    .AsNoTracking()
                    .Where(r => r.Estado == BookingRequestStates.Pending)
                    .ToListAsync();

                // La invariante: pase lo que pase con el orden real, sólo un hold sobrevive.
                Assert.Single(pendientes);
                Assert.Equal(1, resultados.Count(r => r.Success));
                Assert.Equal(funcionarioId, pendientes[0].FuncionarioAsignadoId);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    try { File.Delete(dbPath); } catch (IOException) { }
                }
            }
        }

        // ── 14. Cross-tenant: un hold del tenant A jamás afecta al tenant B ──────────────

        [Fact]
        public async Task PendingRequest_NeverAffectsAnotherTenant()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantA);
            await SeedSettingsAsync(context);
            var servicioA = await SeedServicioAsync(context, "Corte A", 60);
            var funcionarioA = await SeedFuncionarioAsync(context, "Jamie A");
            await SeedPendingAsync(context, servicioA.Id, funcionarioA.IdFuncionario, At(12, 0), 60);

            tenantProvider.TenantId = tenantB;
            await EnsureTenantAsync(context, tenantB);
            await SeedSettingsAsync(context);
            var servicioB = await SeedServicioAsync(context, "Corte B", 60);
            var funcionarioB = await SeedFuncionarioAsync(context, "Jamie B");

            var availability = ControllerTestSupport.CreateAvailabilityService(context);

            // El tenant B ve su agenda completamente libre...
            Assert.True((await availability.CheckAsync(funcionarioB.IdFuncionario, At(12, 0), 60)).Disponible);

            // ...y ni siquiera puede "ver" el funcionario del tenant A a través del motor.
            var ocupados = await availability.GetBusyIntervalsAsync(
                new[] { funcionarioA.IdFuncionario, funcionarioB.IdFuncionario }, Fecha, Fecha);
            Assert.Empty(ocupados);

            var booking = ControllerTestSupport.CreateBookingAvailabilityService(
                context, new FixedBusinessDateTimeProvider());
            Assert.Contains("12:00", await booking.GetAvailableSlotsAsync(
                servicioB.Id, Fecha, funcionarioB.IdFuncionario));

            // Y el hold del tenant A sigue en pie en su propio tenant.
            tenantProvider.TenantId = tenantA;
            Assert.False((await availability.CheckAsync(funcionarioA.IdFuncionario, At(12, 0), 60)).Disponible);
        }

        // ── 17. Una solicitud inválida no deja holds a medias ───────────────────────────

        [Theory]
        [InlineData("2026-05-25", "12:00")] // fecha pasada
        [InlineData("2026-05-27", "07:00")] // antes de abrir
        [InlineData("2026-05-27", "17:45")] // el bloque no cabe antes del cierre
        public async Task InvalidSubmit_LeavesNoPartialHold(string fecha, string hora)
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            var service = BuildPublicService(context);
            var input = NewInput(servicio.Id, hora, "88880033", funcionario.IdFuncionario);
            input.Fecha = fecha;

            var resultado = await service.SubmitAsync(BuildContext(tenantProvider.TenantId), input);

            Assert.False(resultado.Success);
            Assert.Empty(await context.BookingRequests.ToListAsync());
        }

        [Fact]
        public async Task HiddenService_LeavesNoPartialHold()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            await SeedFuncionarioAsync(context, "Jamie");

            var service = BuildPublicService(context);

            // Id de un servicio que no existe en este tenant.
            var resultado = await service.SubmitAsync(
                BuildContext(tenantProvider.TenantId),
                NewInput(servicio.Id + 9999, "12:00", "88880044", funcionarioId: null));

            Assert.False(resultado.Success);
            Assert.Empty(await context.BookingRequests.ToListAsync());
        }

        [Fact]
        public void SubmitEndpoint_UsesItsOwnRateLimitingPolicy()
        {
            var method = typeof(PublicReservasController)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(m => m.Name == nameof(PublicReservasController.Solicitar));

            var policy = method
                .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
                .Cast<EnableRateLimitingAttribute>()
                .Single();

            // Crear una solicitud ahora ocupa agenda: no puede compartir la cuota de navegación.
            Assert.Equal("PublicBookingSubmit", policy.PolicyName);
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────

        private static DateTime At(int hora, int minuto) =>
            Fecha.ToDateTime(new TimeOnly(hora, minuto));

        private static ApplicationDbContext CreateFileContext(string connectionString, Guid tenantId)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString)
                .Options;

            return new ApplicationDbContext(
                options,
                new TestTenantProvider { TenantId = tenantId },
                NullLogger<ApplicationDbContext>.Instance);
        }

        private static PublicBookingService BuildPublicService(ApplicationDbContext context)
        {
            var catalog = new BookingCatalogService(context);
            return new PublicBookingService(
                context,
                new NoOpBookingSettingsService(),
                ControllerTestSupport.CreateBookingAvailabilityService(
                    context, new FixedBusinessDateTimeProvider(), catalog),
                catalog,
                new FixedBusinessDateTimeProvider(),
                new FakeTenantWhatsAppFeatureService { IsEnabled = false },
                new NoOpNotificationService(),
                new HttpContextAccessor(),
                ControllerTestSupport.CreateClienteIdentityService(context),
                NullLogger<PublicBookingService>.Instance);
        }

        private static BookingRequestService BuildRequestService(ApplicationDbContext context) =>
            new(
                context,
                ControllerTestSupport.CreateCalendarCommandService(context),
                new NoOpCalendarWhatsAppNotificationService(),
                ControllerTestSupport.CreateBookingAvailabilityService(
                    context, new FixedBusinessDateTimeProvider()),
                new NoOpBookingSettingsService(),
                new FixedBusinessDateTimeProvider(),
                new HttpContextAccessor(),
                new FakeTenantWhatsAppFeatureService { IsEnabled = true },
                new RecordingBookingRejectionWhatsAppService(),
                ControllerTestSupport.CreateClienteIdentityService(context),
                NullLogger<BookingRequestService>.Instance);

        private static PublicBookingTenantContext BuildContext(Guid tenantId) => new()
        {
            TenantId = tenantId,
            NombreNegocio = "Test",
            Slug = "test",
            PermiteElegirFuncionario = true,
            PermiteCualquierFuncionario = true,
            MostrarFotosFuncionarios = false,
            MinAdvanceMinutes = 0,
            MaxDaysAhead = 30
        };

        private static PublicBookingRequestInput NewInput(
            int servicioId,
            string hora,
            string telefono,
            int? funcionarioId) => new()
            {
                ServicioId = servicioId,
                FuncionarioId = funcionarioId,
                Fecha = Fecha.ToString("yyyy-MM-dd"),
                Hora = hora,
                Nombre = "Cliente Prueba",
                Telefono = telefono,
                AceptaWhatsApp = false,
                SubmissionToken = Guid.NewGuid().ToString("N")
            };

        private static async Task<BookingRequest> SeedPendingAsync(
            ApplicationDbContext context,
            int servicioId,
            int funcionarioAsignadoId,
            DateTime inicio,
            int duracion,
            string estado = BookingRequestStates.Pending,
            bool solicitoCualquiera = true)
        {
            var solicitud = new BookingRequest
            {
                ServicioId = servicioId,
                FuncionarioId = solicitoCualquiera ? null : funcionarioAsignadoId,
                FuncionarioAsignadoId = funcionarioAsignadoId,
                NombreCliente = "Cliente",
                TelefonoCliente = "88889999",
                FechaHoraInicioSolicitada = inicio,
                FechaHoraFinCalculada = inicio.AddMinutes(duracion),
                DuracionMinutos = duracion,
                Estado = estado,
                Origen = BookingRequestOrigins.PublicLink,
                CreatedAtUtc = DateTime.UtcNow
            };

            context.BookingRequests.Add(solicitud);
            await context.SaveChangesAsync();
            return solicitud;
        }

        private static async Task SeedSettingsAsync(ApplicationDbContext context)
        {
            context.TenantBookingSettings.Add(new TenantBookingSettings
            {
                PublicBookingEnabled = true,
                PublicBookingSlug = $"slug-{Guid.NewGuid():N}"[..18],
                OpenTime = new TimeOnly(8, 0),
                CloseTime = new TimeOnly(18, 0),
                SlotIntervalMinutes = 30,
                PublicBookingMinAdvanceMinutes = 0,
                PublicBookingMaxDaysAhead = 30,
                WorkingDaysMask = 0b111_1111
            });
            await context.SaveChangesAsync();
        }

        private static async Task<Servicio> SeedServicioAsync(
            ApplicationDbContext context, string nombre, int duracion)
        {
            var servicio = new Servicio
            {
                Nombre = nombre,
                Precio = 12000m,
                DuracionMinutos = duracion,
                Activo = true
            };
            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ApplicationDbContext context, string nombre)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                Detalle = "Reservas",
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
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task EnsureTenantAsync(ApplicationDbContext context, Guid tenantId)
        {
            if (!await context.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
            {
                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Test", Activo = true });
                await context.SaveChangesAsync();
            }
        }

        // ── fakes ──────────────────────────────────────────────────────────────────────────

        private sealed class NoOpBookingSettingsService : IBookingSettingsService
        {
            public Task<BookingSettingsViewModel> BuildSettingsViewModelAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new BookingSettingsViewModel());
            public Task SaveSettingsAsync(BookingSettingsViewModel input, string? userId, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
            public Task<PublicBookingTenantContext?> ResolvePublicBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
                Task.FromResult<PublicBookingTenantContext?>(null);
            public Task<string?> GetCurrentSlugAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>(null);
        }

        private sealed class NoOpNotificationService : INotificationService
        {
            public Task<NotificationSummary> GetSummaryAsync(int limit = 15, CancellationToken cancellationToken = default) =>
                Task.FromResult(new NotificationSummary());
            public Task<int> MarkAllAsReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
            public Task<bool> MarkAsReadAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(true);
            public Task CreateBookingRequestReceivedAsync(BookingRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task CreateAppointmentCancelledViaWhatsAppAsync(Cita cita, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
