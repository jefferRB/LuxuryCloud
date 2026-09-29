using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Horarios;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Horarios;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.Horarios
{
    /// <summary>
    /// Descanso aplicado a VARIOS colaboradores y repetido en VARIAS fechas.
    ///
    /// <para>
    /// Lo que se protege acá: que el descanso múltiple siga siendo N filas <c>Cita</c> del modelo
    /// de siempre, que la operación sea todo-o-nada, y que la disponibilidad se decida con la
    /// MISMA regla que usa el resto del calendario (citas, descansos, bloqueos recurrentes y
    /// solicitudes de reserva pendientes).
    /// </para>
    /// </summary>
    public class DescansoMultipleTests
    {
        // Jueves 2026-09-17, la fecha base de los ejemplos.
        private static readonly DateOnly FechaBase = new(2026, 9, 17);
        private static readonly TimeOnly HoraBase = new(9, 45);

        // ─────────────────────────────────────────────────────────────────────────────────────
        // FUNCIONARIOS
        // ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Descanso_ConVariosFuncionarios_CreaUnaEntradaPorCadaUno()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            await fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                new[] { fixture.JamieId, fixture.DrewId, fixture.CaseyId }));

            fixture.Context.ChangeTracker.Clear();

            var descansos = await fixture.Context.Citas.AsNoTracking()
                .Where(cita => cita.Tipo == "DESCANSO")
                .ToListAsync();

            Assert.Equal(3, descansos.Count);
            Assert.Equal(
                new[] { fixture.JamieId, fixture.DrewId, fixture.CaseyId }.OrderBy(id => id),
                descansos.Select(descanso => descanso.FuncionarioId).OrderBy(id => id));

            // Mismo modelo de siempre: sin cliente, sin servicio y con duración explícita.
            Assert.All(descansos, descanso =>
            {
                Assert.Equal(FechaBase.ToDateTime(HoraBase), descanso.FechaHoraCita);
                Assert.Equal(30, descanso.DuracionMinutos);
                Assert.Null(descanso.ServicioId);
                Assert.Null(descanso.ClienteId);
            });
        }

        [Fact]
        public async Task Descanso_SinFuncionarios_EsRechazado()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(new CalendarUpsertRequest
                {
                    Tipo = "DESCANSO",
                    FechaHoraCita = FechaBase.ToDateTime(HoraBase),
                    DuracionMinutos = 30,
                    FuncionarioIds = Array.Empty<int>()
                }));

            Assert.Contains("funcionario", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await fixture.Context.Citas.CountAsync());
        }

        [Fact]
        public async Task Descanso_ConFuncionarioDeOtroTenant_NoCreaNada()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // Id inexistente para este tenant: el DbContext está filtrado por tenant, así que un
            // id ajeno simplemente no aparece y la operación completa se rechaza.
            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                    new[] { fixture.JamieId, 999_999 })));

            Assert.Contains("no pertenece al tenant actual", error.Message);
            Assert.Equal(0, await fixture.Context.Citas.CountAsync());
        }

        [Fact]
        public async Task Descanso_ConFuncionarioInactivo_NoCreaNada()
        {
            using var fixture = await DescansoFixture.CreateAsync();
            var inactivo = await InvestorTestSupport.SeedFuncionarioAsync(
                fixture.Context, "Retirado", activo: false);

            await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                    new[] { fixture.JamieId, inactivo.IdFuncionario })));

            Assert.Equal(0, await fixture.Context.Citas.CountAsync());
        }

        [Fact]
        public async Task Descanso_ConFuncionarioRepetido_LoNormalizaSinDuplicar()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            await fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                new[] { fixture.JamieId, fixture.JamieId, fixture.DrewId }));

            fixture.Context.ChangeTracker.Clear();

            var descansos = await fixture.Context.Citas.AsNoTracking().ToListAsync();

            Assert.Equal(2, descansos.Count);
            Assert.Single(descansos, descanso => descanso.FuncionarioId == fixture.JamieId);
        }

        [Fact]
        public async Task Descanso_ConUnSoloFuncionario_SigueFuncionandoComoAntes()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // Compatibilidad: el formulario viejo mandaba FuncionarioId y nada más.
            await fixture.Calendar.CreateAsync(new CalendarUpsertRequest
            {
                Tipo = "DESCANSO",
                FechaHoraCita = FechaBase.ToDateTime(HoraBase),
                DuracionMinutos = 30,
                FuncionarioId = fixture.JamieId
            });

            var descanso = await fixture.Context.Citas.AsNoTracking().SingleAsync();

            Assert.Equal(fixture.JamieId, descanso.FuncionarioId);
            Assert.Equal("DESCANSO", descanso.Tipo);
        }

        [Fact]
        public async Task Cita_IgnoraLaListaDeFuncionarios_YSigueSiendoDeUnSolo()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // Estado arrastrado del modo descanso: una CITA nunca puede volverse múltiple.
            await fixture.Calendar.CreateAsync(new CalendarUpsertRequest
            {
                Tipo = "CITA",
                NombreCliente = "Cliente",
                ServicioId = fixture.ServicioId,
                FuncionarioId = fixture.JamieId,
                FuncionarioIds = new[] { fixture.JamieId, fixture.DrewId, fixture.CaseyId },
                FechaHoraCita = FechaBase.ToDateTime(HoraBase)
            });

            fixture.Context.ChangeTracker.Clear();

            var citas = await fixture.Context.Citas.AsNoTracking().ToListAsync();

            Assert.Single(citas);
            Assert.Equal(fixture.JamieId, citas[0].FuncionarioId);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // CONFLICTOS
        // ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Descanso_ConTodosLibres_SeCrea()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            var respuesta = await fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                new[] { fixture.JamieId, fixture.DrewId }));

            Assert.True(respuesta.Id > 0);
            Assert.Equal(2, await fixture.Context.Citas.CountAsync());
        }

        [Fact]
        public async Task Descanso_ConUnFuncionarioOcupado_RechazaLaOperacionCompleta()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // Drew tiene una cita de 09:30 a 10:30 el día de la fecha base.
            await fixture.SeedCitaAsync(fixture.DrewId, FechaBase, new TimeOnly(9, 30), 60);

            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                    new[] { fixture.JamieId, fixture.DrewId })));

            Assert.Contains("Drew", error.Message);
            Assert.Contains("09:30", error.Message);
            Assert.Contains("10:30", error.Message);

            // Jamie estaba libre, pero no se crea su descanso: es todo o nada.
            Assert.Equal(1, await fixture.Context.Citas.CountAsync());
            Assert.Equal(0, await fixture.Context.Citas.CountAsync(cita => cita.Tipo == "DESCANSO"));
        }

        [Fact]
        public async Task Descanso_ConVariosOcupados_ReportaTodosLosConflictos()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            await fixture.SeedCitaAsync(fixture.DrewId, FechaBase, new TimeOnly(9, 30), 60);
            await fixture.SeedDescansoAsync(fixture.CaseyId, FechaBase, new TimeOnly(10, 0), 30);

            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                    new[] { fixture.JamieId, fixture.DrewId, fixture.CaseyId })));

            Assert.Contains("Drew", error.Message);
            Assert.Contains("Casey", error.Message);
            Assert.Contains("una cita", error.Message);
            Assert.Contains("un descanso", error.Message);
        }

        [Fact]
        public async Task Descanso_NoRevelaDatosDelCliente()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            await fixture.SeedCitaAsync(
                fixture.DrewId, FechaBase, new TimeOnly(9, 30), 60,
                nombreCliente: "Maria Secreta", telefonoCliente: "88887777");

            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(new[] { fixture.DrewId })));

            // Para explicar por qué no se puede alcanza con el colaborador y la franja.
            Assert.DoesNotContain("Maria Secreta", error.Message);
            Assert.DoesNotContain("88887777", error.Message);
        }

        [Fact]
        public async Task Descanso_UnDescansoExistenteBloquea()
        {
            using var fixture = await DescansoFixture.CreateAsync();
            await fixture.SeedDescansoAsync(fixture.JamieId, FechaBase, HoraBase, 30);

            await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(new[] { fixture.JamieId })));
        }

        [Fact]
        public async Task Descanso_UnaSolicitudPendienteBloquea()
        {
            using var fixture = await DescansoFixture.CreateAsync();
            await fixture.SeedSolicitudPendienteAsync(fixture.JamieId, FechaBase, HoraBase, 30);

            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(new[] { fixture.JamieId })));

            Assert.Contains("reserva pendiente", error.Message);
            Assert.Equal(0, await fixture.Context.Citas.CountAsync());
        }

        [Fact]
        public async Task Descanso_UnBloqueoRecurrenteBloquea()
        {
            using var fixture = await DescansoFixture.CreateAsync();
            await fixture.CrearBloqueoRecurrenteAsync();

            // El bloqueo va de 13:00 a 14:00 de lunes a sábado.
            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                    new[] { fixture.JamieId },
                    hora: new TimeOnly(13, 30))));

            Assert.Contains("bloqueo de horario", error.Message);
            Assert.Equal(0, await fixture.Context.Citas.CountAsync());
        }

        // ── Límites de la regla de solapamiento ───────────────────────────────────────────────

        [Fact]
        public async Task Descanso_QueEmpiezaCuandoTerminaOtroBloque_NoEsConflicto()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // Cita de 09:15 a 09:45; el descanso arranca exactamente a las 09:45.
            await fixture.SeedCitaAsync(fixture.JamieId, FechaBase, new TimeOnly(9, 15), 30);

            var respuesta = await fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                new[] { fixture.JamieId }));

            Assert.True(respuesta.Id > 0);
        }

        [Fact]
        public async Task Descanso_QueTerminaCuandoEmpiezaOtroBloque_NoEsConflicto()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // El descanso va de 09:45 a 10:15 y la cita arranca a las 10:15.
            await fixture.SeedCitaAsync(fixture.JamieId, FechaBase, new TimeOnly(10, 15), 30);

            var respuesta = await fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                new[] { fixture.JamieId }));

            Assert.True(respuesta.Id > 0);
        }

        [Fact]
        public async Task Descanso_ConSolapamientoParcial_EsConflicto()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // Cita 10:00-10:30 contra descanso 09:45-10:15.
            await fixture.SeedCitaAsync(fixture.JamieId, FechaBase, new TimeOnly(10, 0), 30);

            await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(new[] { fixture.JamieId })));
        }

        [Fact]
        public async Task Descanso_QueContieneOtraCita_EsConflicto()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // Cita corta 10:00-10:10 dentro de un descanso de 09:45 a 11:45.
            await fixture.SeedCitaAsync(fixture.JamieId, FechaBase, new TimeOnly(10, 0), 10);

            await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                    new[] { fixture.JamieId },
                    duracion: 120)));
        }

        [Fact]
        public async Task Descanso_ContenidoDentroDeOtraCita_EsConflicto()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // Cita larga 09:00-11:00 que envuelve al descanso de 09:45 a 10:15.
            await fixture.SeedCitaAsync(fixture.JamieId, FechaBase, new TimeOnly(9, 0), 120);

            await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(new[] { fixture.JamieId })));
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // REPETICIÓN EN OTRAS FECHAS
        // ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Descanso_ConDosFechasRepetidasYDosFuncionarios_CreaSeisEntradas()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            await fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                new[] { fixture.JamieId, fixture.DrewId },
                repetir: new[] { "2026-09-18", "2026-09-19" }));

            fixture.Context.ChangeTracker.Clear();

            var descansos = await fixture.Context.Citas.AsNoTracking().ToListAsync();

            Assert.Equal(6, descansos.Count);

            // Misma hora y misma duración en todas las fechas.
            Assert.All(descansos, descanso =>
            {
                Assert.Equal(HoraBase, TimeOnly.FromDateTime(descanso.FechaHoraCita));
                Assert.Equal(30, descanso.DuracionMinutos);
            });

            Assert.Equal(
                new[] { new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 19) },
                descansos.Select(descanso => DateOnly.FromDateTime(descanso.FechaHoraCita))
                    .Distinct()
                    .OrderBy(fecha => fecha));
        }

        [Fact]
        public async Task Descanso_ConFechasRepetidasDuplicadas_NoCreaDuplicados()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            await fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                new[] { fixture.JamieId },
                repetir: new[] { "2026-09-18", "2026-09-18" }));

            fixture.Context.ChangeTracker.Clear();

            // Base + una sola ocurrencia del 18.
            Assert.Equal(2, await fixture.Context.Citas.CountAsync());
        }

        [Fact]
        public async Task Descanso_ConLaFechaBaseEntreLasRepetidas_CreaUnaSolaOcurrencia()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                    new[] { fixture.JamieId },
                    repetir: new[] { "2026-09-17", "2026-09-18" })));

            // La fecha base ya es la primera ocurrencia: repetirla generaría el mismo horario dos
            // veces, y el servicio lo rechaza en vez de crear un duplicado silencioso.
            Assert.Contains("2026-09-17", error.Message);
            Assert.Equal(0, await fixture.Context.Citas.CountAsync());
        }

        [Fact]
        public async Task Descanso_SiUnaFechaRepetidaChoca_NoCreaNada()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // El 19 Drew está ocupado; el 17 y el 18 están libres para ambos.
            await fixture.SeedCitaAsync(
                fixture.DrewId, new DateOnly(2026, 9, 19), new TimeOnly(9, 30), 60);

            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                    new[] { fixture.JamieId, fixture.DrewId },
                    repetir: new[] { "2026-09-18", "2026-09-19" })));

            Assert.Contains("19/09", error.Message);
            Assert.Contains("Drew", error.Message);

            // Ni siquiera las combinaciones libres del 17 y el 18 se crean.
            Assert.Equal(0, await fixture.Context.Citas.CountAsync(cita => cita.Tipo == "DESCANSO"));
        }

        [Fact]
        public async Task Descanso_ConDuracionMayor_DetectaConflictosQueNoExistianAntes()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // Cita de 10:30 a 11:00.
            await fixture.SeedCitaAsync(fixture.JamieId, FechaBase, new TimeOnly(10, 30), 30);

            // Con 30 minutos el descanso va de 09:45 a 10:15: no estorba.
            var conTreinta = await fixture.Availability.CheckManyAsync(
                new[] { fixture.JamieId }, new[] { FechaBase }, HoraBase, 30);
            Assert.True(conTreinta.Single().Disponible);

            // Al subir a 60 va de 09:45 a 10:45 y ya no cabe: la fecha deja de ser válida.
            var conSesenta = await fixture.Availability.CheckManyAsync(
                new[] { fixture.JamieId }, new[] { FechaBase }, HoraBase, 60);
            Assert.False(conSesenta.Single().Disponible);

            // Y el guardado coincide con la vista previa, que es lo que se está protegiendo.
            await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                    new[] { fixture.JamieId },
                    duracion: 60)));

            Assert.Equal(0, await fixture.Context.Citas.CountAsync(cita => cita.Tipo == "DESCANSO"));
        }

        [Fact]
        public async Task Descanso_ElMismoHorarioEnFechasDistintas_NoSePisaEntreSi()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            await fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                new[] { fixture.JamieId },
                repetir: new[] { "2026-09-18", "2026-09-19", "2026-09-22" }));

            fixture.Context.ChangeTracker.Clear();

            Assert.Equal(4, await fixture.Context.Citas.CountAsync());
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // BACKEND AUTORITATIVO
        // ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Descanso_NoConfiaEnLaVistaPrevia_YRevalidaAlGuardar()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            var funcionarios = new[] { fixture.JamieId, fixture.DrewId };
            var fechas = new[] { FechaBase };

            // 1) El navegador pregunta y recibe "libre".
            var previa = await fixture.Availability.CheckManyAsync(funcionarios, fechas, HoraBase, 30);
            Assert.True(previa.Single().Disponible);

            // 2) Otra persona agenda una cita justo encima, después de esa consulta.
            await fixture.SeedCitaAsync(fixture.DrewId, FechaBase, HoraBase, 30);

            // 3) El primero pulsa Guardar con la información vieja.
            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(funcionarios)));

            Assert.Contains("Drew", error.Message);
            Assert.Equal(0, await fixture.Context.Citas.CountAsync(cita => cita.Tipo == "DESCANSO"));
        }

        [Fact]
        public async Task Descanso_ReenviadoDosVeces_NoCreaDuplicadosSuperpuestos()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            await fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                new[] { fixture.JamieId, fixture.DrewId }));

            // El mismo envío otra vez: el descanso recién creado ahora es el conflicto.
            await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Calendar.CreateAsync(fixture.BuildDescanso(
                    new[] { fixture.JamieId, fixture.DrewId })));

            fixture.Context.ChangeTracker.Clear();
            Assert.Equal(2, await fixture.Context.Citas.CountAsync());
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // MATRIZ DE DISPONIBILIDAD (CheckManyAsync)
        // ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Matriz_UnaFechaSoloEstaDisponibleSiLoEstaParaTodos()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            // El 18 Drew tiene cita de 10:00 a 11:00; Jamie está libre.
            await fixture.SeedCitaAsync(
                fixture.DrewId, new DateOnly(2026, 9, 18), new TimeOnly(10, 0), 60);

            var soloJamie = await fixture.Availability.CheckManyAsync(
                new[] { fixture.JamieId },
                new[] { new DateOnly(2026, 9, 18) },
                new TimeOnly(10, 0),
                30);

            Assert.True(soloJamie.Single().Disponible);

            var ambos = await fixture.Availability.CheckManyAsync(
                new[] { fixture.JamieId, fixture.DrewId },
                new[] { new DateOnly(2026, 9, 18) },
                new TimeOnly(10, 0),
                30);

            var dia = ambos.Single();
            Assert.False(dia.Disponible);

            var conflicto = Assert.Single(dia.Conflictos);
            Assert.Equal(fixture.DrewId, conflicto.FuncionarioId);
            Assert.Equal("Drew", conflicto.FuncionarioNombre);
            Assert.Equal(BusyIntervalSources.Cita, conflicto.Tipo);
            Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(conflicto.Inicio));
            Assert.Equal(new TimeOnly(11, 0), TimeOnly.FromDateTime(conflicto.Fin));
        }

        [Fact]
        public async Task Matriz_DevuelveUnConflictoPorCadaFuncionarioOcupado()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            await fixture.SeedCitaAsync(fixture.DrewId, FechaBase, new TimeOnly(9, 30), 60);
            await fixture.SeedDescansoAsync(fixture.CaseyId, FechaBase, new TimeOnly(10, 0), 30);

            var resultado = await fixture.Availability.CheckManyAsync(
                new[] { fixture.JamieId, fixture.DrewId, fixture.CaseyId },
                new[] { FechaBase },
                HoraBase,
                30);

            var dia = resultado.Single();

            Assert.False(dia.Disponible);
            Assert.Equal(2, dia.Conflictos.Count);
            Assert.Contains(dia.Conflictos, c => c.FuncionarioNombre == "Drew" && c.Tipo == BusyIntervalSources.Cita);
            Assert.Contains(dia.Conflictos, c => c.FuncionarioNombre == "Casey" && c.Tipo == BusyIntervalSources.Descanso);
        }

        [Fact]
        public async Task Matriz_ResuelveVariasFechasYFuncionariosConUnaSolaPasada()
        {
            using var fixture = await DescansoFixture.CreateAsync();

            await fixture.SeedCitaAsync(
                fixture.DrewId, new DateOnly(2026, 9, 19), HoraBase, 30);

            var fechas = new[]
            {
                new DateOnly(2026, 9, 17),
                new DateOnly(2026, 9, 18),
                new DateOnly(2026, 9, 19)
            };

            var resultado = await fixture.Availability.CheckManyAsync(
                new[] { fixture.JamieId, fixture.DrewId },
                fechas,
                HoraBase,
                30);

            Assert.Equal(3, resultado.Count);
            Assert.True(resultado[0].Disponible);
            Assert.True(resultado[1].Disponible);
            Assert.False(resultado[2].Disponible);
        }

        [Fact]
        public async Task Matriz_LaMismaReglaQueElChequeoIndividual()
        {
            using var fixture = await DescansoFixture.CreateAsync();
            await fixture.SeedCitaAsync(fixture.JamieId, FechaBase, new TimeOnly(9, 15), 30);

            // 09:15-09:45 pega justo con el inicio del descanso: ninguna de las dos rutas debe
            // considerarlo conflicto. Esa identidad es lo que evita que la vista previa ofrezca
            // algo que el guardado rechaza.
            var individual = await fixture.Availability.CheckAsync(
                fixture.JamieId, FechaBase.ToDateTime(HoraBase), 30);

            var matriz = await fixture.Availability.CheckManyAsync(
                new[] { fixture.JamieId }, new[] { FechaBase }, HoraBase, 30);

            Assert.True(individual.Disponible);
            Assert.True(matriz.Single().Disponible);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────

        private sealed class DescansoFixture : IDisposable
        {
            public required ProyectoIdentity.Datos.ApplicationDbContext Context { get; init; }

            public required Microsoft.Data.Sqlite.SqliteConnection Connection { get; init; }

            public required TestTenantProvider TenantProvider { get; init; }

            public required IFuncionarioAvailabilityService Availability { get; init; }

            public required ICalendarCommandService Calendar { get; init; }

            public required RecurringScheduleService Schedule { get; init; }

            public int JamieId { get; init; }

            public int DrewId { get; init; }

            public int CaseyId { get; init; }

            public int ServicioId { get; init; }

            public static async Task<DescansoFixture> CreateAsync()
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                var jamie = await InvestorTestSupport.SeedFuncionarioAsync(context, "Jamie");
                var drew = await InvestorTestSupport.SeedFuncionarioAsync(context, "Drew");
                var casey = await InvestorTestSupport.SeedFuncionarioAsync(context, "Casey");

                var servicio = new LuxuryApp.Models.Finanzas.Servicio
                {
                    Nombre = "Corte",
                    Precio = 10_000m,
                    DuracionMinutos = 30,
                    Activo = true
                };

                context.Servicios.Add(servicio);
                await context.SaveChangesAsync();

                return new DescansoFixture
                {
                    Context = context,
                    Connection = connection,
                    TenantProvider = tenantProvider,
                    Availability = ControllerTestSupport.CreateAvailabilityService(context),
                    Calendar = ControllerTestSupport.CreateCalendarCommandService(context),
                    Schedule = new RecurringScheduleService(
                        context,
                        ControllerTestSupport.BusinessDateTimeProvider,
                        new FakePlatformAuditService(),
                        NullLogger<RecurringScheduleService>.Instance),
                    JamieId = jamie.IdFuncionario,
                    DrewId = drew.IdFuncionario,
                    CaseyId = casey.IdFuncionario,
                    ServicioId = servicio.Id
                };
            }

            public CalendarUpsertRequest BuildDescanso(
                IReadOnlyList<int> funcionarioIds,
                int duracion = 30,
                TimeOnly? hora = null,
                IReadOnlyList<string>? repetir = null) =>
                new()
                {
                    Tipo = "DESCANSO",
                    FechaHoraCita = FechaBase.ToDateTime(hora ?? HoraBase),
                    DuracionMinutos = duracion,
                    FuncionarioIds = funcionarioIds.ToArray(),
                    Duplicar = repetir is { Count: > 0 },
                    FechasDuplicadas = repetir ?? Array.Empty<string>()
                };

            public async Task<int> SeedCitaAsync(
                int funcionarioId,
                DateOnly fecha,
                TimeOnly hora,
                int duracionMinutos,
                string nombreCliente = "Cliente existente",
                string telefonoCliente = "88880000")
            {
                var cita = new Cita
                {
                    NombreCliente = nombreCliente,
                    TelefonoCliente = telefonoCliente,
                    FuncionarioId = funcionarioId,
                    ServicioId = ServicioId,
                    FechaHoraCita = fecha.ToDateTime(hora),
                    DuracionMinutos = duracionMinutos,
                    Tipo = "CITA"
                };

                Context.Citas.Add(cita);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();

                return cita.Id;
            }

            public async Task<int> SeedDescansoAsync(
                int funcionarioId,
                DateOnly fecha,
                TimeOnly hora,
                int duracionMinutos)
            {
                var descanso = new Cita
                {
                    FuncionarioId = funcionarioId,
                    FechaHoraCita = fecha.ToDateTime(hora),
                    DuracionMinutos = duracionMinutos,
                    Tipo = "DESCANSO"
                };

                Context.Citas.Add(descanso);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();

                return descanso.Id;
            }

            public async Task<int> SeedSolicitudPendienteAsync(
                int funcionarioId,
                DateOnly fecha,
                TimeOnly hora,
                int duracionMinutos)
            {
                var inicio = fecha.ToDateTime(hora);

                var solicitud = new BookingRequest
                {
                    ServicioId = ServicioId,
                    FuncionarioId = funcionarioId,
                    FuncionarioAsignadoId = funcionarioId,
                    NombreCliente = "Cliente web",
                    TelefonoCliente = "88887777",
                    FechaHoraInicioSolicitada = inicio,
                    FechaHoraFinCalculada = inicio.AddMinutes(duracionMinutos),
                    DuracionMinutos = duracionMinutos,
                    Estado = BookingRequestStates.Pending,
                    Origen = BookingRequestOrigins.PublicLink,
                    CreatedAtUtc = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc)
                };

                Context.BookingRequests.Add(solicitud);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();

                return solicitud.Id;
            }

            /// <summary>Almuerzo 13:00-14:00, lunes a sábado, para todos los colaboradores.</summary>
            public async Task<int> CrearBloqueoRecurrenteAsync()
            {
                var resultado = await Schedule.CreateAsync(
                    new RecurringScheduleRuleFormViewModel
                    {
                        Nombre = "Almuerzo",
                        EtiquetaCalendario = "Almuerzo",
                        HoraInicio = new TimeOnly(13, 0),
                        HoraFin = new TimeOnly(14, 0),
                        Dias = new List<int> { 1, 2, 3, 4, 5, 6 },
                        VigenteDesde = new DateTime(2026, 9, 1),
                        Activa = true,
                        Alcance = RecurringScheduleScope.TodosLosColaboradores,
                        IncluirNuevosColaboradores = true
                    },
                    "admin",
                    default);

                Context.ChangeTracker.Clear();
                return resultado.RuleId;
            }

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }
    }
}
