using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Notifications;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Panel privado "Solicitudes de reserva" (read-side): rangos en hora local del negocio,
    /// conteos del rango, orden por fecha de recepción y aislamiento multi-tenant.
    ///
    /// <para>
    /// El reloj de referencia es JUEVES 2026-08-27, 14:00 hora del negocio (offset -06:00).
    /// Las solicitudes se guardan en UTC: eso es justamente lo que estas pruebas defienden.
    /// </para>
    /// </summary>
    public class BookingRequestsPanelTests
    {
        private static readonly DateTimeOffset JuevesLocal =
            new(new DateTime(2026, 8, 27, 14, 0, 0), TimeSpan.FromHours(-6));

        private static IBusinessDateTimeProvider Reloj =>
            new FixedBusinessDateTimeProvider(JuevesLocal.DateTime);

        // ── 1. Límites del rango: se resuelven en LOCAL y se consultan en UTC ──────────────

        [Fact]
        public void Resolve_Today_UsesLocalMidnightToNextLocalMidnight()
        {
            var rango = BookingRequestDateRangeResolver.Resolve(BookingRequestDateRange.Today, JuevesLocal);

            // 00:00 local del 27 = 06:00 UTC del 27; fin exclusivo = 00:00 local del 28.
            Assert.Equal(new DateTime(2026, 8, 27, 6, 0, 0, DateTimeKind.Utc), rango.StartUtc);
            Assert.Equal(new DateTime(2026, 8, 28, 6, 0, 0, DateTimeKind.Utc), rango.EndUtc);
        }

        [Fact]
        public void Resolve_ThisWeek_StartsOnMondayAndEndsOnNextMonday()
        {
            var rango = BookingRequestDateRangeResolver.Resolve(BookingRequestDateRange.ThisWeek, JuevesLocal);

            // Jueves 27 ⇒ lunes 24 00:00 local … lunes 31 00:00 local (exclusivo).
            Assert.Equal(new DateTime(2026, 8, 24, 6, 0, 0, DateTimeKind.Utc), rango.StartUtc);
            Assert.Equal(new DateTime(2026, 8, 31, 6, 0, 0, DateTimeKind.Utc), rango.EndUtc);
        }

        [Fact]
        public void Resolve_ThisMonth_StartsOnFirstDayAndEndsOnFirstDayOfNextMonth()
        {
            var rango = BookingRequestDateRangeResolver.Resolve(BookingRequestDateRange.ThisMonth, JuevesLocal);

            Assert.Equal(new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc), rango.StartUtc);
            Assert.Equal(new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc), rango.EndUtc);
        }

        [Fact]
        public void Resolve_WeekStartingOnMonday_DoesNotShiftWhenTodayIsMonday()
        {
            var lunes = new DateTimeOffset(new DateTime(2026, 8, 24, 9, 0, 0), TimeSpan.FromHours(-6));

            var rango = BookingRequestDateRangeResolver.Resolve(BookingRequestDateRange.ThisWeek, lunes);

            Assert.Equal(new DateTime(2026, 8, 24, 6, 0, 0, DateTimeKind.Utc), rango.StartUtc);
            Assert.Equal(new DateTime(2026, 8, 31, 6, 0, 0, DateTimeKind.Utc), rango.EndUtc);
        }

        // ── 2. Allowlist de filtros: lo desconocido cae al default seguro ──────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("anio")]
        [InlineData("'; DROP TABLE BookingRequests--")]
        public void ParseRange_MissingOrInvalidValue_FallsBackToThisWeek(string? valor)
        {
            Assert.Equal(BookingRequestDateRange.ThisWeek, BookingRequestFilters.ParseRange(valor));
        }

        [Theory]
        [InlineData("hoy", BookingRequestDateRange.Today)]
        [InlineData("semana", BookingRequestDateRange.ThisWeek)]
        [InlineData("mes", BookingRequestDateRange.ThisMonth)]
        [InlineData("MES", BookingRequestDateRange.ThisMonth)]
        public void ParseRange_KnownTokens_AreHonored(string valor, BookingRequestDateRange esperado)
        {
            Assert.Equal(esperado, BookingRequestFilters.ParseRange(valor));
        }

        [Theory]
        [InlineData(null, BookingRequestStatusFilter.Pending)]
        [InlineData("hacker", BookingRequestStatusFilter.Pending)]
        [InlineData("Confirmed", BookingRequestStatusFilter.Confirmed)]
        [InlineData("rejected", BookingRequestStatusFilter.Rejected)]
        [InlineData("all", BookingRequestStatusFilter.All)]
        public void ParseStatus_UsesAllowlistWithSafeFallback(string? valor, BookingRequestStatusFilter esperado)
        {
            Assert.Equal(esperado, BookingRequestFilters.ParseStatus(valor));
        }

        // ── 3. Default de la pantalla ─────────────────────────────────────────────────────

        [Fact]
        public async Task BuildPage_WithoutQueryString_DefaultsToThisWeek()
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var pagina = await fixture.Service.BuildPageAsync(estado: null, rango: null);

            Assert.Equal(BookingRequestDateRange.ThisWeek, pagina.RangoFiltro);
            Assert.Equal("semana", pagina.RangoFiltroValor);
            Assert.True(pagina.EsRangoActivo(BookingRequestDateRange.ThisWeek));
        }

        [Fact]
        public async Task BuildPage_WithTamperedRange_FallsBackToThisWeekDataset()
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var manipulada = await fixture.Service.BuildPageAsync("all", "trimestre");
            var semana = await fixture.Service.BuildPageAsync("all", "semana");

            Assert.Equal(BookingRequestDateRange.ThisWeek, manipulada.RangoFiltro);
            Assert.Equal(semana.TotalCount, manipulada.TotalCount);
        }

        // ── 4. Conteos: pendientes = backlog completo; resueltas = rango ──────────────────

        [Theory]
        [InlineData("hoy", 3, 0, 1, 4)]
        [InlineData("semana", 3, 1, 1, 5)]
        [InlineData("mes", 3, 2, 1, 6)]
        public async Task BuildPage_CountsBelongToSelectedRange(
            string rango,
            int pendientes,
            int confirmadas,
            int rechazadas,
            int total)
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var pagina = await fixture.Service.BuildPageAsync("Pending", rango);

            Assert.Equal(pendientes, pagina.PendientesCount);
            Assert.Equal(confirmadas, pagina.ConfirmadasCount);
            Assert.Equal(rechazadas, pagina.RechazadasCount);
            Assert.Equal(total, pagina.TotalCount);
        }

        [Fact]
        public async Task BuildPage_StatusTabFiltersCardsButNeverTheCounts()
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var pendientes = await fixture.Service.BuildPageAsync("Pending", "mes");
            var confirmadas = await fixture.Service.BuildPageAsync("Confirmed", "mes");
            var todas = await fixture.Service.BuildPageAsync("all", "mes");

            // Las tarjetas cambian…
            Assert.Equal(3, pendientes.Solicitudes.Count);
            Assert.Equal(2, confirmadas.Solicitudes.Count);
            Assert.Equal(6, todas.Solicitudes.Count);
            Assert.All(confirmadas.Solicitudes, s => Assert.Equal(BookingRequestStates.Confirmed, s.Estado));

            // …los contadores, no.
            foreach (var pagina in new[] { pendientes, confirmadas, todas })
            {
                Assert.Equal(3, pagina.PendientesCount);
                Assert.Equal(2, pagina.ConfirmadasCount);
                Assert.Equal(1, pagina.RechazadasCount);
                Assert.Equal(6, pagina.TotalCount);
            }

            // "Todas" muestra el total del rango.
            Assert.Equal(todas.TotalCount, todas.Solicitudes.Count);
        }

        // ── 5. Corte del día en hora LOCAL, no en UTC ─────────────────────────────────────

        [Fact]
        public async Task BuildPage_Today_IncludesLocalMidnightAndExcludesTomorrow()
        {
            var fixture = await CrearEscenarioAsync(seedBase: false);
            using var _ = fixture;

            // 00:00 local de hoy y 00:00 local de mañana (frontera exacta). Se usan estados ya
            // resueltos porque son los únicos que el período limita.
            await SeedSolicitudAsync(fixture, "Arranca hoy", BookingRequestStates.Confirmed, new DateTime(2026, 8, 27, 6, 0, 0));
            await SeedSolicitudAsync(fixture, "Arranca mañana", BookingRequestStates.Confirmed, new DateTime(2026, 8, 28, 6, 0, 0));

            var pagina = await fixture.Service.BuildPageAsync("Confirmed", "hoy");

            var solicitud = Assert.Single(pagina.Solicitudes);
            Assert.Equal("Arranca hoy", solicitud.NombreCliente);
            Assert.Equal(1, pagina.TotalCount);
        }

        // ── 6. "Recibida" en hora local del negocio ───────────────────────────────────────

        [Fact]
        public async Task BuildPage_ReceivedAt_IsConvertedToBusinessLocalTime()
        {
            var fixture = await CrearEscenarioAsync(seedBase: false);
            using var _ = fixture;

            // Regresión: 16:18 UTC debe leerse "26 ago, 10:18" en Costa Rica.
            await SeedSolicitudAsync(fixture, "Ana", BookingRequestStates.Pending, new DateTime(2026, 8, 26, 16, 18, 0));

            var pagina = await fixture.Service.BuildPageAsync("Pending", "semana");

            var solicitud = Assert.Single(pagina.Solicitudes);
            Assert.Equal(new DateTime(2026, 8, 26, 16, 18, 0), solicitud.CreatedAtUtc); // se sigue guardando en UTC
            Assert.Equal(new DateTime(2026, 8, 26, 10, 18, 0), solicitud.RecibidaLocal);
        }

        // ── 7. Orden: la más reciente primero ─────────────────────────────────────────────

        [Fact]
        public async Task BuildPage_OrdersByReceivedDateDescending()
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var pagina = await fixture.Service.BuildPageAsync("all", "mes");

            var fechas = pagina.Solicitudes.Select(s => s.CreatedAtUtc).ToArray();
            Assert.Equal(fechas.OrderByDescending(f => f).ToArray(), fechas);
            Assert.Equal("Rechazada hoy", pagina.Solicitudes[0].NombreCliente);
            Assert.Equal("Vieja del mes", pagina.Solicitudes[^1].NombreCliente);
        }

        [Fact]
        public async Task BuildPage_SameTimestamp_BreaksTieByIdDescending()
        {
            var fixture = await CrearEscenarioAsync(seedBase: false);
            using var _ = fixture;

            var mismoInstante = new DateTime(2026, 8, 26, 16, 18, 0);
            var primera = await SeedSolicitudAsync(fixture, "Primera", BookingRequestStates.Pending, mismoInstante);
            var segunda = await SeedSolicitudAsync(fixture, "Segunda", BookingRequestStates.Pending, mismoInstante);

            var pagina = await fixture.Service.BuildPageAsync("Pending", "semana");

            Assert.Equal(new[] { segunda, primera }, pagina.Solicitudes.Select(s => s.Id).ToArray());
        }

        // ── 8. Aislamiento multi-tenant ───────────────────────────────────────────────────

        [Fact]
        public async Task BuildPage_NeverLeaksRequestsFromAnotherTenant()
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var otroTenant = Guid.NewGuid();
            await EnsureTenantAsync(fixture.Context, otroTenant);
            fixture.TenantProvider.TenantId = otroTenant;
            fixture.Context.ChangeTracker.Clear();

            var ajeno = await fixture.Service.BuildPageAsync("all", "mes");

            Assert.Empty(ajeno.Solicitudes);
            Assert.Equal(0, ajeno.TotalCount);
            Assert.Equal(0, ajeno.PendientesCount);
            Assert.Equal(0, ajeno.ConfirmadasCount);
            Assert.Equal(0, ajeno.RechazadasCount);

            // El dueño de los datos las sigue viendo.
            fixture.TenantProvider.TenantId = fixture.TenantId;
            fixture.Context.ChangeTracker.Clear();
            var propio = await fixture.Service.BuildPageAsync("all", "mes");
            Assert.Equal(6, propio.TotalCount);
        }

        // ── 9. La vista refleja el estado activo sin lógica propia ────────────────────────

        [Fact]
        public async Task BuildPage_ExposesActiveFiltersForTheView()
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var pagina = await fixture.Service.BuildPageAsync("Confirmed", "hoy");

            Assert.True(pagina.EsEstadoActivo(BookingRequestStatusFilter.Confirmed));
            Assert.False(pagina.EsEstadoActivo(BookingRequestStatusFilter.Pending));
            Assert.True(pagina.EsRangoActivo(BookingRequestDateRange.Today));
            Assert.Equal("Confirmed", pagina.EstadoFiltroValor);
            Assert.Equal("hoy", pagina.RangoFiltroValor);
            Assert.Equal(pagina.TotalCount, pagina.ConteoDe(BookingRequestStatusFilter.All));
            Assert.Equal(pagina.PendientesCount, pagina.ConteoDe(BookingRequestStatusFilter.Pending));
        }

        [Fact]
        public void SolicitudesView_RendersLocalTimeAndSaaSShell()
        {
            var lista = File.ReadAllText(TestProjectPaths.ProjectPath("Views", "Reservas", "_SolicitudesList.cshtml"));
            var index = File.ReadAllText(TestProjectPaths.ProjectPath("Views", "Reservas", "Index.cshtml"));

            // "Recibida" sale del ViewModel ya convertido: nada de ToLocalTime() (depende del
            // huso del servidor, que en producción es UTC).
            Assert.Contains("RecibidaLocal", lista);
            Assert.DoesNotContain("ToLocalTime()", lista);

            // Contenedor y controles del design system del SaaS (mismo lenguaje que Ingresos).
            Assert.Contains("finance-page-shell", index);
            Assert.Contains("finance-hero", index);
            Assert.Contains("finance-filter-select", lista);
            Assert.Contains("<button type=\"button\"", lista);
            Assert.Contains("aria-pressed", lista);

            // Los contadores se renderizan dentro del bloque recargable, junto a las tarjetas:
            // así nunca quedan desfasados del rango.
            Assert.Contains("ConteoDe(", lista);
            Assert.Contains("reservasPanel", index);

            // El período no aplica a las pendientes: el selector se deshabilita y se explica.
            Assert.Contains("RangoAplicaAlListado", lista);
            Assert.Contains("sin importar el período", lista);
        }

        // ── 10. Backlog: una PENDIENTE no depende de ninguna fecha ───────────────────────

        /// <summary>
        /// Caso reportado: solicitud recibida el DOMINGO; el lunes arranca otra semana. Con el
        /// filtro por fecha de recepción desaparecía del panel aunque seguía sin resolverse.
        /// </summary>
        [Theory]
        [InlineData("hoy")]
        [InlineData("semana")]
        [InlineData("mes")]
        public async Task BuildPage_PendingReceivedLastWeek_StillAppearsAfterTheWeekRolls(string rango)
        {
            // Lunes 31/08/2026 09:00 local: arrancó una semana nueva.
            var lunes = new FixedBusinessDateTimeProvider(new DateTime(2026, 8, 31, 9, 0, 0));
            var fixture = await CrearEscenarioAsync(seedBase: false, reloj: lunes);
            using var _ = fixture;

            // Domingo 30/08 20:00 local = 31/08 02:00 UTC (semana anterior en hora del negocio).
            await SeedSolicitudAsync(fixture, "Domingo", BookingRequestStates.Pending, new DateTime(2026, 8, 31, 2, 0, 0));

            var pagina = await fixture.Service.BuildPageAsync("Pending", rango);

            Assert.Equal("Domingo", Assert.Single(pagina.Solicitudes).NombreCliente);
            Assert.Equal(1, pagina.PendientesCount);
        }

        [Fact]
        public async Task BuildPage_VeryOldPending_KeepsShowingWhileItStaysPending()
        {
            var fixture = await CrearEscenarioAsync(seedBase: false);
            using var _ = fixture;

            // Recibida hace meses y con la fecha de la cita ya pasada: sigue siendo trabajo abierto.
            await SeedSolicitudAsync(
                fixture,
                "Antigua",
                BookingRequestStates.Pending,
                new DateTime(2026, 3, 2, 15, 0, 0),
                fechaCitaLocal: new DateTime(2026, 3, 3, 10, 0, 0));

            var pagina = await fixture.Service.BuildPageAsync("Pending", "hoy");

            Assert.Single(pagina.Solicitudes);
            Assert.Equal(1, pagina.PendientesCount);
        }

        [Fact]
        public async Task BuildPage_OpeningTheScreenTwice_DoesNotHideAnything()
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var primera = await fixture.Service.BuildPageAsync("Pending", "semana");
            var segunda = await fixture.Service.BuildPageAsync("Pending", "semana");
            var tercera = await fixture.Service.BuildPageAsync("Pending", "hoy");

            Assert.Equal(3, primera.PendientesCount);
            Assert.Equal(primera.PendientesCount, segunda.PendientesCount);
            Assert.Equal(primera.PendientesCount, tercera.PendientesCount);
            Assert.Equal(
                primera.Solicitudes.Select(x => x.Id).ToArray(),
                tercera.Solicitudes.Select(x => x.Id).ToArray());
        }

        [Fact]
        public async Task BuildPage_PendingCountMatchesTheListedPendings()
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var pagina = await fixture.Service.BuildPageAsync("Pending", "hoy");
            var backlogReal = await fixture.Context.BookingRequests
                .CountAsync(r => r.Estado == BookingRequestStates.Pending);

            Assert.Equal(backlogReal, pagina.PendientesCount);
            Assert.Equal(backlogReal, pagina.Solicitudes.Count);
        }

        [Fact]
        public async Task BuildPage_AllTab_AlwaysIncludesPendingsPlusResolvedOfThePeriod()
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var todas = await fixture.Service.BuildPageAsync("all", "hoy");

            // 3 pendientes (backlog) + 1 rechazada de hoy.
            Assert.Equal(4, todas.Solicitudes.Count);
            Assert.Equal(todas.TotalCount, todas.Solicitudes.Count);
            Assert.Equal(3, todas.Solicitudes.Count(x => x.Estado == BookingRequestStates.Pending));
        }

        // ── 11. Solo confirmar o rechazar la sacan del backlog ───────────────────────────

        [Fact]
        public async Task Confirm_RemovesTheRequestFromPendings()
        {
            var fixture = await CrearEscenarioAsync(seedBase: false);
            using var _ = fixture;

            var id = await SeedSolicitudAsync(fixture, "Ana", BookingRequestStates.Pending, new DateTime(2026, 3, 2, 15, 0, 0));
            Assert.Equal(1, (await fixture.Service.BuildPageAsync("Pending", "semana")).PendientesCount);

            var resultado = await fixture.Service.ConfirmAsync(id, null, "admin");

            Assert.True(resultado.Success);
            var pagina = await fixture.Service.BuildPageAsync("Pending", "semana");
            Assert.Empty(pagina.Solicitudes);
            Assert.Equal(0, pagina.PendientesCount);
        }

        [Fact]
        public async Task Reject_RemovesTheRequestFromPendings()
        {
            var fixture = await CrearEscenarioAsync(seedBase: false);
            using var _ = fixture;

            var id = await SeedSolicitudAsync(fixture, "Ana", BookingRequestStates.Pending, new DateTime(2026, 3, 2, 15, 0, 0));

            var resultado = await fixture.Service.RejectAsync(id, "No hay espacio", "admin");

            Assert.True(resultado.Success);
            var pagina = await fixture.Service.BuildPageAsync("Pending", "semana");
            Assert.Empty(pagina.Solicitudes);
            Assert.Equal(0, pagina.PendientesCount);
        }

        [Fact]
        public async Task BuildPage_PendingBacklog_IsStillTenantScoped()
        {
            var fixture = await CrearEscenarioAsync();
            using var _ = fixture;

            var otroTenant = Guid.NewGuid();
            await EnsureTenantAsync(fixture.Context, otroTenant);
            fixture.TenantProvider.TenantId = otroTenant;
            fixture.Context.ChangeTracker.Clear();

            var ajeno = await fixture.Service.BuildPageAsync("Pending", "mes");

            Assert.Empty(ajeno.Solicitudes);
            Assert.Equal(0, ajeno.PendientesCount);
        }

        // ── Leer la notificación es independiente del estado de negocio ──────────────────

        [Fact]
        public async Task BuildPage_ReadingTheNotification_DoesNotAffectThePendingRequest()
        {
            var fixture = await CrearEscenarioAsync(seedBase: false);
            using var _ = fixture;

            var id = await SeedSolicitudAsync(fixture, "Ana", BookingRequestStates.Pending, new DateTime(2026, 8, 2, 15, 0, 0));
            var solicitud = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync(r => r.Id == id);

            var notificaciones = new NotificationService(
                fixture.Context,
                Reloj,
                NullLogger<NotificationService>.Instance);

            await notificaciones.CreateBookingRequestReceivedAsync(solicitud);
            var leidas = await notificaciones.MarkAllAsReadAsync();
            fixture.Context.ChangeTracker.Clear();

            Assert.Equal(1, leidas);

            var pagina = await fixture.Service.BuildPageAsync("Pending", "hoy");
            Assert.Single(pagina.Solicitudes);
            Assert.Equal(1, pagina.PendientesCount);
            Assert.Equal(
                BookingRequestStates.Pending,
                await fixture.Context.BookingRequests.Where(r => r.Id == id).Select(r => r.Estado).SingleAsync());
        }

        /// <summary>
        /// La visibilidad de una solicitud NO puede depender de un estado de "vista/leída": la
        /// entidad no tiene ese concepto y no debe adquirirlo por la puerta de atrás.
        /// </summary>
        [Fact]
        public void BookingRequest_HasNoSeenOrReadFlag()
        {
            var propiedades = typeof(BookingRequest)
                .GetProperties()
                .Select(p => p.Name)
                .ToArray();

            Assert.DoesNotContain(propiedades, n =>
                n.Contains("Read", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Seen", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Viewed", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Visto", StringComparison.OrdinalIgnoreCase));
        }

        // ── 12. Reservas y Calendario no son excluyentes ─────────────────────────────────

        [Fact]
        public async Task PendingRequest_AppearsInBothTheBacklogAndTheCalendar()
        {
            var fixture = await CrearEscenarioAsync(seedBase: false);
            using var _ = fixture;

            var fechaCita = new DateTime(2026, 9, 4, 10, 0, 0);
            await SeedSolicitudAsync(
                fixture,
                "Ana",
                BookingRequestStates.Pending,
                new DateTime(2026, 8, 2, 15, 0, 0), // recibida hace semanas
                fechaCitaLocal: fechaCita,
                conFuncionarioAsignado: true);

            var panel = await fixture.Service.BuildPageAsync("Pending", "hoy");
            var calendario = await fixture.Service.GetPendingForCalendarAsync(DateOnly.FromDateTime(fechaCita));

            Assert.Equal("Ana", Assert.Single(panel.Solicitudes).NombreCliente);
            Assert.Equal("Ana", Assert.Single(calendario).NombreCliente);
        }

        // ── Infraestructura ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Escenario base (reloj = jueves 27/08/2026 14:00 local): 3 pendientes (backlog completo,
        /// siempre visibles), 1 rechazada hoy, 1 confirmada esta semana, 1 confirmada del mes y
        /// 1 confirmada del mes anterior (fuera de todos los rangos).
        /// </summary>
        private static async Task<PanelFixture> CrearEscenarioAsync(
            bool seedBase = true,
            IBusinessDateTimeProvider? reloj = null)
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

            await EnsureTenantAsync(context, tenantProvider.TenantId);

            var servicio = new Servicio { Nombre = "Corte", Precio = 8000m, DuracionMinutos = 60, Activo = true };
            context.Servicios.Add(servicio);

            var puesto = new Puesto { NombrePuesto = "Estilista", Detalle = "R", Activo = true };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = "Jamie",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#123456",
                PorcentajeGanancia = 40m,
                PorcentajeProducto = 10m,
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();

            var fixture = new PanelFixture(
                context,
                connection,
                tenantProvider,
                servicio.Id,
                funcionario.IdFuncionario,
                reloj ?? Reloj);

            if (seedBase)
            {
                // Local 26/08 23:00 → hoy NO, esta semana SÍ (prueba que el corte es local).
                await SeedSolicitudAsync(fixture, "Ayer casi medianoche", BookingRequestStates.Pending, new DateTime(2026, 8, 27, 5, 0, 0));
                await SeedSolicitudAsync(fixture, "Pendiente hoy", BookingRequestStates.Pending, new DateTime(2026, 8, 27, 16, 18, 0));
                await SeedSolicitudAsync(fixture, "Rechazada hoy", BookingRequestStates.Rejected, new DateTime(2026, 8, 27, 18, 0, 0));
                await SeedSolicitudAsync(fixture, "Confirmada esta semana", BookingRequestStates.Confirmed, new DateTime(2026, 8, 25, 12, 0, 0));
                await SeedSolicitudAsync(fixture, "Confirmada del mes", BookingRequestStates.Confirmed, new DateTime(2026, 8, 11, 12, 0, 0));
                await SeedSolicitudAsync(fixture, "Vieja del mes", BookingRequestStates.Pending, new DateTime(2026, 8, 10, 12, 0, 0));
                await SeedSolicitudAsync(fixture, "Mes anterior", BookingRequestStates.Confirmed, new DateTime(2026, 7, 15, 12, 0, 0));
            }

            return fixture;
        }

        private static async Task<int> SeedSolicitudAsync(
            PanelFixture fixture,
            string nombre,
            string estado,
            DateTime createdAtUtc,
            DateTime? fechaCitaLocal = null,
            bool conFuncionarioAsignado = false)
        {
            var inicio = fechaCitaLocal ?? new DateTime(2026, 8, 28, 10, 0, 0);

            var solicitud = new BookingRequest
            {
                ServicioId = fixture.ServicioId,
                FuncionarioAsignadoId = conFuncionarioAsignado ? fixture.FuncionarioId : null,
                NombreCliente = nombre,
                TelefonoCliente = "88880000",
                FechaHoraInicioSolicitada = inicio,
                FechaHoraFinCalculada = inicio.AddHours(1),
                DuracionMinutos = 60,
                Estado = estado,
                Origen = BookingRequestOrigins.PublicLink,
                CreatedAtUtc = createdAtUtc
            };

            fixture.Context.BookingRequests.Add(solicitud);
            await fixture.Context.SaveChangesAsync();
            return solicitud.Id;
        }

        private static async Task EnsureTenantAsync(ApplicationDbContext context, Guid tenantId)
        {
            if (!await context.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
            {
                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Test", Activo = true });
                await context.SaveChangesAsync();
            }
        }

        private sealed class PanelFixture : IDisposable
        {
            public PanelFixture(
                ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider,
                int servicioId,
                int funcionarioId,
                IBusinessDateTimeProvider reloj)
            {
                Context = context;
                Connection = connection;
                TenantProvider = tenantProvider;
                TenantId = tenantProvider.TenantId;
                ServicioId = servicioId;
                FuncionarioId = funcionarioId;

                Service = new BookingRequestService(
                    context,
                    new InsertingCalendarCommandService(context),
                    new NoOpCalendarWhatsAppNotificationService(),
                    new AlwaysAvailableAvailabilityService(funcionarioId),
                    new StubBookingSettingsService(),
                    reloj,
                    new HttpContextAccessor(),
                    new FakeTenantWhatsAppFeatureService { IsEnabled = true },
                    new RecordingBookingRejectionWhatsAppService(),
                    ControllerTestSupport.CreateClienteIdentityService(context),
                    NullLogger<BookingRequestService>.Instance);
            }

            public ApplicationDbContext Context { get; }
            public Microsoft.Data.Sqlite.SqliteConnection Connection { get; }
            public TestTenantProvider TenantProvider { get; }
            public Guid TenantId { get; }
            public int ServicioId { get; }
            public int FuncionarioId { get; }
            public BookingRequestService Service { get; }

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }

        private sealed class AlwaysAvailableAvailabilityService : IBookingAvailabilityService
        {
            private readonly int _funcionarioId;

            public AlwaysAvailableAvailabilityService(int funcionarioId) => _funcionarioId = funcionarioId;

            public Task<SlotResolution> ResolveSlotAsync(int servicioId, DateTime inicio, int? funcionarioId, CancellationToken cancellationToken = default) =>
                Task.FromResult(new SlotResolution { Disponible = true, FuncionarioId = funcionarioId ?? _funcionarioId, DuracionMinutos = 60 });

            public Task<IReadOnlyList<string>> GetAvailableSlotsAsync(int servicioId, DateOnly fecha, int? funcionarioId, CancellationToken cancellationToken = default) =>
                Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

            public Task<IReadOnlyList<AvailableSlotSuggestion>> GetNextAvailableSlotsAsync(int servicioId, DateOnly fromDate, int? funcionarioId, int maxSuggestions = 5, CancellationToken cancellationToken = default) =>
                Task.FromResult((IReadOnlyList<AvailableSlotSuggestion>)Array.Empty<AvailableSlotSuggestion>());
        }

        /// <summary>Persiste una cita real para que la FK ConvertedCitaId sea valida, como en produccion.</summary>
        private sealed class InsertingCalendarCommandService : ICalendarCommandService
        {
            private readonly ApplicationDbContext _context;

            public InsertingCalendarCommandService(ApplicationDbContext context) => _context = context;

            public async Task<CalendarAppointmentResponse> CreateAsync(CalendarUpsertRequest request, CancellationToken cancellationToken = default)
            {
                var cita = new Cita
                {
                    FuncionarioId = request.FuncionarioId,
                    FechaHoraCita = request.FechaHoraCita,
                    Tipo = "CITA",
                    DuracionMinutos = 60,
                    NombreCliente = request.NombreCliente ?? "Cliente"
                };
                _context.Citas.Add(cita);
                await _context.SaveChangesAsync(cancellationToken);
                return new CalendarAppointmentResponse { Id = cita.Id };
            }

            public Task<CalendarAppointmentResponse> UpdateAsync(int id, CalendarUpsertRequest request, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();
            public Task MoveAsync(int id, CalendarMoveRequest request, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();
            public Task ResizeDurationAsync(int id, int duracionMinutos, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();
            public Task DeleteAsync(int id, string? motivoCancelacion = null, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();
            public Task ProcessVisitsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class StubBookingSettingsService : IBookingSettingsService
        {
            public Task<BookingSettingsViewModel> BuildSettingsViewModelAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new BookingSettingsViewModel());

            public Task SaveSettingsAsync(BookingSettingsViewModel input, string? userId, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<PublicBookingTenantContext?> ResolvePublicBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
                Task.FromResult<PublicBookingTenantContext?>(null);

            public Task<string?> GetCurrentSlugAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>("mi-negocio");
        }
    }
}
