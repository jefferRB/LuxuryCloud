using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class BookingAvailabilityNextSlotsTests
    {
        // "Now" fijo del proveedor: martes 2026-05-26 10:30 (-06:00).
        private static readonly DateOnly Today = new(2026, 5, 26);
        private static readonly DateOnly Tomorrow = new(2026, 5, 27);

        [Fact]
        public async Task GetNextAvailableSlots_RespectsServiceDuration()
        {
            using var ctx = await Ctx.CreateAsync(open: new TimeOnly(8, 0), close: new TimeOnly(12, 0), interval: 30, minAdvance: 0);
            var servicio = await ctx.SeedServicioAsync(90);
            await ctx.SeedFuncionarioAsync("Deyner");

            var slots = await ctx.Service.GetNextAvailableSlotsAsync(servicio.Id, Today, funcionarioId: null, maxSuggestions: 5);

            Assert.Equal(5, slots.Count);
            // Cada sugerencia debe permitir un bloque continuo de 90 min antes del cierre (12:00 → inicio ≤ 10:30).
            Assert.All(slots, s => Assert.True(s.Hora <= new TimeOnly(10, 30)));
            // El primero es hoy a las 10:30 (única hora ≥ now 10:30 que cabe hoy).
            Assert.Equal(Today, slots[0].Fecha);
            Assert.Equal(new TimeOnly(10, 30), slots[0].Hora);
        }

        [Fact]
        public async Task GetAvailableSlots_LongService_NotShownWhereOnlyShortFits()
        {
            using var ctx = await Ctx.CreateAsync(open: new TimeOnly(8, 0), close: new TimeOnly(9, 0), interval: 30, minAdvance: 0);
            var noventa = await ctx.SeedServicioAsync(90);
            var sesenta = await ctx.SeedServicioAsync(60);
            await ctx.SeedFuncionarioAsync("Deyner");

            var largos = await ctx.Service.GetAvailableSlotsAsync(noventa.Id, Tomorrow, funcionarioId: null);
            var cortos = await ctx.Service.GetAvailableSlotsAsync(sesenta.Id, Tomorrow, funcionarioId: null);

            Assert.Empty(largos);       // 90 min no cabe en una ventana de 60 min (08:00–09:00)
            Assert.NotEmpty(cortos);    // 60 min sí cabe (08:00–09:00)
        }

        [Fact]
        public async Task GetAvailableSlots_SpecificBusy_OtherEmployeeStillFree()
        {
            using var ctx = await Ctx.CreateAsync(open: new TimeOnly(8, 0), close: new TimeOnly(12, 0), interval: 30, minAdvance: 0);
            var servicio = await ctx.SeedServicioAsync(60);
            var a = await ctx.SeedFuncionarioAsync("Ana");
            await ctx.SeedFuncionarioAsync("Bruno");

            // Ana ocupada toda la jornada de mañana; Bruno libre.
            await ctx.SeedCitaAsync(a.IdFuncionario, Tomorrow.ToDateTime(new TimeOnly(8, 0)), 240);

            var conAna = await ctx.Service.GetAvailableSlotsAsync(servicio.Id, Tomorrow, a.IdFuncionario);
            var conCualquiera = await ctx.Service.GetAvailableSlotsAsync(servicio.Id, Tomorrow, funcionarioId: null);

            Assert.Empty(conAna);           // el profesional específico no tiene espacio
            Assert.NotEmpty(conCualquiera); // pero otros compatibles sí → base de HasAvailabilityWithOtherEmployees
        }

        [Fact]
        public async Task GetAvailableSlots_IncompatibleFuncionario_ReturnsEmpty()
        {
            using var ctx = await Ctx.CreateAsync(open: new TimeOnly(8, 0), close: new TimeOnly(12, 0), interval: 30, minAdvance: 0);
            var servicio = await ctx.SeedServicioAsync(60);
            var a = await ctx.SeedFuncionarioAsync("Ana");
            var b = await ctx.SeedFuncionarioAsync("Bruno");

            // Solo Bruno atiende este servicio.
            await ctx.SeedAssignmentAsync(servicio.Id, b.IdFuncionario);

            var conAna = await ctx.Service.GetAvailableSlotsAsync(servicio.Id, Tomorrow, a.IdFuncionario);
            var conBruno = await ctx.Service.GetAvailableSlotsAsync(servicio.Id, Tomorrow, b.IdFuncionario);

            Assert.Empty(conAna);      // Ana no es compatible → sin horarios
            Assert.NotEmpty(conBruno);
        }

        [Fact]
        public async Task GetNextAvailableSlots_RespectsMaxDaysAhead()
        {
            // Máximo 0 días hacia el futuro (solo hoy) y hoy (martes) NO es laboral → sin sugerencias.
            var maskSinMartes = 0b111_1111 & ~(1 << (int)DayOfWeek.Tuesday);
            using var ctx = await Ctx.CreateAsync(
                open: new TimeOnly(8, 0), close: new TimeOnly(18, 0), interval: 30, minAdvance: 0,
                maxDaysAhead: 0, mask: maskSinMartes);
            var servicio = await ctx.SeedServicioAsync(60);
            await ctx.SeedFuncionarioAsync("Deyner");

            var slots = await ctx.Service.GetNextAvailableSlotsAsync(servicio.Id, Today, funcionarioId: null, maxSuggestions: 5);

            Assert.Empty(slots);
        }

        // ── helper fixture ──
        private sealed class Ctx : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
            public ApplicationDbContext Context { get; }
            public IBookingAvailabilityService Service { get; }

            private Ctx(ApplicationDbContext context, Microsoft.Data.Sqlite.SqliteConnection connection, IBookingAvailabilityService service)
            {
                Context = context;
                _connection = connection;
                Service = service;
            }

            public static async Task<Ctx> CreateAsync(
                TimeOnly open, TimeOnly close, int interval, int minAdvance,
                int maxDaysAhead = 30, int mask = 0b111_1111)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                if (!await context.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantProvider.TenantId))
                {
                    context.Tenants.Add(new Tenant { Id = tenantProvider.TenantId, Nombre = "Tenant Test", Activo = true });
                    await context.SaveChangesAsync();
                }

                context.TenantBookingSettings.Add(new TenantBookingSettings
                {
                    PublicBookingEnabled = true,
                    PublicBookingSlug = $"slug-{Guid.NewGuid():N}"[..18],
                    OpenTime = open,
                    CloseTime = close,
                    SlotIntervalMinutes = interval,
                    PublicBookingMinAdvanceMinutes = minAdvance,
                    PublicBookingMaxDaysAhead = maxDaysAhead,
                    WorkingDaysMask = mask
                });
                await context.SaveChangesAsync();

                var catalog = new BookingCatalogService(context);
                var service = ControllerTestSupport.CreateBookingAvailabilityService(context, new FixedBusinessDateTimeProvider(), catalog);
                return new Ctx(context, connection, service);
            }

            public async Task<Servicio> SeedServicioAsync(int duracion)
            {
                var servicio = new Servicio { Nombre = $"Servicio {Guid.NewGuid():N}", Precio = 5000m, DuracionMinutos = duracion, Activo = true };
                Context.Servicios.Add(servicio);
                await Context.SaveChangesAsync();
                return servicio;
            }

            public async Task<Funcionario> SeedFuncionarioAsync(string nombre)
            {
                var puesto = new Puesto { NombrePuesto = $"Puesto {Guid.NewGuid():N}", Detalle = "R", Activo = true };
                Context.Puestos.Add(puesto);
                await Context.SaveChangesAsync();

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
                Context.Funcionarios.Add(funcionario);
                await Context.SaveChangesAsync();
                return funcionario;
            }

            public async Task SeedCitaAsync(int funcionarioId, DateTime inicio, int duracion)
            {
                Context.Citas.Add(new Cita
                {
                    FuncionarioId = funcionarioId,
                    FechaHoraCita = inicio,
                    DuracionMinutos = duracion,
                    Tipo = "CITA",
                    NombreCliente = "Bloqueo"
                });
                await Context.SaveChangesAsync();
            }

            public async Task SeedAssignmentAsync(int servicioId, int funcionarioId)
            {
                Context.TenantBookingFuncionarioServices.Add(new TenantBookingFuncionarioService
                {
                    ServicioId = servicioId,
                    FuncionarioId = funcionarioId,
                    IsEnabled = true
                });
                await Context.SaveChangesAsync();
            }

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }
    }
}
