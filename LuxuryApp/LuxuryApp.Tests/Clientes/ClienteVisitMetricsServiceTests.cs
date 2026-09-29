using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Clientes;
using LuxuryApp.Tests.Support;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Clientes
{
    /// <summary>
    /// Verifica que las métricas salgan de las CITAS del historial y de nada más: ni cobros,
    /// ni ClienteVisitas, ni notas. Y que los datos de otro tenant nunca se mezclen.
    /// </summary>
    public class ClienteVisitMetricsServiceTests
    {
        private static readonly DateTime Hoy = new(2026, 9, 16, 9, 0, 0);

        [Fact]
        public async Task CasoReportado_DosCitasYDosCobrosPosteriores()
        {
            // Historial: 02/09 y 09/09. Cobros: dos el 16/09 (hoy).
            // Los cobros NO deben mover ninguna métrica.
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Reportado", "88880001");
            var funcionarioId = await SeedFuncionarioAsync(context, "Jamie");

            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 9, 2, 10, 0, 0));
            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 9, 9, 10, 0, 0));
            AddCobro(context, clienteId, funcionarioId, new DateTime(2026, 9, 16, 11, 0, 0));
            AddCobro(context, clienteId, funcionarioId, new DateTime(2026, 9, 16, 12, 0, 0));
            await context.SaveChangesAsync();

            var metricas = await CreateService(context).GetForClienteAsync(clienteId);

            Assert.Equal(2, metricas.AttendedVisits);
            Assert.Equal(new DateTime(2026, 9, 9), metricas.LastVisitDate);
            Assert.Equal(7, metricas.AverageVisitFrequencyDays);
            Assert.Equal(7, metricas.DaysSinceLastVisit);
        }

        [Fact]
        public async Task CobroPosterior_NoAdelantaLaUltimaVisita()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Cobro", "88880002");
            var funcionarioId = await SeedFuncionarioAsync(context, "Casey");

            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 9, 9, 10, 0, 0));
            await context.SaveChangesAsync();

            var antes = await CreateService(context).GetForClienteAsync(clienteId);

            AddCobro(context, clienteId, funcionarioId, new DateTime(2026, 9, 16, 11, 0, 0));
            await context.SaveChangesAsync();

            var despues = await CreateService(context).GetForClienteAsync(clienteId);

            Assert.Equal(antes, despues);
            Assert.Equal(new DateTime(2026, 9, 9), despues.LastVisitDate);
        }

        [Fact]
        public async Task CobroPosterior_NoAumentaElNumeroDeVisitas()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Conteo", "88880003");
            var funcionarioId = await SeedFuncionarioAsync(context, "Marco");

            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 9, 2, 10, 0, 0));
            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 9, 9, 10, 0, 0));
            AddCobro(context, clienteId, funcionarioId, new DateTime(2026, 9, 16, 11, 0, 0));
            await context.SaveChangesAsync();

            var metricas = await CreateService(context).GetForClienteAsync(clienteId);

            Assert.Equal(2, metricas.AttendedVisits);
        }

        [Fact]
        public async Task ClienteVisitasHeredadas_NoCuentanComoVisita()
        {
            // Las filas viejas de ClienteVisitas (incluida la que creaba el alta del cliente)
            // dejaron de ser fuente de verdad: solo mandan las citas.
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Legacy", "88880004");
            var funcionarioId = await SeedFuncionarioAsync(context, "Laura");

            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 9, 9, 10, 0, 0));
            context.ClienteVisitas.Add(new ClienteVisitas
            {
                ClienteId = clienteId,
                NumeroTelefono = "88880004",
                FechaVisita = new DateTime(2026, 9, 14)
            });
            await context.SaveChangesAsync();

            var metricas = await CreateService(context).GetForClienteAsync(clienteId);

            Assert.Equal(1, metricas.AttendedVisits);
            Assert.Equal(new DateTime(2026, 9, 9), metricas.LastVisitDate);
        }

        [Fact]
        public async Task CitaFutura_NoCuentaNiAdelantaLaUltimaVisita()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Futuro", "88880005");
            var funcionarioId = await SeedFuncionarioAsync(context, "Sofía");

            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 9, 9, 10, 0, 0));
            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 10, 20, 10, 0, 0));
            await context.SaveChangesAsync();

            var metricas = await CreateService(context).GetForClienteAsync(clienteId);

            Assert.Equal(1, metricas.AttendedVisits);
            Assert.Equal(new DateTime(2026, 9, 9), metricas.LastVisitDate);
        }

        [Fact]
        public async Task CitaCancelada_NoDejaRastro()
        {
            // Cancelar una cita es un borrado físico: tras cancelarla las métricas quedan
            // exactamente como si nunca hubiera existido.
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Cancela", "88880006");
            var funcionarioId = await SeedFuncionarioAsync(context, "Andrés");

            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 9, 2, 10, 0, 0));
            var cancelada = new Cita
            {
                ClienteId = clienteId,
                FuncionarioId = funcionarioId,
                NombreCliente = "Cliente Cancela",
                FechaHoraCita = new DateTime(2026, 9, 9, 10, 0, 0)
            };
            context.Citas.Add(cancelada);
            await context.SaveChangesAsync();

            context.Citas.Remove(cancelada);
            await context.SaveChangesAsync();

            var metricas = await CreateService(context).GetForClienteAsync(clienteId);

            Assert.Equal(1, metricas.AttendedVisits);
            Assert.Equal(new DateTime(2026, 9, 2), metricas.LastVisitDate);
            Assert.Null(metricas.AverageVisitFrequencyDays);
        }

        [Fact]
        public async Task ClienteRecienCreado_NoTieneVisitas()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Nuevo", "88880007");

            var metricas = await CreateService(context).GetForClienteAsync(clienteId);

            Assert.Equal(0, metricas.AttendedVisits);
            Assert.Null(metricas.AverageVisitFrequencyDays);
            Assert.Null(metricas.LastVisitDate);
            Assert.Null(metricas.DaysSinceLastVisit);
        }

        [Fact]
        public async Task LasCitasDeOtroTenant_NoSeMezclan()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteB = await SeedClienteAsync(context, "Cliente B", "88880008");
            var funcionarioB = await SeedFuncionarioAsync(context, "Funcionario B");
            AddCita(context, clienteB, funcionarioB, new DateTime(2026, 9, 2, 10, 0, 0));
            AddCita(context, clienteB, funcionarioB, new DateTime(2026, 9, 9, 10, 0, 0));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            // El tenant A pregunta por ese mismo id: no debe ver nada.
            tenantProvider.TenantId = tenantA;
            var metricas = await CreateService(context).GetForClienteAsync(clienteB);

            Assert.Equal(0, metricas.AttendedVisits);
            Assert.Null(metricas.LastVisitDate);
        }

        [Fact]
        public async Task LasCitasDeOtroCliente_NoCuentan()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteUno = await SeedClienteAsync(context, "Cliente Uno", "88880009");
            var clienteDos = await SeedClienteAsync(context, "Cliente Dos", "88880010");
            var funcionarioId = await SeedFuncionarioAsync(context, "Karla");

            AddCita(context, clienteUno, funcionarioId, new DateTime(2026, 9, 2, 10, 0, 0));
            AddCita(context, clienteDos, funcionarioId, new DateTime(2026, 9, 9, 10, 0, 0));
            await context.SaveChangesAsync();

            var metricas = await CreateService(context).GetForClienteAsync(clienteUno);

            Assert.Equal(1, metricas.AttendedVisits);
            Assert.Equal(new DateTime(2026, 9, 2), metricas.LastVisitDate);
        }

        // ── FRECUENCIA INICIAL vs EFECTIVA (desde BD) ─────────────────────────

        [Fact]
        public async Task SinCitas_UsaLaFrecuenciaInicialDelCliente()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Inicial", "88880011", frecuenciaInicial: 20);

            var metricas = await CreateService(context).GetForClienteAsync(clienteId);

            Assert.Equal(20, metricas.EffectiveFrequencyDays);
            Assert.Null(metricas.AverageVisitFrequencyDays);
        }

        [Fact]
        public async Task ConDosCitas_ElPromedioObservadoDesplazaALaInicial()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Patron", "88880012", frecuenciaInicial: 15);
            var funcionarioId = await SeedFuncionarioAsync(context, "Karla");

            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 9, 2, 10, 0, 0));
            AddCita(context, clienteId, funcionarioId, new DateTime(2026, 9, 9, 10, 0, 0));
            await context.SaveChangesAsync();

            var metricas = await CreateService(context).GetForClienteAsync(clienteId);

            Assert.Equal(7, metricas.EffectiveFrequencyDays);
        }

        // ── LOTE PARA EL LISTADO ──────────────────────────────────────────────

        [Fact]
        public async Task ElLote_DevuelveLoMismoQueLaConsultaIndividual()
        {
            // Es la garantía de que listado y detalle no pueden divergir.
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionarioId = await SeedFuncionarioAsync(context, "Equipo");

            var sinCitas = await SeedClienteAsync(context, "Sin citas", "88880013", frecuenciaInicial: 15);
            var unaCita = await SeedClienteAsync(context, "Una cita", "88880014", frecuenciaInicial: 30);
            var variasCitas = await SeedClienteAsync(context, "Varias citas", "88880015", frecuenciaInicial: 15);

            AddCita(context, unaCita, funcionarioId, new DateTime(2026, 9, 9, 10, 0, 0));
            AddCita(context, variasCitas, funcionarioId, new DateTime(2026, 9, 2, 10, 0, 0));
            AddCita(context, variasCitas, funcionarioId, new DateTime(2026, 9, 9, 10, 0, 0));
            AddCita(context, variasCitas, funcionarioId, new DateTime(2026, 9, 11, 10, 0, 0));
            await context.SaveChangesAsync();

            var ids = new[] { sinCitas, unaCita, variasCitas };
            var lote = await CreateService(context).GetForClientesAsync(ids);

            foreach (var id in ids)
            {
                var individual = await CreateService(context).GetForClienteAsync(id);
                Assert.Equal(individual, lote[id]);
            }

            // El caso de la captura: 02/09, 09/09 y 11/09 => intervalos 7 y 2 => 4.5 => 5.
            Assert.Equal(5, lote[variasCitas].EffectiveFrequencyDays);
            Assert.Equal(new DateTime(2026, 9, 11), lote[variasCitas].LastVisitDate);
            Assert.Equal(3, lote[variasCitas].AttendedVisits);

            Assert.Equal(15, lote[sinCitas].EffectiveFrequencyDays);
            Assert.Null(lote[sinCitas].LastVisitDate);
            Assert.Equal(30, lote[unaCita].EffectiveFrequencyDays);
        }

        [Fact]
        public async Task ElLote_NoMezclaCitasEntreClientes()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var uno = await SeedClienteAsync(context, "Uno", "88880016");
            var dos = await SeedClienteAsync(context, "Dos", "88880017");
            var funcionarioId = await SeedFuncionarioAsync(context, "Marta");

            AddCita(context, uno, funcionarioId, new DateTime(2026, 9, 2, 10, 0, 0));
            AddCita(context, dos, funcionarioId, new DateTime(2026, 9, 9, 10, 0, 0));
            await context.SaveChangesAsync();

            var lote = await CreateService(context).GetForClientesAsync(new[] { uno, dos });

            Assert.Equal(new DateTime(2026, 9, 2), lote[uno].LastVisitDate);
            Assert.Equal(new DateTime(2026, 9, 9), lote[dos].LastVisitDate);
            Assert.Equal(1, lote[uno].AttendedVisits);
            Assert.Equal(1, lote[dos].AttendedVisits);
        }

        [Fact]
        public async Task ElLote_IgnoraClientesDeOtroTenant()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteB = await SeedClienteAsync(context, "Cliente B", "88880018");
            var funcionarioB = await SeedFuncionarioAsync(context, "Funcionario B");
            AddCita(context, clienteB, funcionarioB, new DateTime(2026, 9, 9, 10, 0, 0));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            tenantProvider.TenantId = tenantA;
            var lote = await CreateService(context).GetForClientesAsync(new[] { clienteB });

            Assert.Empty(lote);
        }

        [Fact]
        public async Task ElLote_ConListaVacia_NoConsultaNada()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            Assert.Empty(await CreateService(context).GetForClientesAsync(Array.Empty<int>()));
        }

        private static ClienteVisitMetricsService CreateService(ApplicationDbContext context) =>
            new(context, new FixedBusinessDateTimeProvider(Hoy));

        private static async Task<int> SeedClienteAsync(
            ApplicationDbContext context,
            string nombre,
            string telefono,
            int frecuenciaInicial = ClienteDefaults.InitialVisitFrequencyDays)
        {
            var cliente = new ClientesModel
            {
                Nombre = nombre,
                NumeroTelefono = telefono,
                FrecuenciaVisita = frecuenciaInicial,
                // Columna legacy: se siembra a propósito con una fecha que NO es la última
                // visita real, para comprobar que las métricas no la leen.
                FechaUltimaVisita = new DateTime(2026, 1, 1)
            };

            context.Clientes.Add(cliente);
            await context.SaveChangesAsync();
            return cliente.Id;
        }

        private static async Task<int> SeedFuncionarioAsync(ApplicationDbContext context, string nombre)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                Detalle = "Atención",
                Activo = true
            };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                Activo = true,
                ColorCalendario = "#123456",
                PorcentajeGanancia = 40m,
                PorcentajeProducto = 10m,
                FechaIngreso = new DateTime(2026, 1, 1)
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario.IdFuncionario;
        }

        private static void AddCita(
            ApplicationDbContext context,
            int clienteId,
            int funcionarioId,
            DateTime fechaHora)
        {
            context.Citas.Add(new Cita
            {
                ClienteId = clienteId,
                FuncionarioId = funcionarioId,
                NombreCliente = "Cliente",
                FechaHoraCita = fechaHora
            });
        }

        private static void AddCobro(
            ApplicationDbContext context,
            int clienteId,
            int funcionarioId,
            DateTime fecha)
        {
            context.Cobros.Add(new Cobro
            {
                ClienteId = clienteId,
                FuncionarioId = funcionarioId,
                NombreCliente = "Cliente",
                FechaCobro = fecha,
                Monto = 7000m,
                MetodoPago = "EFECTIVO"
            });
        }
    }
}
