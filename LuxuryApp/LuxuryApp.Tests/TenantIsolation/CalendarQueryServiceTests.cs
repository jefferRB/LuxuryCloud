using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class CalendarQueryServiceTests
    {
        [Fact]
        public async Task GetAppointmentsByDayAsync_ShouldReturnOnlySelectedDayAndCurrentTenant()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 45);

            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 24, 9, 0, 0), servicio.Id);
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 25, 0, 0, 0), servicio.Id);

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();

            var foreignFuncionario = await SeedFuncionarioAsync(context, "Externa");
            var foreignServicio = await SeedServicioAsync(context, "Color", 60);
            await SeedCitaAsync(context, foreignFuncionario.IdFuncionario, new DateTime(2026, 4, 24, 10, 0, 0), foreignServicio.Id);

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateCalendarQueryService(context);

            var citas = await service.GetAppointmentsByDayAsync(new DateTime(2026, 4, 24, 14, 30, 0));

            Assert.Single(citas);
            Assert.Equal(new DateTime(2026, 4, 24, 9, 0, 0), citas[0].FechaHoraCita);
            Assert.Equal("Ana", citas[0].FuncionarioNombre);
        }

        [Fact]
        public async Task GetCitasCountByMonthAsync_ShouldCountOnlyCurrentMonthCitas()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Luis");
            var servicio = await SeedServicioAsync(context, "Corte", 45);

            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 2, 9, 0, 0), servicio.Id);
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 2, 11, 0, 0), servicio.Id);
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 3, 8, 0, 0), null, tipo: "DESCANSO", duracionMinutos: 30);
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 5, 2, 9, 0, 0), servicio.Id);

            var service = ControllerTestSupport.CreateCalendarQueryService(context);

            var counts = await service.GetCitasCountByMonthAsync(2026, 4);

            Assert.Single(counts);
            Assert.Equal(2, counts[0].Day);
            Assert.Equal(2, counts[0].Count);
        }

        [Fact]
        public async Task GetUpcomingAppointmentsAsync_ShouldReturnFutureSameDayAppointments_WithOptionalFuncionarioFilter()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionarioA = await SeedFuncionarioAsync(context, "Mariana");
            var funcionarioB = await SeedFuncionarioAsync(context, "Pablo");
            var servicio = await SeedServicioAsync(context, "Peinado", 30);
            var tomorrow = DateTime.Today.AddDays(1);

            await SeedCitaAsync(context, funcionarioA.IdFuncionario, tomorrow.AddHours(10), servicio.Id);
            await SeedCitaAsync(context, funcionarioB.IdFuncionario, tomorrow.AddHours(11), servicio.Id);
            await SeedCitaAsync(context, funcionarioA.IdFuncionario, tomorrow.AddHours(12), null, tipo: "DESCANSO", duracionMinutos: 30);
            await SeedCitaAsync(context, funcionarioA.IdFuncionario, tomorrow.AddDays(1).AddHours(9), servicio.Id);

            var service = ControllerTestSupport.CreateCalendarQueryService(context);

            var allUpcoming = await service.GetUpcomingAppointmentsAsync(tomorrow, null);
            var filtered = await service.GetUpcomingAppointmentsAsync(tomorrow, funcionarioA.IdFuncionario);

            Assert.Equal(2, allUpcoming.Count);
            Assert.Single(filtered);
            Assert.All(allUpcoming, cita => Assert.Equal(tomorrow.Date, cita.FechaHoraCita.Date));
            Assert.Equal("Mariana", filtered[0].FuncionarioNombre);
        }

        [Fact]
        public async Task GetUpcomingAppointmentsAsync_ShouldExcludePastAppointmentsOnlyWhenSelectedDateIsToday()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Sofia");
            var servicio = await SeedServicioAsync(context, "Corte", 30);
            var businessToday = ControllerTestSupport.BusinessDateTimeProvider.Today();

            var yesterdayMorning = await SeedCitaAsync(context, funcionario.IdFuncionario, businessToday.AddDays(-1).AddHours(8), servicio.Id);
            await SeedCitaAsync(context, funcionario.IdFuncionario, businessToday.AddHours(8), servicio.Id);
            var futureToday = await SeedCitaAsync(context, funcionario.IdFuncionario, businessToday.AddHours(12), servicio.Id);
            var tomorrowMorning = await SeedCitaAsync(context, funcionario.IdFuncionario, businessToday.AddDays(1).AddHours(8), servicio.Id);

            var service = ControllerTestSupport.CreateCalendarQueryService(context);

            var todayAppointments = await service.GetUpcomingAppointmentsAsync(businessToday, null);
            var yesterdayAppointments = await service.GetUpcomingAppointmentsAsync(businessToday.AddDays(-1), null);
            var tomorrowAppointments = await service.GetUpcomingAppointmentsAsync(businessToday.AddDays(1), null);

            Assert.Equal(futureToday.Id, Assert.Single(todayAppointments).Id);
            Assert.Equal(yesterdayMorning.Id, Assert.Single(yesterdayAppointments).Id);
            Assert.Equal(tomorrowMorning.Id, Assert.Single(tomorrowAppointments).Id);
        }

        [Fact]
        public async Task FuncionarioExistsForCurrentTenantAsync_ShouldRespectTenantIsolation()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var ownFuncionario = await SeedFuncionarioAsync(context, "Propio");

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();
            var foreignFuncionario = await SeedFuncionarioAsync(context, "Ajeno");

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateCalendarQueryService(context);

            Assert.True(await service.FuncionarioExistsForCurrentTenantAsync(ownFuncionario.IdFuncionario));
            Assert.False(await service.FuncionarioExistsForCurrentTenantAsync(foreignFuncionario.IdFuncionario));
            Assert.False(await service.FuncionarioExistsForCurrentTenantAsync(0));
        }

        [Fact]
        public async Task GetFechasOcupadasAsync_ShouldRespectRequestedRange()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Rosa");
            var servicio = await SeedServicioAsync(context, "Color", 60);

            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 10, 9, 0, 0), servicio.Id);
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 20, 9, 0, 0), servicio.Id);
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 5, 5, 9, 0, 0), servicio.Id);

            var service = ControllerTestSupport.CreateCalendarQueryService(context);

            var fechas = await service.GetFechasOcupadasAsync(
                funcionario.IdFuncionario,
                new DateTime(2026, 4, 1),
                new DateTime(2026, 4, 30));

            Assert.Equal(2, fechas.Count);
            Assert.Equal("2026-04-10", fechas[0].Fecha);
            Assert.Equal("09:00", fechas[0].Hora);
            Assert.Equal("2026-04-20", fechas[1].Fecha);
        }

        [Fact]
        public async Task GetByIdAsync_ShouldRespectTenantIsolation()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var foreignFuncionario = await SeedFuncionarioAsync(context, "Privado");
            var foreignServicio = await SeedServicioAsync(context, "Servicio Privado", 30);
            var foreignCita = await SeedCitaAsync(context, foreignFuncionario.IdFuncionario, new DateTime(2026, 4, 24, 9, 0, 0), foreignServicio.Id);

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateCalendarQueryService(context);

            var cita = await service.GetByIdAsync(foreignCita.Id);

            Assert.Null(cita);
        }

        [Fact]
        public async Task GetByIdAsync_ShouldExposeConsentAndStatusDisplay_ForManualAppointmentWithoutConsent()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Sofia");
            var servicio = await SeedServicioAsync(context, "Corte", 30);
            var cita = await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 24, 15, 0, 0), servicio.Id);

            cita.WhatsAppConsentAtCreation = false;
            cita.WhatsAppConsentSource = "SinConsentimiento";
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateCalendarQueryService(context);

            var detalle = await service.GetByIdAsync(cita.Id);

            Assert.NotNull(detalle);
            Assert.Equal("WhatsApp no autorizado para esta cita. No se enviarán confirmaciones ni recordatorios.", detalle!.WhatsAppConsentDisplay);
            Assert.Equal("WhatsApp: no enviado. Motivo: cliente sin autorización.", detalle.WhatsAppStatusDisplay);
        }

        [Fact]
        public async Task GetServiciosActivosAsync_ShouldReturnActiveServicesOrdered()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedServicioAsync(context, "Zeta", 30);
            await SeedServicioAsync(context, "Alfa", 45);
            await SeedServicioAsync(context, "Oculto", 60, activo: false);

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();
            await SeedServicioAsync(context, "Ajeno", 30);

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateCalendarQueryService(context);

            var servicios = await service.GetServiciosActivosAsync();

            Assert.Equal(2, servicios.Count);
            Assert.Equal(new[] { "Alfa", "Zeta" }, servicios.Select(s => s.Nombre).ToArray());
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre)
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
                ColorCalendario = "#654321",
                PorcentajeGanancia = 35m,
                PorcentajeProducto = 8m,
                FechaIngreso = new DateTime(2026, 4, 1),
                Activo = true
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
                Precio = 30m,
                DuracionMinutos = duracionMinutos,
                Activo = activo
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<Cita> SeedCitaAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            DateTime fechaHora,
            int? servicioId,
            string tipo = "CITA",
            int? duracionMinutos = null)
        {
            var cita = new Cita
            {
                NombreCliente = tipo == "DESCANSO" ? "DESCANSO" : $"Cliente {Guid.NewGuid():N}",
                TelefonoCliente = tipo == "DESCANSO" ? null : "70000000",
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
    }
}
