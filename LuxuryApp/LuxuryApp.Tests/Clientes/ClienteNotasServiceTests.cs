using LuxuryApp.Models.DataBase;
using LuxuryApp.Services.Clientes;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Clientes
{
    /// <summary>
    /// Notas de servicio: validación en el servidor, aislamiento entre negocios y orden del
    /// historial. El almacenamiento es <see cref="ClienteServicioRealizado"/>, la entidad que
    /// ya existía; no se creó ninguna tabla nueva.
    /// </summary>
    public class ClienteNotasServiceTests
    {
        private static readonly DateTime Hoy = new(2026, 10, 10, 9, 0, 0);

        [Fact]
        public async Task AgregarNota_GuardaEnLaEntidadExistente()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Notas", "88881001");

            var resultado = await CreateService(context)
                .AgregarNotaAsync(clienteId, "Prefiere tijera arriba.", null);

            Assert.True(resultado.Exitoso);
            Assert.Equal("Prefiere tijera arriba.", resultado.Nota!.Notas);

            var persistida = await context.ClienteServiciosRealizados
                .AsNoTracking()
                .SingleAsync(registro => registro.ClienteId == clienteId);

            Assert.Equal("Prefiere tijera arriba.", persistida.Notas);
            Assert.Equal(OrigenServicioRealizado.Manual, persistida.Origen);
            Assert.Equal(tenantId, persistida.TenantId);
            Assert.Null(persistida.CobroId);
        }

        [Fact]
        public async Task AgregarNota_RecortaEspaciosSobrantes()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Trim", "88881002");

            var resultado = await CreateService(context)
                .AgregarNotaAsync(clienteId, "   Usar tono 7.1   ", null);

            Assert.True(resultado.Exitoso);
            Assert.Equal("Usar tono 7.1", resultado.Nota!.Notas);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task AgregarNota_RechazaTextoVacio(string? texto)
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Vacio", "88881003");

            var resultado = await CreateService(context).AgregarNotaAsync(clienteId, texto, null);

            Assert.False(resultado.Exitoso);
            Assert.NotNull(resultado.Error);
            Assert.Empty(await context.ClienteServiciosRealizados.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task AgregarNota_RechazaTextoQueSuperaElLimite()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Largo", "88881004");
            var demasiadoLargo = new string('a', IClienteNotasService.NotaMaxLength + 1);

            var resultado = await CreateService(context)
                .AgregarNotaAsync(clienteId, demasiadoLargo, null);

            Assert.False(resultado.Exitoso);
            Assert.Empty(await context.ClienteServiciosRealizados.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task AgregarNota_AceptaExactamenteElLimite()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Limite", "88881005");
            var enElLimite = new string('a', IClienteNotasService.NotaMaxLength);

            var resultado = await CreateService(context).AgregarNotaAsync(clienteId, enElLimite, null);

            Assert.True(resultado.Exitoso);
        }

        [Fact]
        public async Task AgregarNota_NoPermiteEscribirSobreClienteDeOtroTenant()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteB = await SeedClienteAsync(context, "Cliente B", "88881006");
            context.ChangeTracker.Clear();

            // El tenant A envía el id de un cliente del tenant B.
            tenantProvider.TenantId = tenantA;
            var resultado = await CreateService(context)
                .AgregarNotaAsync(clienteB, "Intento cruzado", null);

            Assert.False(resultado.Exitoso);
            Assert.True(resultado.ClienteNoEncontrado);
            Assert.Empty(await context.ClienteServiciosRealizados.IgnoreQueryFilters().ToListAsync());
        }

        [Fact]
        public async Task AgregarNota_RechazaClienteInexistente()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var resultado = await CreateService(context).AgregarNotaAsync(999999, "Nota", null);

            Assert.False(resultado.Exitoso);
            Assert.True(resultado.ClienteNoEncontrado);
        }

        [Fact]
        public async Task AgregarNota_IgnoraFuncionarioQueNoPerteneceAlTenant()
        {
            // Un id de colaborador inventado no debe atribuir la nota a nadie.
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Autor", "88881007");

            var resultado = await CreateService(context)
                .AgregarNotaAsync(clienteId, "Nota sin autor válido", 424242);

            Assert.True(resultado.Exitoso);
            Assert.Null(resultado.Nota!.NombreFuncionario);

            var persistida = await context.ClienteServiciosRealizados
                .AsNoTracking()
                .SingleAsync(registro => registro.ClienteId == clienteId);

            Assert.Null(persistida.FuncionarioId);
        }

        [Fact]
        public async Task GetNotas_DevuelveLaMasRecientePrimero()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Orden", "88881008");

            AddNota(context, clienteId, "Primera", new DateTime(2026, 9, 1, 10, 0, 0));
            AddNota(context, clienteId, "Segunda", new DateTime(2026, 9, 20, 10, 0, 0));
            AddNota(context, clienteId, "Tercera", new DateTime(2026, 10, 5, 10, 0, 0));
            await context.SaveChangesAsync();

            var notas = await CreateService(context).GetNotasAsync(clienteId);

            Assert.Equal(new[] { "Tercera", "Segunda", "Primera" }, notas.Select(n => n.Notas));
        }

        [Fact]
        public async Task GetNotas_NoDevuelveNotasDeOtroCliente()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteUno = await SeedClienteAsync(context, "Cliente Uno", "88881009");
            var clienteDos = await SeedClienteAsync(context, "Cliente Dos", "88881010");

            AddNota(context, clienteUno, "De uno", new DateTime(2026, 9, 1, 10, 0, 0));
            AddNota(context, clienteDos, "De dos", new DateTime(2026, 9, 2, 10, 0, 0));
            await context.SaveChangesAsync();

            var notas = await CreateService(context).GetNotasAsync(clienteUno);

            Assert.Equal("De uno", Assert.Single(notas).Notas);
        }

        [Fact]
        public async Task GetNotas_NoDevuelveNotasDeOtroTenant()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteB = await SeedClienteAsync(context, "Cliente B", "88881011");
            AddNota(context, clienteB, "Secreto de B", new DateTime(2026, 9, 1, 10, 0, 0));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            tenantProvider.TenantId = tenantA;
            var notas = await CreateService(context).GetNotasAsync(clienteB);

            Assert.Empty(notas);
        }

        [Fact]
        public async Task AgregarNota_NoCuentaComoVisitaAtendida()
        {
            // Escribir una nota no es atender a nadie: las métricas no se mueven.
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Nota", "88881012");
            await CreateService(context).AgregarNotaAsync(clienteId, "Le gusta degradado bajo.", null);

            var metricas = await new ClienteVisitMetricsService(
                    context,
                    new FixedBusinessDateTimeProvider(Hoy))
                .GetForClienteAsync(clienteId);

            Assert.Equal(0, metricas.AttendedVisits);
            Assert.Null(metricas.LastVisitDate);
        }

        // ── EDICIÓN ───────────────────────────────────────────────────────────

        [Fact]
        public async Task ActualizarNota_CambiaElTextoDeLaNotaExistente()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Editar", "88882001");
            var creada = await CreateService(context).AgregarNotaAsync(clienteId, "Texto viejo", null);
            var notaId = creada.Nota!.Id;
            context.ChangeTracker.Clear();

            var resultado = await CreateService(context)
                .ActualizarNotaAsync(clienteId, notaId, "Texto nuevo");

            Assert.True(resultado.Exitoso);
            Assert.Equal("Texto nuevo", resultado.Nota!.Notas);

            // Se modifica la MISMA fila, no se crea otra.
            var persistidas = await context.ClienteServiciosRealizados.AsNoTracking().ToListAsync();
            Assert.Equal("Texto nuevo", Assert.Single(persistidas).Notas);
            Assert.Equal(notaId, persistidas[0].Id);
        }

        [Fact]
        public async Task ActualizarNota_RecortaEspaciosYRechazaTextoVacio()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Editar Vacio", "88882002");
            var creada = await CreateService(context).AgregarNotaAsync(clienteId, "Original", null);
            var notaId = creada.Nota!.Id;
            context.ChangeTracker.Clear();

            var vacio = await CreateService(context).ActualizarNotaAsync(clienteId, notaId, "   ");
            Assert.False(vacio.Exitoso);

            context.ChangeTracker.Clear();
            var conEspacios = await CreateService(context)
                .ActualizarNotaAsync(clienteId, notaId, "  Texto limpio  ");

            Assert.True(conEspacios.Exitoso);
            Assert.Equal("Texto limpio", conEspacios.Nota!.Notas);
        }

        [Fact]
        public async Task ActualizarNota_RechazaTextoQueSuperaElLimite()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Editar Largo", "88882003");
            var creada = await CreateService(context).AgregarNotaAsync(clienteId, "Original", null);
            var notaId = creada.Nota!.Id;
            context.ChangeTracker.Clear();

            var demasiadoLargo = new string('a', IClienteNotasService.NotaMaxLength + 1);
            var resultado = await CreateService(context)
                .ActualizarNotaAsync(clienteId, notaId, demasiadoLargo);

            Assert.False(resultado.Exitoso);

            var persistida = await context.ClienteServiciosRealizados.AsNoTracking().SingleAsync();
            Assert.Equal("Original", persistida.Notas);
        }

        [Fact]
        public async Task ActualizarNota_DeOtroTenant_NoSeEncuentra()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteB = await SeedClienteAsync(context, "Cliente B", "88882004");
            var creada = await CreateService(context).AgregarNotaAsync(clienteB, "Secreto de B", null);
            var notaId = creada.Nota!.Id;
            context.ChangeTracker.Clear();

            tenantProvider.TenantId = tenantA;
            var resultado = await CreateService(context)
                .ActualizarNotaAsync(clienteB, notaId, "Intento cruzado");

            Assert.False(resultado.Exitoso);
            Assert.True(resultado.ClienteNoEncontrado);

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();
            var persistida = await context.ClienteServiciosRealizados.AsNoTracking().SingleAsync();
            Assert.Equal("Secreto de B", persistida.Notas);
        }

        [Fact]
        public async Task ActualizarNota_DeOtroCliente_NoSeEncuentra()
        {
            // El id de la nota es válido dentro del tenant, pero pertenece a otro cliente:
            // la nota se busca acotada por cliente, así que no se alcanza.
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteUno = await SeedClienteAsync(context, "Cliente Uno", "88882005");
            var clienteDos = await SeedClienteAsync(context, "Cliente Dos", "88882006");
            var creada = await CreateService(context).AgregarNotaAsync(clienteUno, "De uno", null);
            context.ChangeTracker.Clear();

            var resultado = await CreateService(context)
                .ActualizarNotaAsync(clienteDos, creada.Nota!.Id, "Robada");

            Assert.False(resultado.Exitoso);
            Assert.True(resultado.ClienteNoEncontrado);

            var persistida = await context.ClienteServiciosRealizados.AsNoTracking().SingleAsync();
            Assert.Equal("De uno", persistida.Notas);
        }

        [Fact]
        public async Task ActualizarNota_Inexistente_DevuelveResultadoControlado()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Fantasma", "88882007");

            var resultado = await CreateService(context)
                .ActualizarNotaAsync(clienteId, 999999, "Texto");

            Assert.False(resultado.Exitoso);
            Assert.True(resultado.ClienteNoEncontrado);
            Assert.Equal("La nota ya no existe.", resultado.Error);
        }

        // ── ELIMINACIÓN ───────────────────────────────────────────────────────

        [Fact]
        public async Task EliminarNota_BorraSoloEsaNota()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Borrar", "88882008");
            AddNota(context, clienteId, "Se queda", new DateTime(2026, 9, 1, 10, 0, 0));
            await context.SaveChangesAsync();

            var creada = await CreateService(context).AgregarNotaAsync(clienteId, "Se borra", null);
            context.ChangeTracker.Clear();

            var resultado = await CreateService(context)
                .EliminarNotaAsync(clienteId, creada.Nota!.Id);

            Assert.True(resultado.Exitoso);

            var restantes = await context.ClienteServiciosRealizados.AsNoTracking().ToListAsync();
            Assert.Equal("Se queda", Assert.Single(restantes).Notas);
        }

        [Fact]
        public async Task EliminarNota_Inexistente_DevuelveResultadoControlado()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Sin Nota", "88882009");

            var resultado = await CreateService(context).EliminarNotaAsync(clienteId, 999999);

            Assert.False(resultado.Exitoso);
            Assert.True(resultado.ClienteNoEncontrado);
            Assert.Equal("La nota ya no existe.", resultado.Error);
        }

        [Fact]
        public async Task EliminarNota_DosVeces_NoLanzaExcepcion()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Doble", "88882010");
            var creada = await CreateService(context).AgregarNotaAsync(clienteId, "Una nota", null);
            var notaId = creada.Nota!.Id;
            context.ChangeTracker.Clear();

            Assert.True((await CreateService(context).EliminarNotaAsync(clienteId, notaId)).Exitoso);

            context.ChangeTracker.Clear();
            var segunda = await CreateService(context).EliminarNotaAsync(clienteId, notaId);

            Assert.False(segunda.Exitoso);
            Assert.True(segunda.ClienteNoEncontrado);
        }

        [Fact]
        public async Task EliminarNota_DeOtroTenant_NoSeEncuentra()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteB = await SeedClienteAsync(context, "Cliente B", "88882011");
            var creada = await CreateService(context).AgregarNotaAsync(clienteB, "Secreto de B", null);
            var notaId = creada.Nota!.Id;
            context.ChangeTracker.Clear();

            tenantProvider.TenantId = tenantA;
            var resultado = await CreateService(context).EliminarNotaAsync(clienteB, notaId);

            Assert.False(resultado.Exitoso);
            Assert.True(resultado.ClienteNoEncontrado);

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();
            Assert.Single(await context.ClienteServiciosRealizados.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task EliminarNota_DeOtroCliente_NoSeEncuentra()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteUno = await SeedClienteAsync(context, "Cliente Uno", "88882012");
            var clienteDos = await SeedClienteAsync(context, "Cliente Dos", "88882013");
            var creada = await CreateService(context).AgregarNotaAsync(clienteUno, "De uno", null);
            context.ChangeTracker.Clear();

            var resultado = await CreateService(context)
                .EliminarNotaAsync(clienteDos, creada.Nota!.Id);

            Assert.False(resultado.Exitoso);
            Assert.Single(await context.ClienteServiciosRealizados.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task TrasEditarYEliminar_ElOrdenSigueSiendoMasRecientePrimero()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clienteId = await SeedClienteAsync(context, "Cliente Orden CRUD", "88882014");

            AddNota(context, clienteId, "Primera", new DateTime(2026, 9, 1, 10, 0, 0));
            AddNota(context, clienteId, "Segunda", new DateTime(2026, 9, 10, 10, 0, 0));
            AddNota(context, clienteId, "Tercera", new DateTime(2026, 9, 14, 10, 0, 0));
            await context.SaveChangesAsync();

            var segundaId = (await context.ClienteServiciosRealizados
                .AsNoTracking()
                .SingleAsync(n => n.Notas == "Segunda")).Id;
            var terceraId = (await context.ClienteServiciosRealizados
                .AsNoTracking()
                .SingleAsync(n => n.Notas == "Tercera")).Id;
            context.ChangeTracker.Clear();

            // Editar no reordena: corregir el texto no cambia la fecha de la nota.
            await CreateService(context).ActualizarNotaAsync(clienteId, segundaId, "Segunda corregida");
            context.ChangeTracker.Clear();
            await CreateService(context).EliminarNotaAsync(clienteId, terceraId);
            context.ChangeTracker.Clear();

            var notas = await CreateService(context).GetNotasAsync(clienteId);

            Assert.Equal(new[] { "Segunda corregida", "Primera" }, notas.Select(n => n.Notas));
        }

        private static ClienteNotasService CreateService(ApplicationDbContext context) =>
            new(context, new FixedBusinessDateTimeProvider(Hoy));

        private static async Task<int> SeedClienteAsync(
            ApplicationDbContext context,
            string nombre,
            string telefono)
        {
            var cliente = new ClientesModel
            {
                Nombre = nombre,
                NumeroTelefono = telefono,
                FrecuenciaVisita = 15,
                FechaUltimaVisita = new DateTime(2026, 1, 1)
            };

            context.Clientes.Add(cliente);
            await context.SaveChangesAsync();
            return cliente.Id;
        }

        private static void AddNota(
            ApplicationDbContext context,
            int clienteId,
            string texto,
            DateTime fechaHora)
        {
            context.ClienteServiciosRealizados.Add(new ClienteServicioRealizado
            {
                ClienteId = clienteId,
                FechaHora = fechaHora,
                CreadoEn = fechaHora,
                Notas = texto,
                Origen = OrigenServicioRealizado.Manual
            });
        }
    }
}
