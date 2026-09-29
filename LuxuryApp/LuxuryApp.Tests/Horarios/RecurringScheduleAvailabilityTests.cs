using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Horarios;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Horarios;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.Horarios
{
    /// <summary>
    /// Integración de los bloqueos recurrentes con la disponibilidad real: reservas públicas,
    /// creación manual de citas, conflictos y aislamiento entre negocios.
    /// </summary>
    public class RecurringScheduleAvailabilityTests
    {
        // Lunes 2026-08-03.
        private static readonly DateOnly Lunes = new(2026, 8, 3);

        [Fact]
        public async Task Disponibilidad_BloqueaLaCreacionManualDeUnaCitaEnElAlmuerzo()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            await fixture.CrearAlmuerzoAsync();

            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(new CalendarUpsertRequest
                {
                    Tipo = "CITA",
                    NombreCliente = "Cliente",
                    ServicioId = fixture.ServicioId,
                    FuncionarioId = fixture.FuncionarioId,
                    FechaHoraCita = Lunes.ToDateTime(new TimeOnly(13, 30))
                }));

            Assert.Contains("Almuerzo", error.Message);
            Assert.Equal(0, await fixture.Context.Citas.CountAsync());
        }

        [Fact]
        public async Task Disponibilidad_PermiteUnaCitaFueraDelBloqueo()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            await fixture.CrearAlmuerzoAsync();

            var respuesta = await fixture.Calendar.CreateAsync(new CalendarUpsertRequest
            {
                Tipo = "CITA",
                NombreCliente = "Cliente",
                ServicioId = fixture.ServicioId,
                FuncionarioId = fixture.FuncionarioId,
                FechaHoraCita = Lunes.ToDateTime(new TimeOnly(10, 0))
            });

            Assert.True(respuesta.Id > 0);
            Assert.Equal(1, await fixture.Context.Citas.CountAsync());
        }

        [Fact]
        public async Task ReservasPublicas_NoOfrecenSlotsDentroDelBloqueo()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            await fixture.SeedBookingSettingsAsync();

            var sinRegla = await fixture.Booking.GetAvailableSlotsAsync(fixture.ServicioId, Lunes, null);
            Assert.Contains("13:00", sinRegla);
            Assert.Contains("13:30", sinRegla);

            await fixture.CrearAlmuerzoAsync();

            var conRegla = await fixture.Booking.GetAvailableSlotsAsync(fixture.ServicioId, Lunes, null);

            Assert.DoesNotContain("13:00", conRegla);
            Assert.DoesNotContain("13:30", conRegla);
            // El resto de la jornada sigue disponible.
            Assert.Contains("10:00", conRegla);
            Assert.Contains("14:00", conRegla);
        }

        [Fact]
        public async Task ProximosEspacios_SaltanElBloqueoRecurrente()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            await fixture.SeedBookingSettingsAsync();
            await fixture.CrearAlmuerzoAsync();

            // Mañana completa ocupada: el siguiente hueco del lunes cae justo contra el almuerzo,
            // así que si la sugerencia ignorara el bloqueo propondría las 13:00.
            fixture.Context.Citas.Add(new Cita
            {
                NombreCliente = "Cliente",
                FuncionarioId = fixture.FuncionarioId,
                ServicioId = fixture.ServicioId,
                FechaHoraCita = Lunes.ToDateTime(new TimeOnly(8, 0)),
                DuracionMinutos = 300,
                Tipo = "CITA"
            });
            await fixture.Context.SaveChangesAsync();

            var proximos = await fixture.Booking.GetNextAvailableSlotsAsync(
                fixture.ServicioId, Lunes, fixture.FuncionarioId, maxSuggestions: 3);

            Assert.NotEmpty(proximos);
            Assert.Equal(Lunes, proximos[0].Fecha);
            Assert.Equal(new TimeOnly(14, 0), proximos[0].Hora);
            Assert.DoesNotContain(proximos, slot =>
                slot.Fecha == Lunes && slot.Hora >= new TimeOnly(13, 0) && slot.Hora < new TimeOnly(14, 0));
        }

        [Fact]
        public async Task ReservasPublicas_RechazanUnSlotManipuladoDentroDelBloqueo()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            await fixture.SeedBookingSettingsAsync();
            await fixture.CrearAlmuerzoAsync();

            var resolucion = await fixture.Booking.ResolveSlotAsync(
                fixture.ServicioId,
                Lunes.ToDateTime(new TimeOnly(13, 0)),
                fixture.FuncionarioId);

            Assert.False(resolucion.Disponible);
        }

        [Fact]
        public async Task Excepcion_LiberaEsaFechaYLaReservaVuelveAEstarDisponible()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            await fixture.SeedBookingSettingsAsync();
            var ruleId = await fixture.CrearAlmuerzoAsync();

            await fixture.Schedule.AddExceptionAsync(
                new RecurringScheduleExceptionFormViewModel
                {
                    RuleId = ruleId,
                    Fecha = Lunes.ToDateTime(TimeOnly.MinValue),
                    Tipo = RecurringScheduleExceptionType.Omitir,
                    Motivo = "Feriado"
                },
                "admin",
                default);

            var slots = await fixture.Booking.GetAvailableSlotsAsync(fixture.ServicioId, Lunes, null);

            Assert.Contains("13:00", slots);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.RecurringScheduleExceptionCreated));
        }

        [Fact]
        public async Task Excepcion_ConHorarioAlternativo_MueveElBloqueoSoloEsaFecha()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            var ruleId = await fixture.CrearAlmuerzoAsync();

            await fixture.Schedule.AddExceptionAsync(
                new RecurringScheduleExceptionFormViewModel
                {
                    RuleId = ruleId,
                    FuncionarioId = fixture.FuncionarioId,
                    Fecha = Lunes.ToDateTime(TimeOnly.MinValue),
                    Tipo = RecurringScheduleExceptionType.CambiarHorario,
                    HoraInicioAlternativa = new TimeOnly(14, 0),
                    HoraFinAlternativa = new TimeOnly(15, 0)
                },
                "admin",
                default);

            var libre = await fixture.Availability.CheckAsync(
                fixture.FuncionarioId, Lunes.ToDateTime(new TimeOnly(13, 0)), 30);

            var ocupado = await fixture.Availability.CheckAsync(
                fixture.FuncionarioId, Lunes.ToDateTime(new TimeOnly(14, 0)), 30);

            Assert.True(libre.Disponible);
            Assert.False(ocupado.Disponible);

            // La regla general sigue intacta.
            var regla = await fixture.Context.RecurringScheduleRules.SingleAsync(r => r.Id == ruleId);
            Assert.Equal(new TimeOnly(13, 0), regla.HoraInicio);
        }

        [Fact]
        public async Task ReglaPausada_DejaDeBloquear()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            var ruleId = await fixture.CrearAlmuerzoAsync();

            var antes = await fixture.Availability.CheckAsync(
                fixture.FuncionarioId, Lunes.ToDateTime(new TimeOnly(13, 0)), 30);
            Assert.False(antes.Disponible);

            await fixture.Schedule.SetActivaAsync(ruleId, false, "admin", default);

            var despues = await fixture.Availability.CheckAsync(
                fixture.FuncionarioId, Lunes.ToDateTime(new TimeOnly(13, 0)), 30);

            Assert.True(despues.Disponible);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.RecurringScheduleRulePaused));
        }

        [Fact]
        public async Task ReglaConFechaFinal_DejaDeBloquearDespuesDeEsaFecha()
        {
            using var fixture = await ScheduleFixture.CreateAsync();

            var form = ScheduleFixture.BuildAlmuerzoForm();
            form.VigenteHasta = Lunes.ToDateTime(TimeOnly.MinValue);
            await fixture.Schedule.CreateAsync(form, "admin", default);

            var dentro = await fixture.Availability.CheckAsync(
                fixture.FuncionarioId, Lunes.ToDateTime(new TimeOnly(13, 0)), 30);

            var fuera = await fixture.Availability.CheckAsync(
                fixture.FuncionarioId, Lunes.AddDays(1).ToDateTime(new TimeOnly(13, 0)), 30);

            Assert.False(dentro.Disponible);
            Assert.True(fuera.Disponible);
        }

        [Fact]
        public async Task Calendario_DevuelveLosBloqueosDelDiaParaPintarlos()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            await fixture.CrearAlmuerzoAsync();

            var bloqueos = await fixture.Availability.GetRecurringBlocksAsync(Lunes, Lunes);

            var bloqueo = Assert.Single(bloqueos);
            Assert.Equal("Almuerzo", bloqueo.Titulo);
            Assert.Equal(fixture.FuncionarioId, bloqueo.FuncionarioId);
            Assert.Equal(60, bloqueo.DuracionMinutos);
        }

        // ─────────────── Conflictos ───────────────

        [Fact]
        public async Task Crear_ConCitasQueCoinciden_PideConfirmacionYNoGuardaNada()
        {
            using var fixture = await ScheduleFixture.CreateAsync();

            await fixture.SeedCitaAsync(Lunes.ToDateTime(new TimeOnly(13, 15)));

            var resultado = await fixture.Schedule.CreateAsync(
                ScheduleFixture.BuildAlmuerzoForm(), "admin", default);

            Assert.True(resultado.RequiereConfirmacion);
            Assert.Equal(1, resultado.Conflictos.Total);
            Assert.Contains("1 cita", resultado.Conflictos.Mensaje);
            Assert.Equal(0, await fixture.Context.RecurringScheduleRules.CountAsync());
        }

        [Fact]
        public async Task Crear_ConfirmandoConflictos_GuardaLaReglaYConservaLasCitas()
        {
            using var fixture = await ScheduleFixture.CreateAsync();

            var citaId = await fixture.SeedCitaAsync(Lunes.ToDateTime(new TimeOnly(13, 15)));

            var form = ScheduleFixture.BuildAlmuerzoForm();
            form.ConfirmarConflictos = true;

            var resultado = await fixture.Schedule.CreateAsync(form, "admin", default);

            Assert.False(resultado.RequiereConfirmacion);
            Assert.True(resultado.RuleId > 0);

            // La cita existente sigue exactamente donde estaba.
            var cita = await fixture.Context.Citas.SingleAsync(c => c.Id == citaId);
            Assert.Equal(Lunes.ToDateTime(new TimeOnly(13, 15)), cita.FechaHoraCita);
            Assert.Equal(1, await fixture.Context.Citas.CountAsync());

            Assert.True(fixture.Audit.Contains(PlatformAuditActions.RecurringScheduleRuleActivatedWithConflicts));
        }

        [Fact]
        public async Task Crear_ConCitaConfirmada_ImpideNuevasReservasEnLosEspaciosLibres()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            await fixture.SeedBookingSettingsAsync();
            await fixture.SeedCitaAsync(Lunes.ToDateTime(new TimeOnly(13, 15)));

            var form = ScheduleFixture.BuildAlmuerzoForm();
            form.ConfirmarConflictos = true;
            await fixture.Schedule.CreateAsync(form, "admin", default);

            // El martes está libre de citas pero el bloqueo igual impide reservar.
            var martes = Lunes.AddDays(1);
            var slots = await fixture.Booking.GetAvailableSlotsAsync(fixture.ServicioId, martes, null);

            Assert.DoesNotContain("13:00", slots);
            Assert.DoesNotContain("13:30", slots);
        }

        // ─────────────── Validaciones ───────────────

        [Fact]
        public async Task Crear_ConHoraFinalMenorQueLaInicial_EsRechazado()
        {
            using var fixture = await ScheduleFixture.CreateAsync();

            var form = ScheduleFixture.BuildAlmuerzoForm();
            form.HoraInicio = new TimeOnly(14, 0);
            form.HoraFin = new TimeOnly(13, 0);

            await Assert.ThrowsAsync<RecurringScheduleValidationException>(() =>
                fixture.Schedule.CreateAsync(form, "admin", default));
        }

        [Fact]
        public async Task Crear_SinDias_EsRechazado()
        {
            using var fixture = await ScheduleFixture.CreateAsync();

            var form = ScheduleFixture.BuildAlmuerzoForm();
            form.Dias = new List<int>();

            await Assert.ThrowsAsync<RecurringScheduleValidationException>(() =>
                fixture.Schedule.CreateAsync(form, "admin", default));
        }

        [Fact]
        public async Task Crear_ConAlcanceSeleccionadoSinColaboradores_EsRechazado()
        {
            using var fixture = await ScheduleFixture.CreateAsync();

            var form = ScheduleFixture.BuildAlmuerzoForm();
            form.Alcance = RecurringScheduleScope.ColaboradoresSeleccionados;
            form.FuncionarioIds = new List<int>();

            await Assert.ThrowsAsync<RecurringScheduleValidationException>(() =>
                fixture.Schedule.CreateAsync(form, "admin", default));
        }

        [Fact]
        public async Task Crear_ConFechaFinalAnteriorALaInicial_EsRechazado()
        {
            using var fixture = await ScheduleFixture.CreateAsync();

            var form = ScheduleFixture.BuildAlmuerzoForm();
            form.VigenteDesde = new DateTime(2026, 8, 10);
            form.VigenteHasta = new DateTime(2026, 8, 1);

            await Assert.ThrowsAsync<RecurringScheduleValidationException>(() =>
                fixture.Schedule.CreateAsync(form, "admin", default));
        }

        [Fact]
        public async Task Crear_DuplicadoExacto_EsRechazado()
        {
            using var fixture = await ScheduleFixture.CreateAsync();

            await fixture.Schedule.CreateAsync(ScheduleFixture.BuildAlmuerzoForm(), "admin", default);

            var error = await Assert.ThrowsAsync<RecurringScheduleValidationException>(() =>
                fixture.Schedule.CreateAsync(ScheduleFixture.BuildAlmuerzoForm(), "admin", default));

            Assert.Contains("idéntica", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Excepcion_DuplicadaParaLaMismaFechaYColaborador_EsRechazada()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            var ruleId = await fixture.CrearAlmuerzoAsync();

            var form = new RecurringScheduleExceptionFormViewModel
            {
                RuleId = ruleId,
                FuncionarioId = fixture.FuncionarioId,
                Fecha = Lunes.ToDateTime(TimeOnly.MinValue),
                Tipo = RecurringScheduleExceptionType.Omitir
            };

            await fixture.Schedule.AddExceptionAsync(form, "admin", default);

            await Assert.ThrowsAsync<RecurringScheduleValidationException>(() =>
                fixture.Schedule.AddExceptionAsync(form, "admin", default));
        }

        [Fact]
        public async Task Excepcion_EnUnDiaQueLaReglaNoCubre_EsRechazada()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            var ruleId = await fixture.CrearAlmuerzoAsync();

            // Domingo: la regla es lunes a sábado.
            await Assert.ThrowsAsync<RecurringScheduleValidationException>(() =>
                fixture.Schedule.AddExceptionAsync(
                    new RecurringScheduleExceptionFormViewModel
                    {
                        RuleId = ruleId,
                        Fecha = new DateTime(2026, 8, 9),
                        Tipo = RecurringScheduleExceptionType.Omitir
                    },
                    "admin",
                    default));
        }

        // ─────────────── Edición y versionado ───────────────

        [Fact]
        public async Task Editar_UnaReglaVigente_CierraLaVersionAnteriorYSoloAfectaElFuturo()
        {
            using var fixture = await ScheduleFixture.CreateAsync();

            // El reloj del test está en 2026-05-26; la regla arranca antes, así que ya estuvo vigente.
            var form = ScheduleFixture.BuildAlmuerzoForm();
            form.VigenteDesde = new DateTime(2026, 1, 1);
            var resultado = await fixture.Schedule.CreateAsync(form, "admin", default);

            var cambio = ScheduleFixture.BuildAlmuerzoForm();
            cambio.Id = resultado.RuleId;
            cambio.VigenteDesde = new DateTime(2026, 1, 1);
            cambio.HoraInicio = new TimeOnly(12, 0);
            cambio.HoraFin = new TimeOnly(13, 0);
            cambio.ConfirmarConflictos = true;

            var actualizado = await fixture.Schedule.UpdateAsync(resultado.RuleId, cambio, "admin", default);

            var reglas = await fixture.Context.RecurringScheduleRules
                .OrderBy(regla => regla.Id)
                .ToListAsync();

            Assert.Equal(2, reglas.Count);

            var anterior = reglas[0];
            var nueva = reglas[1];

            Assert.NotEqual(resultado.RuleId, actualizado.RuleId);
            Assert.Equal(new TimeOnly(13, 0), anterior.HoraInicio);
            Assert.False(anterior.Activa);
            Assert.Equal(new DateOnly(2026, 5, 25), anterior.VigenteHasta);

            Assert.Equal(new TimeOnly(12, 0), nueva.HoraInicio);
            Assert.Equal(new DateOnly(2026, 5, 26), nueva.VigenteDesde);
            Assert.Equal(resultado.RuleId, nueva.ReglaOrigenId);

            Assert.True(fixture.Audit.Contains(PlatformAuditActions.RecurringScheduleRuleVersioned));
        }

        [Fact]
        public async Task Finalizar_EsUnaBajaLogicaQueConservaElHistorial()
        {
            using var fixture = await ScheduleFixture.CreateAsync();

            var form = ScheduleFixture.BuildAlmuerzoForm();
            form.VigenteDesde = new DateTime(2026, 1, 1);
            var resultado = await fixture.Schedule.CreateAsync(form, "admin", default);

            await fixture.Schedule.EndAsync(resultado.RuleId, "admin", default);

            var regla = await fixture.Context.RecurringScheduleRules.SingleAsync();

            Assert.False(regla.Activa);
            Assert.Equal(new DateOnly(2026, 5, 26), regla.VigenteHasta);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.RecurringScheduleRuleEnded));
        }

        // ─────────────── Aislamiento entre tenants ───────────────

        [Fact]
        public async Task Reglas_NoSeVenNiBloqueanEntreTenants()
        {
            using var fixture = await ScheduleFixture.CreateAsync();
            await fixture.CrearAlmuerzoAsync();

            var funcionarioId = fixture.FuncionarioId;

            fixture.TenantProvider.TenantId = Guid.NewGuid();

            var pagina = await fixture.Schedule.BuildPageAsync();
            Assert.Empty(pagina.Reglas);

            // El colaborador del otro negocio tampoco queda bloqueado.
            var resultado = await fixture.Availability.CheckAsync(
                funcionarioId, Lunes.ToDateTime(new TimeOnly(13, 0)), 30);

            Assert.True(resultado.Disponible);
        }

        /// <summary>Contexto SQLite con un colaborador, un servicio y los servicios reales del módulo.</summary>
        private sealed class ScheduleFixture : IDisposable
        {
            public required ProyectoIdentity.Datos.ApplicationDbContext Context { get; init; }

            public required Microsoft.Data.Sqlite.SqliteConnection Connection { get; init; }

            public required TestTenantProvider TenantProvider { get; init; }

            public required FakePlatformAuditService Audit { get; init; }

            public required RecurringScheduleService Schedule { get; init; }

            public required FuncionarioAvailabilityService Availability { get; init; }

            public required IBookingAvailabilityService Booking { get; init; }

            public required ICalendarCommandService Calendar { get; init; }

            public int FuncionarioId { get; init; }

            public int ServicioId { get; init; }

            public static async Task<ScheduleFixture> CreateAsync()
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
                var audit = new FakePlatformAuditService();

                var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(context, "Ana");

                var servicio = new LuxuryApp.Models.Finanzas.Servicio
                {
                    Nombre = "Corte",
                    Precio = 10_000m,
                    DuracionMinutos = 30,
                    Activo = true
                };
                context.Servicios.Add(servicio);
                await context.SaveChangesAsync();

                var availability = new FuncionarioAvailabilityService(context);

                return new ScheduleFixture
                {
                    Context = context,
                    Connection = connection,
                    TenantProvider = tenantProvider,
                    Audit = audit,
                    Availability = availability,
                    Schedule = new RecurringScheduleService(
                        context,
                        ControllerTestSupport.BusinessDateTimeProvider,
                        audit,
                        NullLogger<RecurringScheduleService>.Instance),
                    Booking = ControllerTestSupport.CreateBookingAvailabilityService(context),
                    Calendar = ControllerTestSupport.CreateCalendarCommandService(context),
                    FuncionarioId = funcionario.IdFuncionario,
                    ServicioId = servicio.Id
                };
            }

            public static RecurringScheduleRuleFormViewModel BuildAlmuerzoForm() => new()
            {
                Nombre = "Almuerzo",
                EtiquetaCalendario = "Almuerzo",
                HoraInicio = new TimeOnly(13, 0),
                HoraFin = new TimeOnly(14, 0),
                Dias = new List<int> { 1, 2, 3, 4, 5, 6 },
                VigenteDesde = new DateTime(2026, 8, 1),
                Activa = true,
                Alcance = RecurringScheduleScope.TodosLosColaboradores,
                IncluirNuevosColaboradores = true
            };

            public async Task<int> CrearAlmuerzoAsync()
            {
                var resultado = await Schedule.CreateAsync(BuildAlmuerzoForm(), "admin", default);
                return resultado.RuleId;
            }

            public async Task SeedBookingSettingsAsync()
            {
                // TenantBookingSettings tiene FK 1:1 contra Tenants: la fila del negocio debe existir.
                if (!await Context.Tenants.IgnoreQueryFilters()
                        .AnyAsync(tenant => tenant.Id == TenantProvider.TenantId))
                {
                    Context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant
                    {
                        Id = TenantProvider.TenantId,
                        Nombre = "Tenant Test",
                        Activo = true
                    });

                    await Context.SaveChangesAsync();
                }

                Context.TenantBookingSettings.Add(new TenantBookingSettings
                {
                    PublicBookingEnabled = true,
                    PublicBookingSlug = $"tenant-{Guid.NewGuid():N}"[..20],
                    PublicBookingMinAdvanceMinutes = 0,
                    // La ventana debe cubrir agosto 2026 desde el reloj fijo del test (mayo 2026).
                    PublicBookingMaxDaysAhead = 200,
                    OpenTime = new TimeOnly(8, 0),
                    CloseTime = new TimeOnly(18, 0),
                    SlotIntervalMinutes = 30,
                    WorkingDaysMask = TenantBookingSettings.DefaultWorkingDaysMask
                });

                await Context.SaveChangesAsync();
            }

            public async Task<int> SeedCitaAsync(DateTime fechaHora)
            {
                var cita = new Cita
                {
                    NombreCliente = "Cliente existente",
                    FuncionarioId = FuncionarioId,
                    ServicioId = ServicioId,
                    FechaHoraCita = fechaHora,
                    Tipo = "CITA"
                };

                Context.Citas.Add(cita);
                await Context.SaveChangesAsync();

                return cita.Id;
            }

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }
    }
}
