using System.Reflection;
using LuxuryApp.Controllers.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class CalendarControllerTests
    {
        [Theory]
        [InlineData(nameof(CalendarController.Create))]
        [InlineData(nameof(CalendarController.Edit))]
        [InlineData(nameof(CalendarController.Move))]
        [InlineData(nameof(CalendarController.Delete))]
        [InlineData(nameof(CalendarController.ProcesarVisitas))]
        public void MutatingActions_ShouldRequireValidateAntiForgeryToken(string actionName)
        {
            var method = typeof(CalendarController)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(methodInfo => methodInfo.Name == actionName);

            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true));
        }

        [Fact]
        public async Task Create_ShouldReturnBadRequest_WhenFuncionarioIdIsInvalid()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId);

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("calendar-user", tenantId));

            var result = await controller.Create(new CitaCreateVM
            {
                Tipo = "DESCANSO",
                FechaHoraCita = new DateTime(2026, 4, 24, 9, 0, 0),
                FuncionarioId = 0,
                DuracionMinutos = 30
            }, CancellationToken.None);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("funcionario", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GetCitasByDay_ShouldRejectInvalidDateFormat()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId);

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("calendar-user", tenantId));

            var result = await controller.GetCitasByDay("2026/04/24", CancellationToken.None);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("fecha", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Index_ShouldExposeTenantWhatsAppFlag(bool tenantWhatsAppEnabled)
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId, tenantWhatsAppEnabled);

            var result = await controller.Index(CancellationToken.None);

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal(tenantWhatsAppEnabled, (bool?)view.ViewData["TenantWhatsAppEnabled"] ?? !tenantWhatsAppEnabled);
        }

        [Fact]
        public async Task Create_ShouldReturnOk_WhenPayloadIsValid()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 45);

            var controller = CreateController(context, tenantId);

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("calendar-user", tenantId));

            var result = await controller.Create(new CitaCreateVM
            {
                NombreCliente = "Cliente",
                TelefonoCliente = "",
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 10, 15, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA",
                Duplicar = false,
                FechasDuplicadas = []
            }, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            Assert.Single(await context.Citas.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task Edit_ShouldReturnOk_WhenPayloadIsValid()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Luis");
            var servicio = await SeedServicioAsync(context, "Color", 60);

            var cita = new Cita
            {
                NombreCliente = "Inicial",
                TelefonoCliente = "8888",
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 9, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            };

            context.Citas.Add(cita);
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("calendar-user", tenantId));

            var result = await controller.Edit(cita.Id, new CitaCreateVM
            {
                NombreCliente = "Editado",
                TelefonoCliente = "",
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 11, 30, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            }, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);

            context.ChangeTracker.Clear();
            var persisted = await context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal("Editado", persisted.NombreCliente);
            Assert.Equal(new DateTime(2026, 4, 24, 11, 30, 0), persisted.FechaHoraCita);
        }

        [Fact]
        public async Task Move_ShouldReturnOk_WhenPayloadIsValid()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Mario");
            var servicio = await SeedServicioAsync(context, "Spa", 30);

            var cita = new Cita
            {
                NombreCliente = "Mover",
                TelefonoCliente = "8888",
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 8, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            };

            context.Citas.Add(cita);
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("calendar-user", tenantId));

            var result = await controller.Move(cita.Id, new MoveCitaVM
            {
                FechaHoraCita = new DateTime(2026, 4, 24, 10, 0, 0),
                FuncionarioId = funcionario.IdFuncionario
            }, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);

            context.ChangeTracker.Clear();
            var persisted = await context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal(new DateTime(2026, 4, 24, 10, 0, 0), persisted.FechaHoraCita);
        }

        [Fact]
        public async Task Delete_ShouldReturnOk_WhenCitaExists()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Rosa");
            var servicio = await SeedServicioAsync(context, "Lavado", 30);

            var cita = new Cita
            {
                NombreCliente = "Eliminar",
                TelefonoCliente = "8888",
                ServicioId = servicio.Id,
                FechaHoraCita = new DateTime(2026, 4, 24, 13, 0, 0),
                FuncionarioId = funcionario.IdFuncionario,
                Tipo = "CITA"
            };

            context.Citas.Add(cita);
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("calendar-user", tenantId));

            var result = await controller.Delete(cita.Id, motivo: null, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            Assert.Empty(await context.Citas.AsNoTracking().ToListAsync());
        }

        private static CalendarController CreateController(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            bool tenantWhatsAppEnabled = true,
            LuxuryApp.Services.Reservas.IBookingRequestService? bookingRequestService = null,
            Microsoft.AspNetCore.Authorization.IAuthorizationService? authorizationService = null)
        {
            var controller = new CalendarController(
                ControllerTestSupport.CreateCalendarCommandService(context),
                ControllerTestSupport.CreateCalendarQueryService(context),
                ControllerTestSupport.CreateControlCobrosQueryService(context),
                ControllerTestSupport.CreateCobroService(context),
                ControllerTestSupport.CreateComprobanteCobroService(),
                new FakeTenantWhatsAppFeatureService
                {
                    IsEnabled = tenantWhatsAppEnabled
                },
                ControllerTestSupport.BusinessDateTimeProvider,
                ControllerTestSupport.CreateCobroFiscalPreviewService(
                    context, new TestTenantProvider { TenantId = tenantId }),
                ControllerTestSupport.CreateAvailabilityService(context),
                bookingRequestService ?? new SpyBookingRequestService(),
                authorizationService ?? new TestAuthorizationService(),
                new NoOpAppointmentCancellationWhatsAppService());

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("calendar-user", tenantId));

            return controller;
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
                ColorCalendario = "#123456",
                PorcentajeGanancia = 40m,
                PorcentajeProducto = 10m,
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
            int duracionMinutos)
        {
            var servicio = new Servicio
            {
                Nombre = nombre,
                Precio = 25m,
                DuracionMinutos = duracionMinutos,
                Activo = true
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }
    }
}
