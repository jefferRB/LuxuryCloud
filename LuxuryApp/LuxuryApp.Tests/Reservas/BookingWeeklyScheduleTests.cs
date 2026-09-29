using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Horarios;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Reservas
{
    /// <summary>
    /// Horario semanal de reservas online: jornada PROPIA por día, compatibilidad con la
    /// configuración plana anterior, idempotencia del guardado y aislamiento entre tenants.
    ///
    /// <para>
    /// Todo se comprueba a través de <see cref="IBookingAvailabilityService"/>, que consume la
    /// fuente única <c>IFuncionarioAvailabilityService</c>: si la jornada no llegara al motor real,
    /// estas pruebas fallarían aunque la tabla estuviera bien escrita.
    /// </para>
    /// </summary>
    public class BookingWeeklyScheduleTests
    {
        // 2026-05-25 es LUNES. La semana de referencia de todas las pruebas.
        private static readonly DateOnly Lunes = new(2026, 5, 25);
        private static readonly DateOnly Martes = new(2026, 5, 26);
        private static readonly DateOnly Domingo = new(2026, 5, 31);

        // Reloj fijo anterior a la jornada: la anticipación mínima nunca recorta los slots.
        private static FixedBusinessDateTimeProvider Reloj() =>
            new(new DateTime(2026, 5, 25, 6, 0, 0));

        // ─────────────────────────── Jornada por día ───────────────────────────

        [Fact]
        public async Task LunesAbiertoDeNueveASeis_OfreceHorariosDeEseDia()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));

            var horas = await escenario.HorasDe(Lunes);

            Assert.NotEmpty(horas);
            Assert.Equal("09:00", horas[0]);
            // Último bloque de 60 min que cabe antes de las 18:00.
            Assert.Equal("17:00", horas[^1]);
        }

        [Fact]
        public async Task DomingoCerrado_NoOfreceNingunHorario()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 30);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));

            Assert.Empty(await escenario.HorasDe(Domingo));
        }

        [Fact]
        public async Task MartesConHorarioDistinto_RespetaElHorarioDelMartes()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync(
                (DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)),
                (DayOfWeek.Tuesday, new TimeOnly(10, 0), new TimeOnly(19, 0)));

            var lunes = await escenario.HorasDe(Lunes);
            var martes = await escenario.HorasDe(Martes);

            Assert.Equal("09:00", lunes[0]);
            Assert.Equal("17:00", lunes[^1]);

            // El martes arranca y termina una hora más tarde: es SU jornada, no la del lunes.
            Assert.Equal("10:00", martes[0]);
            Assert.Equal("18:00", martes[^1]);
        }

        [Fact]
        public async Task ServicioQueNoCabeAntesDelCierre_NoSeOfrece()
        {
            // Servicio de 90 min en una jornada de 9:00 a 10:00: no cabe ningún bloque completo.
            using var escenario = await Escenario.CrearAsync(duracionServicio: 90);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(10, 0)));

            Assert.Empty(await escenario.HorasDe(Lunes));
        }

        [Fact]
        public async Task SlotQueTerminaExactamenteAlCierre_EsValido()
        {
            // Jornada 9:00–10:00 con servicio de 60 min: el bloque 9:00–10:00 termina justo al
            // cierre y SÍ vale (los intervalos son [inicio, fin)).
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(10, 0)));

            var hora = Assert.Single(await escenario.HorasDe(Lunes));
            Assert.Equal("09:00", hora);

            // Y el backend acepta ese mismo horario cuando llega por POST.
            var resolucion = await escenario.ResolverAsync(Lunes.ToDateTime(new TimeOnly(9, 0)));
            Assert.True(resolucion.Disponible);
        }

        [Fact]
        public async Task PostManipulado_FueraDeLaJornadaDeEseDia_EsRechazado()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync(
                (DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)),
                (DayOfWeek.Tuesday, new TimeOnly(14, 0), new TimeOnly(18, 0)));

            // 9:00 es válido el lunes, pero el martes el negocio abre a las 14:00.
            var martesTemprano = await escenario.ResolverAsync(Martes.ToDateTime(new TimeOnly(9, 0)));

            Assert.False(martesTemprano.Disponible);
            Assert.Equal("Ese horario está fuera de la jornada del negocio.", martesTemprano.Motivo);
        }

        [Fact]
        public async Task PostManipulado_EnUnDiaCerrado_EsRechazado()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 30);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));

            var domingo = await escenario.ResolverAsync(Domingo.ToDateTime(new TimeOnly(10, 0)));

            Assert.False(domingo.Disponible);
            Assert.Equal("Ese día no está disponible para reservas.", domingo.Motivo);
        }

        // ─────────────────────── Compatibilidad hacia atrás ───────────────────────

        [Fact]
        public async Task SinFilasPorDia_SeUsaLaConfiguracionPlanaAnterior()
        {
            // Tenant anterior a esta función: solo tiene WorkingDaysMask + OpenTime/CloseTime.
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.SeedSettingsPlanasAsync(
                maskLunesASabado: true,
                apertura: new TimeOnly(9, 0),
                cierre: new TimeOnly(12, 0));

            Assert.Empty(await escenario.Context.TenantBookingBusinessHours.ToListAsync());

            var lunes = await escenario.HorasDe(Lunes);
            Assert.Equal("09:00", lunes[0]);
            Assert.Equal("11:00", lunes[^1]);

            // El domingo quedaba fuera de la máscara heredada: sigue cerrado.
            Assert.Empty(await escenario.HorasDe(Domingo));
        }

        [Fact]
        public async Task GuardarLaPantalla_SincronizaLasColumnasAntiguasComoEnvolventeDeLaSemana()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 30);
            await escenario.GuardarHorarioAsync(
                (DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)),
                (DayOfWeek.Wednesday, new TimeOnly(8, 0), new TimeOnly(20, 0)));

            escenario.Context.ChangeTracker.Clear();
            var settings = await escenario.Context.TenantBookingSettings.SingleAsync();

            Assert.Equal(new TimeOnly(8, 0), settings.OpenTime);   // apertura más temprana
            Assert.Equal(new TimeOnly(20, 0), settings.CloseTime); // cierre más tardío
            Assert.True(settings.IsWorkingDay(DayOfWeek.Monday));
            Assert.True(settings.IsWorkingDay(DayOfWeek.Wednesday));
            Assert.False(settings.IsWorkingDay(DayOfWeek.Sunday));
        }

        // ──────────────────────────── Idempotencia ────────────────────────────

        [Fact]
        public async Task GuardarDosVeces_NoDuplicaHorarios()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 30);

            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));

            escenario.Context.ChangeTracker.Clear();
            var filas = await escenario.Context.TenantBookingBusinessHours.ToListAsync();

            // Siempre siete filas: una por día, nunca dos del mismo día.
            Assert.Equal(7, filas.Count);
            Assert.Equal(7, filas.Select(fila => fila.DiaSemana).Distinct().Count());
            Assert.Single(filas, fila => fila.IsEnabled);

            // Y una sola configuración principal por tenant.
            Assert.Equal(1, await escenario.Context.TenantBookingSettings.CountAsync());
        }

        [Fact]
        public async Task GuardarDosVecesConCambio_ActualizaLaFilaExistenteEnVezDeInsertarOtra()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 30);

            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(16, 0)));

            escenario.Context.ChangeTracker.Clear();
            var lunes = await escenario.Context.TenantBookingBusinessHours
                .SingleAsync(fila => fila.DiaSemana == DayOfWeek.Monday);

            Assert.Equal(new TimeOnly(10, 0), lunes.OpenTime);
            Assert.Equal(new TimeOnly(16, 0), lunes.CloseTime);
            Assert.Equal(7, await escenario.Context.TenantBookingBusinessHours.CountAsync());
        }

        // ──────────────────────────── Validación ────────────────────────────

        [Fact]
        public async Task CierreAnteriorALaApertura_EsRechazadoEnElServidor()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 30);

            var error = await Assert.ThrowsAsync<BookingValidationException>(() =>
                escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(18, 0), new TimeOnly(9, 0))));

            Assert.Contains("lunes", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await escenario.Context.TenantBookingBusinessHours.ToListAsync());
        }

        [Fact]
        public async Task ActivarReservasSinNingunDiaAbierto_EsRechazado()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 30);

            await Assert.ThrowsAsync<BookingValidationException>(() =>
                escenario.GuardarHorarioAsync(Array.Empty<(DayOfWeek, TimeOnly, TimeOnly)>()));
        }

        [Fact]
        public async Task DiaSemanaFueraDeRango_SeIgnoraSinRomperElGuardado()
        {
            // Un POST manipulado con DiaSemana = 9 no puede crear una octava jornada.
            using var escenario = await Escenario.CrearAsync(duracionServicio: 30);

            await escenario.GuardarCrudoAsync(new List<BookingDayScheduleViewModel>
            {
                new() { DiaSemana = 1, Abierto = true, Apertura = new TimeOnly(9, 0), Cierre = new TimeOnly(18, 0) },
                new() { DiaSemana = 9, Abierto = true, Apertura = new TimeOnly(9, 0), Cierre = new TimeOnly(18, 0) }
            });

            escenario.Context.ChangeTracker.Clear();
            var filas = await escenario.Context.TenantBookingBusinessHours.ToListAsync();

            Assert.Equal(7, filas.Count);
            Assert.All(filas, fila => Assert.InRange((int)fila.DiaSemana, 0, 6));
        }

        [Fact]
        public async Task DiaRepetidoEnElPost_SoloCuentaLaPrimeraAparicion()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 30);

            await escenario.GuardarCrudoAsync(new List<BookingDayScheduleViewModel>
            {
                new() { DiaSemana = 1, Abierto = true, Apertura = new TimeOnly(9, 0), Cierre = new TimeOnly(18, 0) },
                new() { DiaSemana = 1, Abierto = true, Apertura = new TimeOnly(6, 0), Cierre = new TimeOnly(23, 0) }
            });

            escenario.Context.ChangeTracker.Clear();
            var lunes = await escenario.Context.TenantBookingBusinessHours
                .SingleAsync(fila => fila.DiaSemana == DayOfWeek.Monday);

            Assert.Equal(new TimeOnly(9, 0), lunes.OpenTime);
            Assert.Equal(new TimeOnly(18, 0), lunes.CloseTime);
        }

        // ───────────────────── Bloqueos recurrentes + jornada ─────────────────────

        [Fact]
        public async Task BloqueoDeMediodia_EliminaSoloLosSlotsQueLoIntersectan()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(11, 0), new TimeOnly(15, 0)));
            await escenario.CrearBloqueoAsync("Almuerzo", new TimeOnly(12, 0), new TimeOnly(13, 0), RecurringScheduleRule.LunesASabadoMask);

            var horas = await escenario.HorasDe(Lunes);

            // 11:00–12:00 termina justo cuando arranca el bloqueo: NO hay conflicto.
            Assert.Contains("11:00", horas);
            // 13:00–14:00 arranca justo cuando termina: tampoco.
            Assert.Contains("13:00", horas);
            // 12:00 y 12:30 caen dentro del bloqueo.
            Assert.DoesNotContain("12:00", horas);
            Assert.DoesNotContain("12:30", horas);
            // 11:30–12:30 lo intersecta parcialmente.
            Assert.DoesNotContain("11:30", horas);
        }

        [Fact]
        public async Task BloqueoSoloDeLunes_NoAfectaAlMartes()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync(
                (DayOfWeek.Monday, new TimeOnly(11, 0), new TimeOnly(15, 0)),
                (DayOfWeek.Tuesday, new TimeOnly(11, 0), new TimeOnly(15, 0)));

            // Máscara con solo el lunes (bit 1).
            await escenario.CrearBloqueoAsync("Reunión", new TimeOnly(12, 0), new TimeOnly(13, 0), 1 << (int)DayOfWeek.Monday);

            Assert.DoesNotContain("12:00", await escenario.HorasDe(Lunes));
            Assert.Contains("12:00", await escenario.HorasDe(Martes));
        }

        // ───────────────────── Citas, descansos y solicitudes ─────────────────────

        [Theory]
        [InlineData("SERVICIO")]
        [InlineData("DESCANSO")]
        public async Task CitaODescansoExistente_BloqueaEseHorario(string tipo)
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(13, 0)));
            await escenario.SeedCitaAsync(Lunes.ToDateTime(new TimeOnly(10, 0)), 60, tipo);

            var horas = await escenario.HorasDe(Lunes);

            Assert.DoesNotContain("10:00", horas);
            Assert.Contains("09:00", horas);
            Assert.Contains("11:00", horas);
        }

        [Fact]
        public async Task SolicitudPendiente_BloqueaEseHorarioIgualQueUnaCita()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(13, 0)));
            await escenario.SeedSolicitudPendienteAsync(Lunes.ToDateTime(new TimeOnly(10, 0)), 60);

            var horas = await escenario.HorasDe(Lunes);

            Assert.DoesNotContain("10:00", horas);
            Assert.Contains("09:00", horas);
        }

        // ───────────── Servicios publicados y profesionales autorizados ─────────────

        /// <summary>
        /// Un servicio marcado como no visible sale del catálogo público, y con él de los horarios
        /// y de las sugerencias: no hay forma de que el enlace público lo ofrezca.
        ///
        /// <para>
        /// El portero de "publicado o no" es el catálogo (<c>IsServiceVisibleOnlineAsync</c>), que
        /// <c>PublicBookingService.SubmitAsync</c> consulta ANTES de reservar nada. Deliberadamente
        /// no vive dentro de <c>ResolveSlotAsync</c>: esa misma resolución la usa el admin al
        /// confirmar, y ocultar un servicio hoy no debe impedirle confirmar una solicitud legítima
        /// recibida cuando todavía estaba publicado.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ServicioOculto_DesapareceDelCatalogoPublicoYDeSusHorarios()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));

            var catalogo = new BookingCatalogService(escenario.Context);

            // Visible: aparece publicado y se ofrece.
            Assert.Contains(
                await catalogo.GetPublicServicesAsync(),
                servicio => servicio.Id == escenario.ServicioId);
            Assert.True(await catalogo.IsServiceVisibleOnlineAsync(escenario.ServicioId));
            Assert.NotEmpty(await escenario.HorasDe(Lunes));

            await escenario.OcultarServicioAsync();

            // Oculto: fuera del catálogo y rechazado por el portero que usa el flujo público.
            Assert.DoesNotContain(
                await catalogo.GetPublicServicesAsync(),
                servicio => servicio.Id == escenario.ServicioId);
            Assert.False(await catalogo.IsServiceVisibleOnlineAsync(escenario.ServicioId));
        }

        [Fact]
        public async Task ProfesionalNoAutorizadoParaElServicio_NoSePuedeReservarAunqueElPostLoPida()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));

            var otro = await escenario.SeedFuncionarioAsync("Sin permiso");
            // Solo el profesional original queda habilitado para este servicio.
            await escenario.AsignarProfesionalesAsync(escenario.FuncionarioId);

            var autorizado = await escenario.ResolverAsync(
                Lunes.ToDateTime(new TimeOnly(10, 0)), escenario.FuncionarioId);
            Assert.True(autorizado.Disponible);
            Assert.Equal(escenario.FuncionarioId, autorizado.FuncionarioId);

            var noAutorizado = await escenario.ResolverAsync(Lunes.ToDateTime(new TimeOnly(11, 0)), otro);
            Assert.False(noAutorizado.Disponible);

            // Y sus horarios tampoco se exponen.
            var horas = await AvailabilityDe(escenario, otro);
            Assert.Empty(horas);
        }

        private static Task<IReadOnlyList<string>> AvailabilityDe(Escenario escenario, int funcionarioId) =>
            ControllerTestSupport
                .CreateBookingAvailabilityService(escenario.Context, Reloj())
                .GetAvailableSlotsAsync(escenario.ServicioId, Lunes, funcionarioId);

        // ──────────────────────────── Multi-tenant ────────────────────────────

        [Fact]
        public async Task ElHorarioDeUnTenant_NiSeLeeNiSeModificaDesdeOtro()
        {
            using var escenario = await Escenario.CrearAsync(duracionServicio: 60);
            await escenario.GuardarHorarioAsync((DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(18, 0)));

            var tenantA = escenario.TenantProvider.TenantId;
            var tenantB = Guid.NewGuid();
            await escenario.AsegurarTenantAsync(tenantB);

            // El tenant B mira la misma base: no ve ni una fila del tenant A.
            escenario.TenantProvider.TenantId = tenantB;
            escenario.Context.ChangeTracker.Clear();

            Assert.Empty(await escenario.Context.TenantBookingBusinessHours.ToListAsync());

            // Y al guardar lo suyo crea SUS propias filas, sin tocar las del tenant A.
            await escenario.GuardarHorarioAsync((DayOfWeek.Friday, new TimeOnly(14, 0), new TimeOnly(20, 0)));
            escenario.Context.ChangeTracker.Clear();

            var deB = await escenario.Context.TenantBookingBusinessHours.ToListAsync();
            Assert.Equal(7, deB.Count);
            Assert.All(deB, fila => Assert.Equal(tenantB, fila.TenantId));

            var deA = await escenario.Context.TenantBookingBusinessHours
                .IgnoreQueryFilters()
                .Where(fila => fila.TenantId == tenantA)
                .ToListAsync();

            Assert.Equal(7, deA.Count);
            var lunesDeA = deA.Single(fila => fila.DiaSemana == DayOfWeek.Monday);
            Assert.True(lunesDeA.IsEnabled);
            Assert.Equal(new TimeOnly(9, 0), lunesDeA.OpenTime);
        }

        [Fact]
        public async Task GuardarHorario_NoAceptaElTenantIdQueVengaDelFormulario()
        {
            // El VM de configuración no expone TenantId: la entidad lo marca [BindNever] y el
            // guard del DbContext lo reescribe con el tenant del contexto autenticado.
            Assert.Null(typeof(BookingSettingsViewModel).GetProperty("TenantId"));

            var propiedad = typeof(TenantBookingBusinessHour).GetProperty(nameof(TenantBookingBusinessHour.TenantId));
            Assert.NotNull(propiedad);
            Assert.NotEmpty(propiedad!.GetCustomAttributes(
                typeof(Microsoft.AspNetCore.Mvc.ModelBinding.BindNeverAttribute), inherit: true));
        }

        // ──────────────────────────── Escenario ────────────────────────────

        private sealed class Escenario : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

            private Escenario(
                ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider,
                int servicioId,
                int funcionarioId)
            {
                Context = context;
                _connection = connection;
                TenantProvider = tenantProvider;
                ServicioId = servicioId;
                FuncionarioId = funcionarioId;
            }

            public ApplicationDbContext Context { get; }
            public TestTenantProvider TenantProvider { get; }
            public int ServicioId { get; }
            public int FuncionarioId { get; }

            public static async Task<Escenario> CrearAsync(int duracionServicio)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                var escenario = new Escenario(context, connection, tenantProvider, 0, 0);
                await escenario.AsegurarTenantAsync(tenantProvider.TenantId);

                var puesto = new Puesto { NombrePuesto = $"Puesto {Guid.NewGuid():N}", Detalle = "Reservas", Activo = true };
                context.Puestos.Add(puesto);
                await context.SaveChangesAsync();

                var funcionario = new Funcionario
                {
                    Nombre = "Profesional",
                    IdPuesto = puesto.IdPuesto,
                    ColorCalendario = "#123456",
                    PorcentajeGanancia = 40m,
                    PorcentajeProducto = 10m,
                    FechaIngreso = new DateTime(2026, 1, 1),
                    Activo = true
                };
                context.Funcionarios.Add(funcionario);

                var servicio = new Servicio
                {
                    Nombre = "Corte",
                    Precio = 5000m,
                    DuracionMinutos = duracionServicio,
                    Activo = true
                };
                context.Servicios.Add(servicio);
                await context.SaveChangesAsync();

                return new Escenario(context, connection, tenantProvider, servicio.Id, funcionario.IdFuncionario);
            }

            private IBookingSettingsService SettingsService() =>
                new BookingSettingsService(
                    Context,
                    TenantProvider,
                    ControllerTestSupport.CreateTenantDisplayNameService("Negocio"));

            private IBookingAvailabilityService AvailabilityService() =>
                ControllerTestSupport.CreateBookingAvailabilityService(Context, Reloj());

            /// <summary>Guarda la configuración con los días indicados abiertos y el resto cerrados.</summary>
            public Task GuardarHorarioAsync(params (DayOfWeek Dia, TimeOnly Apertura, TimeOnly Cierre)[] abiertos)
            {
                var horario = new List<BookingDayScheduleViewModel>();

                for (var indice = 0; indice < 7; indice++)
                {
                    var dia = (DayOfWeek)indice;
                    var abierto = abiertos.FirstOrDefault(entrada => entrada.Dia == dia);
                    var estaAbierto = abiertos.Any(entrada => entrada.Dia == dia);

                    horario.Add(new BookingDayScheduleViewModel
                    {
                        DiaSemana = indice,
                        Abierto = estaAbierto,
                        Apertura = estaAbierto ? abierto.Apertura : new TimeOnly(8, 0),
                        Cierre = estaAbierto ? abierto.Cierre : new TimeOnly(18, 0)
                    });
                }

                return GuardarCrudoAsync(horario);
            }

            /// <summary>Guarda la jornada tal cual llega, sin normalizarla antes (simula el POST).</summary>
            public async Task GuardarCrudoAsync(List<BookingDayScheduleViewModel> horario)
            {
                await SettingsService().SaveSettingsAsync(
                    new BookingSettingsViewModel
                    {
                        PublicBookingEnabled = true,
                        PublicBookingSlug = $"negocio-{Guid.NewGuid():N}"[..14],
                        PublicBookingMinAdvanceMinutes = 0,
                        PublicBookingMaxDaysAhead = 60,
                        SlotIntervalMinutes = 30,
                        Horario = horario
                    },
                    "user-test");

                Context.ChangeTracker.Clear();
            }

            /// <summary>Configuración PLANA, como la de un tenant anterior a la jornada por día.</summary>
            public async Task SeedSettingsPlanasAsync(bool maskLunesASabado, TimeOnly apertura, TimeOnly cierre)
            {
                Context.TenantBookingSettings.Add(new TenantBookingSettings
                {
                    PublicBookingEnabled = true,
                    PublicBookingSlug = $"legado-{Guid.NewGuid():N}"[..14],
                    WorkingDaysMask = maskLunesASabado ? TenantBookingSettings.DefaultWorkingDaysMask : 0b111_1111,
                    OpenTime = apertura,
                    CloseTime = cierre,
                    SlotIntervalMinutes = 30,
                    PublicBookingMinAdvanceMinutes = 0,
                    PublicBookingMaxDaysAhead = 60
                });

                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            public async Task<IReadOnlyList<string>> HorasDe(DateOnly fecha) =>
                await AvailabilityService().GetAvailableSlotsAsync(ServicioId, fecha, funcionarioId: null);

            public Task<SlotResolution> ResolverAsync(DateTime inicio, int? funcionarioId = null) =>
                AvailabilityService().ResolveSlotAsync(ServicioId, inicio, funcionarioId);

            public async Task CrearBloqueoAsync(string nombre, TimeOnly inicio, TimeOnly fin, int diasMask)
            {
                Context.RecurringScheduleRules.Add(new RecurringScheduleRule
                {
                    Nombre = nombre,
                    HoraInicio = inicio,
                    HoraFin = fin,
                    DiasSemanaMask = diasMask,
                    VigenteDesde = new DateOnly(2026, 1, 1),
                    Activa = true,
                    Alcance = RecurringScheduleScope.TodosLosColaboradores
                });

                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            public async Task SeedCitaAsync(DateTime inicio, int duracionMinutos, string tipo)
            {
                Context.Citas.Add(new LuxuryApp.Models.Calendar.Cita
                {
                    FuncionarioId = FuncionarioId,
                    ServicioId = tipo == "DESCANSO" ? null : ServicioId,
                    FechaHoraCita = inicio,
                    DuracionMinutos = duracionMinutos,
                    Tipo = tipo,
                    NombreCliente = "Cliente"
                });

                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            public async Task SeedSolicitudPendienteAsync(DateTime inicio, int duracionMinutos)
            {
                Context.BookingRequests.Add(new BookingRequest
                {
                    ServicioId = ServicioId,
                    FuncionarioId = FuncionarioId,
                    FuncionarioAsignadoId = FuncionarioId,
                    FechaHoraInicioSolicitada = inicio,
                    DuracionMinutos = duracionMinutos,
                    Estado = BookingRequestStates.Pending,
                    NombreCliente = "Cliente",
                    TelefonoCliente = "88887777"
                });

                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            /// <summary>Marca el servicio como NO visible online (configuración explícita del tenant).</summary>
            public async Task OcultarServicioAsync()
            {
                Context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting
                {
                    ServicioId = ServicioId,
                    IsVisibleOnline = false
                });

                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            /// <summary>Restringe el servicio a los profesionales indicados (el resto queda fuera).</summary>
            public async Task AsignarProfesionalesAsync(params int[] funcionarioIds)
            {
                foreach (var funcionarioId in funcionarioIds)
                {
                    Context.TenantBookingFuncionarioServices.Add(new TenantBookingFuncionarioService
                    {
                        ServicioId = ServicioId,
                        FuncionarioId = funcionarioId,
                        IsEnabled = true
                    });
                }

                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            public async Task<int> SeedFuncionarioAsync(string nombre)
            {
                var puesto = new Puesto { NombrePuesto = $"Puesto {Guid.NewGuid():N}", Detalle = "Reservas", Activo = true };
                Context.Puestos.Add(puesto);
                await Context.SaveChangesAsync();

                var funcionario = new Funcionario
                {
                    Nombre = nombre,
                    IdPuesto = puesto.IdPuesto,
                    ColorCalendario = "#654321",
                    PorcentajeGanancia = 40m,
                    PorcentajeProducto = 10m,
                    FechaIngreso = new DateTime(2026, 1, 1),
                    Activo = true
                };
                Context.Funcionarios.Add(funcionario);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();

                return funcionario.IdFuncionario;
            }

            public async Task AsegurarTenantAsync(Guid tenantId)
            {
                if (await Context.Tenants.IgnoreQueryFilters().AnyAsync(tenant => tenant.Id == tenantId))
                {
                    return;
                }

                Context.Tenants.Add(new Tenant
                {
                    Id = tenantId,
                    Nombre = $"Tenant {tenantId:N}",
                    Activo = true
                });

                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }
    }
}
