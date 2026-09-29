using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Notifications;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
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
    /// "Próximos espacios disponibles" del enlace público: el atajo que se muestra apenas el
    /// visitante elige servicio y profesional, sin haber tocado el calendario.
    ///
    /// <para>
    /// El endpoint es anónimo, así que aquí se prueba lo que un atacante puede hacer llamándolo
    /// directamente: reenviar ids de otro negocio, pedir un profesional que no atiende el servicio
    /// o uno inactivo, y pedir un servicio que el negocio no publicó. También se cubre la
    /// minimización de datos y que ver un espacio NO lo reserva.
    /// </para>
    /// </summary>
    public class PublicBookingNextSlotsTests
    {
        // Reloj fijo de la suite: martes 2026-05-26 10:30 (hora del negocio).
        private static readonly DateOnly Hoy = new(2026, 5, 26);

        // ──────────────────────── Disponibilidad real ────────────────────────

        [Fact]
        public async Task NextSlots_SinElegirFecha_DevuelveEspaciosRealesDelServicio()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            await env.SeedFuncionarioAsync("Deyner");

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, funcionarioId: null);

            Assert.True(result.Success);
            Assert.NotEmpty(result.NextAvailableSlots);
            Assert.True(result.NextAvailableSlots.Count <= 5, "el atajo debe mantenerse corto");
        }

        [Fact]
        public async Task NextSlots_NuncaProponeHorasPasadas()
        {
            // Jornada 08:00–18:00 y el reloj marca las 10:30: las horas de la mañana ya pasaron.
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            await env.SeedFuncionarioAsync("Deyner");

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, funcionarioId: null);

            var deHoy = result.NextAvailableSlots.Where(s => s.Fecha == "2026-05-26").ToList();
            Assert.NotEmpty(deHoy);
            Assert.All(deHoy, slot => Assert.True(
                string.CompareOrdinal(slot.Hora, "10:30") >= 0,
                $"propuso {slot.Hora}, que ya pasó"));
        }

        [Fact]
        public async Task NextSlots_RespetaLaAnticipacionMinima()
        {
            using var env = await Env.CreateAsync(minAdvance: 120);
            var servicio = await env.SeedServicioAsync("Corte", 30);
            await env.SeedFuncionarioAsync("Deyner");

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, funcionarioId: null);

            var deHoy = result.NextAvailableSlots.Where(s => s.Fecha == "2026-05-26").ToList();
            // 10:30 + 120 min ⇒ nada antes de las 12:30.
            Assert.All(deHoy, slot => Assert.True(string.CompareOrdinal(slot.Hora, "12:30") >= 0));
        }

        [Fact]
        public async Task NextSlots_ServicioLargo_SoloProponeBloquesQueCaben()
        {
            using var env = await Env.CreateAsync(close: new TimeOnly(12, 0));
            var servicio = await env.SeedServicioAsync("Corte y barba", 90);
            await env.SeedFuncionarioAsync("Deyner");

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, funcionarioId: null);

            Assert.NotEmpty(result.NextAvailableSlots);
            // Cierre a las 12:00 y bloque de 90 min ⇒ ningún inicio después de las 10:30.
            Assert.All(result.NextAvailableSlots, slot =>
                Assert.True(string.CompareOrdinal(slot.Hora, "10:30") <= 0));
        }

        [Fact]
        public async Task NextSlots_SinEspacioEnElHorizonte_DevuelveVacioSinError()
        {
            // Solo hoy es reservable y la agenda del único profesional está llena todo el día.
            using var env = await Env.CreateAsync(maxDaysAhead: 0);
            var servicio = await env.SeedServicioAsync("Corte", 30);
            var deyner = await env.SeedFuncionarioAsync("Deyner");
            await env.SeedCitaAsync(deyner.IdFuncionario, Hoy.ToDateTime(new TimeOnly(8, 0)), 600);

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, funcionarioId: null);

            Assert.True(result.Success);
            Assert.Empty(result.NextAvailableSlots);
        }

        // ──────────────────────── "Cualquier profesional" ────────────────────────

        [Fact]
        public async Task NextSlots_CualquierProfesional_ConservaQuienAtiendeCadaEspacio()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            var ana = await env.SeedFuncionarioAsync("Ana");
            var bruno = await env.SeedFuncionarioAsync("Bruno");

            // Ana ocupada el resto de hoy; Bruno libre.
            await env.SeedCitaAsync(ana.IdFuncionario, Hoy.ToDateTime(new TimeOnly(8, 0)), 600);

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, funcionarioId: null);

            var deHoy = result.NextAvailableSlots.Where(s => s.Fecha == "2026-05-26").ToList();
            Assert.NotEmpty(deHoy);

            // Cada espacio viaja con la persona concreta que puede atenderlo, no con "alguien".
            Assert.All(deHoy, slot =>
            {
                Assert.Equal(bruno.IdFuncionario, slot.FuncionarioId);
                Assert.Equal("Bruno", slot.FuncionarioNombre);
            });
        }

        [Fact]
        public async Task NextSlots_ProfesionalEspecifico_SoloDevuelveEspaciosDeEsaPersona()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            var ana = await env.SeedFuncionarioAsync("Ana");
            await env.SeedFuncionarioAsync("Bruno");

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, ana.IdFuncionario);

            Assert.NotEmpty(result.NextAvailableSlots);
            Assert.All(result.NextAvailableSlots, slot => Assert.Equal(ana.IdFuncionario, slot.FuncionarioId));
        }

        // ──────────────────────── Aislamiento multi-tenant ────────────────────────

        [Fact]
        public async Task NextSlots_ServicioDeOtroTenant_NoDevuelveNada()
        {
            using var env = await Env.CreateAsync();

            // Servicio y profesional del negocio A (el "atacado").
            var servicioAjeno = await env.SeedServicioAsync("Corte ajeno", 30);
            await env.SeedFuncionarioAsync("Ajeno");

            // El atacante navega la URL pública del negocio B y reenvía el id del negocio A.
            await env.SwitchToNewTenantAsync();
            await env.SeedServicioAsync("Corte propio", 30);
            await env.SeedFuncionarioAsync("Propio");

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicioAjeno.Id, funcionarioId: null);

            Assert.False(result.Success);
            Assert.Empty(result.NextAvailableSlots);
        }

        [Fact]
        public async Task NextSlots_FuncionarioDeOtroTenant_NoCaeDeVueltaEnCualquiera()
        {
            using var env = await Env.CreateAsync();
            var funcionarioAjeno = await env.SeedFuncionarioAsync("Ajeno");

            await env.SwitchToNewTenantAsync();
            var servicioPropio = await env.SeedServicioAsync("Corte propio", 30);
            await env.SeedFuncionarioAsync("Propio");

            var result = await env.Service.GetNextSlotsAsync(
                env.Context(), servicioPropio.Id, funcionarioAjeno.IdFuncionario);

            // Clave: un id incompatible NO puede degradar a "cualquier profesional" y devolver la
            // agenda del negocio B; tampoco puede revelar nada del negocio A.
            Assert.Empty(result.NextAvailableSlots);
        }

        // ──────────────────────── Reglas del negocio ────────────────────────

        [Fact]
        public async Task NextSlots_ProfesionalQueNoAtiendeElServicio_NoDevuelveNada()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            var ana = await env.SeedFuncionarioAsync("Ana");
            var bruno = await env.SeedFuncionarioAsync("Bruno");

            // Solo Bruno queda habilitado para este servicio.
            await env.SeedAsignacionAsync(servicio.Id, bruno.IdFuncionario);

            var conAna = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, ana.IdFuncionario);
            var conBruno = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, bruno.IdFuncionario);

            Assert.Empty(conAna.NextAvailableSlots);
            Assert.NotEmpty(conBruno.NextAvailableSlots);
        }

        [Fact]
        public async Task NextSlots_ProfesionalInactivo_NoDevuelveNada()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            var ana = await env.SeedFuncionarioAsync("Ana");
            await env.SeedFuncionarioAsync("Bruno");

            await env.DesactivarFuncionarioAsync(ana.IdFuncionario);

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, ana.IdFuncionario);

            Assert.Empty(result.NextAvailableSlots);
        }

        [Fact]
        public async Task NextSlots_ServicioOcultoOInactivo_EsRechazado()
        {
            using var env = await Env.CreateAsync();
            var visible = await env.SeedServicioAsync("Visible", 30);
            var oculto = await env.SeedServicioAsync("Oculto", 30);
            var inactivo = await env.SeedServicioAsync("Inactivo", 30, activo: false);
            await env.SeedFuncionarioAsync("Deyner");

            // Con configuración explícita, solo lo marcado como visible se publica.
            await env.SeedVisibilidadAsync(visible.Id, visible: true);
            await env.SeedVisibilidadAsync(oculto.Id, visible: false);

            var conVisible = await env.Service.GetNextSlotsAsync(env.Context(), visible.Id, null);
            var conOculto = await env.Service.GetNextSlotsAsync(env.Context(), oculto.Id, null);
            var conInactivo = await env.Service.GetNextSlotsAsync(env.Context(), inactivo.Id, null);

            Assert.True(conVisible.Success);
            Assert.NotEmpty(conVisible.NextAvailableSlots);

            Assert.False(conOculto.Success);
            Assert.Empty(conOculto.NextAvailableSlots);

            Assert.False(conInactivo.Success);
            Assert.Empty(conInactivo.NextAvailableSlots);
        }

        [Fact]
        public async Task NextSlots_CitaExistente_NoSeVuelveAOfrecerEseEspacio()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            var deyner = await env.SeedFuncionarioAsync("Deyner");

            // Ocupa exactamente el primer espacio que se ofrecería hoy (10:30).
            await env.SeedCitaAsync(deyner.IdFuncionario, Hoy.ToDateTime(new TimeOnly(10, 30)), 30);

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, funcionarioId: null);

            Assert.DoesNotContain(result.NextAvailableSlots, s => s.Fecha == "2026-05-26" && s.Hora == "10:30");
            Assert.Contains(result.NextAvailableSlots, s => s.Fecha == "2026-05-26" && s.Hora == "11:00");
        }

        // ──────────────────────── Minimización de datos ────────────────────────

        [Fact]
        public async Task NextSlots_CuandoElNegocioNoDejaElegirProfesional_NoRevelaQuienEsta()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            await env.SeedFuncionarioAsync("Deyner");

            var result = await env.Service.GetNextSlotsAsync(
                env.Context(permiteElegirFuncionario: false), servicio.Id, funcionarioId: null);

            Assert.NotEmpty(result.NextAvailableSlots);
            // El espacio sigue siendo reservable, pero el negocio no publica su agenda por persona.
            Assert.All(result.NextAvailableSlots, slot =>
            {
                Assert.Null(slot.FuncionarioId);
                Assert.Null(slot.FuncionarioNombre);
            });
        }

        [Fact]
        public async Task NextSlots_SoloExponeLoNecesarioParaElegirUnEspacio()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            var deyner = await env.SeedFuncionarioAsync("Deyner");
            await env.SeedCitaAsync(deyner.IdFuncionario, Hoy.ToDateTime(new TimeOnly(11, 0)), 30, "Cliente Secreto");

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, funcionarioId: null);

            // El DTO no tiene forma de transportar citas, clientes ni motivos de bloqueo: la única
            // manera de que se filtraran sería agregando campos. Este test lo deja fijado.
            var campos = typeof(NextAvailableSlot).GetProperties().Select(p => p.Name).ToArray();
            Assert.Equal(
                new[] { "Fecha", "FechaLabel", "Hora", "HoraLabel", "FuncionarioId", "FuncionarioNombre" }.Order(),
                campos.Order());

            var serializado = System.Text.Json.JsonSerializer.Serialize(result);
            Assert.DoesNotContain("Secreto", serializado);
        }

        // ──────────────────────── Parámetros inválidos ────────────────────────

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(999999)]
        public async Task NextSlots_ServicioInexistenteOInvalido_FallaDeFormaControlada(int servicioId)
        {
            using var env = await Env.CreateAsync();
            await env.SeedServicioAsync("Corte", 30);
            await env.SeedFuncionarioAsync("Deyner");

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicioId, funcionarioId: null);

            Assert.False(result.Success);
            Assert.Empty(result.NextAvailableSlots);
            Assert.False(string.IsNullOrWhiteSpace(result.Mensaje));
        }

        [Fact]
        public async Task NextSlots_ReservasDesactivadas_NoDevuelveEspacios()
        {
            using var env = await Env.CreateAsync(publicBookingEnabled: false);
            var servicio = await env.SeedServicioAsync("Corte", 30);
            await env.SeedFuncionarioAsync("Deyner");

            var result = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, funcionarioId: null);

            Assert.Empty(result.NextAvailableSlots);
        }

        // ──────────────────────── Fecha elegida + alternativas ────────────────────────

        [Fact]
        public async Task Disponibilidad_ConFechaQueSiTieneHoras_TambienTraeAlternativasSinRepetir()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            await env.SeedFuncionarioAsync("Deyner");

            var result = await env.Service.GetAvailabilityAsync(
                env.Context(), servicio.Id, "2026-05-26", funcionarioId: null);

            Assert.NotEmpty(result.Horas);
            Assert.NotEmpty(result.NextAvailableSlots);
            // Las alternativas no pueden repetir el día que ya se está mostrando arriba.
            Assert.DoesNotContain(result.NextAvailableSlots, slot => slot.Fecha == "2026-05-26");
        }

        [Fact]
        public async Task Disponibilidad_ConFechaSinHoras_SigueOfreciendoProximosEspacios()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            var deyner = await env.SeedFuncionarioAsync("Deyner");
            await env.SeedCitaAsync(deyner.IdFuncionario, Hoy.ToDateTime(new TimeOnly(8, 0)), 600);

            var result = await env.Service.GetAvailabilityAsync(
                env.Context(), servicio.Id, "2026-05-26", funcionarioId: null);

            Assert.Empty(result.Horas);
            Assert.False(string.IsNullOrWhiteSpace(result.Mensaje));
            Assert.NotEmpty(result.NextAvailableSlots);
            // Lo próximo que ofrece ya no es hoy.
            Assert.All(result.NextAvailableSlots, slot => Assert.NotEqual("2026-05-26", slot.Fecha));
        }

        [Fact]
        public async Task Disponibilidad_FechaInvalida_FallaSinExponerNada()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            await env.SeedFuncionarioAsync("Deyner");

            var result = await env.Service.GetAvailabilityAsync(
                env.Context(), servicio.Id, "no-es-una-fecha", funcionarioId: null);

            Assert.False(result.Success);
            Assert.Empty(result.Horas);
            Assert.Empty(result.NextAvailableSlots);
        }

        // ──────────────────────── Concurrencia ────────────────────────

        [Fact]
        public async Task EspacioMostrado_NoQuedaReservado_YLaSolicitudFallaSiOtroLoTomaAntes()
        {
            using var env = await Env.CreateAsync();
            var servicio = await env.SeedServicioAsync("Corte", 30);
            var deyner = await env.SeedFuncionarioAsync("Deyner");

            // El visitante A ve el atajo y elige el primer espacio.
            var sugerencias = await env.Service.GetNextSlotsAsync(env.Context(), servicio.Id, null);
            var elegido = sugerencias.NextAvailableSlots.First();

            // Entre que se pintó y que se envió, alguien más ocupó ese horario.
            var inicio = DateOnly.Parse(elegido.Fecha).ToDateTime(TimeOnly.Parse(elegido.Hora));
            await env.SeedCitaAsync(deyner.IdFuncionario, inicio, 30);

            var envio = await env.Service.SubmitAsync(env.Context(), new PublicBookingRequestInput
            {
                ServicioId = servicio.Id,
                Fecha = elegido.Fecha,
                Hora = elegido.Hora,
                Nombre = "Cliente Tardío",
                Telefono = "88887777",
                SubmissionToken = Guid.NewGuid().ToString("N")
            });

            Assert.False(envio.Success);
            Assert.False(string.IsNullOrWhiteSpace(envio.Message));
            // Y no se creó nada: mostrar un espacio nunca lo reserva.
            Assert.Equal(0, await env.Db.BookingRequests.CountAsync());
        }

        // ──────────────────────── Fixture ────────────────────────

        private sealed class Env : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
            private readonly TestTenantProvider _tenantProvider;
            private readonly TimeOnly _open;
            private readonly TimeOnly _close;
            private readonly int _minAdvance;
            private readonly int _maxDaysAhead;
            private readonly bool _publicBookingEnabled;

            public ApplicationDbContext Db { get; }
            public PublicBookingService Service { get; }

            private Env(
                ApplicationDbContext db,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider,
                PublicBookingService service,
                TimeOnly open,
                TimeOnly close,
                int minAdvance,
                int maxDaysAhead,
                bool publicBookingEnabled)
            {
                Db = db;
                _connection = connection;
                _tenantProvider = tenantProvider;
                Service = service;
                _open = open;
                _close = close;
                _minAdvance = minAdvance;
                _maxDaysAhead = maxDaysAhead;
                _publicBookingEnabled = publicBookingEnabled;
            }

            public static async Task<Env> CreateAsync(
                TimeOnly? open = null,
                TimeOnly? close = null,
                int minAdvance = 0,
                int maxDaysAhead = 30,
                bool publicBookingEnabled = true)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (db, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                var catalog = new BookingCatalogService(db);
                var service = new PublicBookingService(
                    db,
                    new NoOpBookingSettingsService(),
                    ControllerTestSupport.CreateBookingAvailabilityService(
                        db, new FixedBusinessDateTimeProvider(), catalog),
                    catalog,
                    new FixedBusinessDateTimeProvider(),
                    new FakeTenantWhatsAppFeatureService { IsEnabled = false },
                    new NoOpNotificationService(),
                    new HttpContextAccessor(),
                    ControllerTestSupport.CreateClienteIdentityService(db),
                    NullLogger<PublicBookingService>.Instance);

                var env = new Env(
                    db, connection, tenantProvider, service,
                    open ?? new TimeOnly(8, 0), close ?? new TimeOnly(18, 0),
                    minAdvance, maxDaysAhead, publicBookingEnabled);

                await env.SeedTenantAsync();
                return env;
            }

            /// <summary>Cambia el request a OTRO negocio, como si se navegara otra URL pública.</summary>
            public async Task SwitchToNewTenantAsync()
            {
                _tenantProvider.TenantId = Guid.NewGuid();
                Db.ChangeTracker.Clear();
                await SeedTenantAsync();
            }

            public PublicBookingTenantContext Context(bool permiteElegirFuncionario = true) => new()
            {
                TenantId = _tenantProvider.TenantId,
                NombreNegocio = "Negocio",
                Slug = "negocio",
                PermiteElegirFuncionario = permiteElegirFuncionario,
                PermiteCualquierFuncionario = true,
                MostrarFotosFuncionarios = false,
                MinAdvanceMinutes = _minAdvance,
                MaxDaysAhead = _maxDaysAhead
            };

            private async Task SeedTenantAsync()
            {
                if (!await Db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == _tenantProvider.TenantId))
                {
                    Db.Tenants.Add(new Tenant { Id = _tenantProvider.TenantId, Nombre = "Negocio", Activo = true });
                    await Db.SaveChangesAsync();
                }

                Db.TenantBookingSettings.Add(new TenantBookingSettings
                {
                    PublicBookingEnabled = _publicBookingEnabled,
                    PublicBookingSlug = $"slug-{Guid.NewGuid():N}"[..18],
                    OpenTime = _open,
                    CloseTime = _close,
                    SlotIntervalMinutes = 30,
                    PublicBookingMinAdvanceMinutes = _minAdvance,
                    PublicBookingMaxDaysAhead = _maxDaysAhead,
                    WorkingDaysMask = 0b111_1111
                });
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public async Task<Servicio> SeedServicioAsync(string nombre, int duracion, bool activo = true)
            {
                var servicio = new Servicio
                {
                    Nombre = nombre,
                    Precio = 10_000m,
                    DuracionMinutos = duracion,
                    Activo = activo
                };
                Db.Servicios.Add(servicio);
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
                return servicio;
            }

            public async Task<Funcionario> SeedFuncionarioAsync(string nombre)
            {
                var puesto = new Puesto { NombrePuesto = $"Puesto {Guid.NewGuid():N}", Detalle = "R", Activo = true };
                Db.Puestos.Add(puesto);
                await Db.SaveChangesAsync();

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
                Db.Funcionarios.Add(funcionario);
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
                return funcionario;
            }

            public async Task DesactivarFuncionarioAsync(int funcionarioId)
            {
                var funcionario = await Db.Funcionarios.SingleAsync(f => f.IdFuncionario == funcionarioId);
                funcionario.Activo = false;
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public async Task SeedCitaAsync(int funcionarioId, DateTime inicio, int duracion, string cliente = "Ocupado")
            {
                Db.Citas.Add(new Cita
                {
                    FuncionarioId = funcionarioId,
                    FechaHoraCita = inicio,
                    DuracionMinutos = duracion,
                    Tipo = "CITA",
                    NombreCliente = cliente
                });
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public async Task SeedAsignacionAsync(int servicioId, int funcionarioId)
            {
                Db.TenantBookingFuncionarioServices.Add(new TenantBookingFuncionarioService
                {
                    ServicioId = servicioId,
                    FuncionarioId = funcionarioId,
                    IsEnabled = true
                });
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public async Task SeedVisibilidadAsync(int servicioId, bool visible)
            {
                Db.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting
                {
                    ServicioId = servicioId,
                    IsVisibleOnline = visible,
                    ShowPrice = true
                });
                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }

            public void Dispose()
            {
                Db.Dispose();
                _connection.Dispose();
            }
        }

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

            public Task CreateBookingRequestReceivedAsync(BookingRequest request, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task CreateAppointmentCancelledViaWhatsAppAsync(Cita cita, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
