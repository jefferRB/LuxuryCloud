using System.Globalization;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Productos;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class InformacionNegocioQueryServiceTests
    {
        [Fact]
        public async Task BuildViewModelAsync_ShouldCalculateHistoricalAndSelectedMonthMetrics()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var ana = await SeedFuncionarioAsync(context, "Ana");
            var luis = await SeedFuncionarioAsync(context, "Luis");

            var corte = await SeedServicioAsync(context, "Corte");
            var barba = await SeedServicioAsync(context, "Barba");
            var shampoo = await SeedProductoAsync(context, "Shampoo");
            var gel = await SeedProductoAsync(context, "Gel");

            await SeedCitaAsync(context, ana.IdFuncionario, corte.Id, new DateTime(2025, 2, 10, 14, 0, 0), "Cliente Fiel", "111");
            await SeedCitaAsync(context, ana.IdFuncionario, corte.Id, new DateTime(2025, 4, 7, 9, 0, 0), "Cliente Fiel", "111");

            await SeedCitaAsync(context, ana.IdFuncionario, corte.Id, new DateTime(2026, 4, 6, 9, 0, 0), "Cliente Fiel", "111");
            await SeedCitaAsync(context, ana.IdFuncionario, corte.Id, new DateTime(2026, 4, 13, 9, 0, 0), "Cliente Fiel", "111");
            await SeedCitaAsync(context, ana.IdFuncionario, barba.Id, new DateTime(2026, 4, 14, 14, 0, 0), "Cliente Medio", "222");
            await SeedCitaAsync(context, luis.IdFuncionario, corte.Id, new DateTime(2026, 4, 16, 16, 0, 0), "Cliente Nuevo", "333");
            await SeedCitaAsync(context, luis.IdFuncionario, null, new DateTime(2026, 4, 16, 18, 0, 0), "Descanso", "000", tipo: "DESCANSO");

            await SeedCitaAsync(context, luis.IdFuncionario, barba.Id, new DateTime(2026, 6, 2, 16, 0, 0), "Cliente Medio", "222");
            await SeedCitaAsync(context, ana.IdFuncionario, corte.Id, new DateTime(2026, 6, 9, 9, 0, 0), "Cliente Top", "444");

            await SeedCobroAsync(context, ana.IdFuncionario, new DateTime(2026, 4, 6, 10, 0, 0), 25m, "EFECTIVO", shampoo.IdProducto);
            await SeedCobroAsync(context, ana.IdFuncionario, new DateTime(2026, 4, 7, 10, 0, 0), 25m, "SINPE", shampoo.IdProducto);
            await SeedCobroAsync(context, luis.IdFuncionario, new DateTime(2026, 4, 8, 10, 0, 0), 18m, "TARJETA", gel.IdProducto);
            await SeedCobroAsync(context, ana.IdFuncionario, new DateTime(2026, 6, 8, 10, 0, 0), 30m, "EFECTIVO", gel.IdProducto);

            // El top de clientes se calcula desde el registro real de Clientes y sus Visitas,
            // no desde las citas. Sembramos los clientes con la cantidad de visitas que el test espera.
            await SeedClienteConVisitasAsync(context, "Cliente Fiel", "111", 4);
            await SeedClienteConVisitasAsync(context, "Cliente Medio", "222", 2);
            await SeedClienteConVisitasAsync(context, "Cliente Nuevo", "333", 1);
            await SeedClienteConVisitasAsync(context, "Cliente Top", "444", 1);

            var service = ControllerTestSupport.CreateInformacionNegocioQueryService(context);
            var model = await service.BuildViewModelAsync(4, 2026, 10);

            Assert.Equal(CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(4), model.MesMasCitas);
            Assert.Equal(5, model.TotalMesMasCitas);
            Assert.Equal(CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(2), model.MesMenosCitas);
            Assert.Equal(1, model.TotalMesMenosCitas);
            Assert.Equal("lunes", model.DiaMasOcupado);
            Assert.Equal(4, model.TotalDiaMasOcupado);
            Assert.Equal("jueves", model.DiaMasLibre);
            Assert.Equal(1, model.TotalDiaMasLibre);
            Assert.Equal("9:00", model.HoraMasOcupada);
            Assert.Equal(4d, model.PromedioHoraMasOcupada);
            Assert.Equal("14:00", model.HoraMasLibre);
            Assert.Equal(2d, model.PromedioHoraMasLibre);
            Assert.Equal("Corte", model.ServicioMasSolicitado);
            Assert.Equal(3, model.TotalServicioMasSolicitado);
            Assert.Equal("Shampoo", model.ProductoMasVendido);
            Assert.Equal(2, model.TotalProductoMasVendido);
            Assert.Equal("Gel", model.ProductoMenosVendido);
            Assert.Equal(1, model.TotalProductoMenosVendido);
            Assert.Equal("Ana", model.FuncionarioMasCitas);
            Assert.Equal(3, model.TotalFuncionarioCitas);
            Assert.Equal(new[] { 0, 0, 0, 4, 0, 2, 0, 0, 0, 0, 0, 0 }, model.CitasPorMes);
            Assert.Equal(new[] { "Ana", "Luis" }, model.FuncionariosNombres);
            Assert.Equal(new[] { 3, 1 }, model.FuncionariosCitas);
            Assert.Equal(new[] { "Corte", "Barba" }, model.ServiciosNombres);
            Assert.Equal(new[] { 3, 1 }, model.ServiciosCantidad);
            Assert.Equal(10, model.TopCantidad);
            Assert.Equal("Cliente Fiel", model.TopClientes[0].Nombre);
            Assert.Equal(4, model.TopClientes[0].TotalVisitas);
        }

        [Fact]
        public async Task BuildViewModelAsync_ShouldRespectTopParameterAndDefaultUnexpectedValues()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");

            for (var index = 1; index <= 6; index++)
            {
                for (var visit = 0; visit < index; visit++)
                {
                    await SeedCitaAsync(
                        context,
                        funcionario.IdFuncionario,
                        null,
                        new DateTime(2026, 4, 1).AddDays(index).AddHours(visit),
                        $"Cliente {index}",
                        $"55{index}");
                }

                await SeedClienteConVisitasAsync(context, $"Cliente {index}", $"55{index}", index);
            }

            var service = ControllerTestSupport.CreateInformacionNegocioQueryService(context);

            var topFive = await service.BuildViewModelAsync(4, 2026, 5);
            var topDefaulted = await service.BuildViewModelAsync(4, 2026, 999);

            Assert.Equal(5, topFive.TopCantidad);
            Assert.Equal(5, topFive.TopClientes.Count);
            Assert.Equal("Cliente 6", topFive.TopClientes[0].Nombre);
            Assert.Equal("Cliente 2", topFive.TopClientes[^1].Nombre);
            Assert.Equal(10, topDefaulted.TopCantidad);
            Assert.Equal(6, topDefaulted.TopClientes.Count);
        }

        [Fact]
        public async Task BuildViewModelAsync_ShouldRespectTenantIsolation()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionarioA = await SeedFuncionarioAsync(context, "Ana");
            var servicioA = await SeedServicioAsync(context, "Corte A");
            await SeedCitaAsync(context, funcionarioA.IdFuncionario, servicioA.Id, new DateTime(2026, 4, 7, 9, 0, 0), "Cliente A", "111");
            await SeedClienteConVisitasAsync(context, "Cliente A", "111", 1);

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();

            var funcionarioB = await SeedFuncionarioAsync(context, "Beto");
            var servicioB = await SeedServicioAsync(context, "Corte B");
            await SeedCitaAsync(context, funcionarioB.IdFuncionario, servicioB.Id, new DateTime(2026, 4, 7, 9, 0, 0), "Cliente B", "222");
            await SeedClienteConVisitasAsync(context, "Cliente B", "222", 1);

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateInformacionNegocioQueryService(context);
            var model = await service.BuildViewModelAsync(4, 2026, 10);

            Assert.Single(model.TopClientes);
            Assert.Equal("Cliente A", model.TopClientes[0].Nombre);
            Assert.Equal("Ana", model.FuncionarioMasCitas);
            Assert.Equal("Corte A", model.ServicioMasSolicitado);
            Assert.Equal(1, model.CitasPorMes[3]);
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {nombre} {Guid.NewGuid():N}",
                Detalle = "Operativo",
                Activo = true
            };

            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = 40m,
                PorcentajeProducto = 10m,
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = true
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task<Servicio> SeedServicioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre)
        {
            var servicio = new Servicio
            {
                Nombre = nombre,
                Precio = 50m,
                DuracionMinutos = 45,
                Activo = true
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<Producto> SeedProductoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre)
        {
            var producto = new Producto
            {
                NombreProducto = nombre,
                PrecioProducto = 20m,
                CantidadProducto = 10,
                Activo = true,
                FechaRegistro = new DateTime(2026, 1, 1)
            };

            context.Productos.Add(producto);
            await context.SaveChangesAsync();
            return producto;
        }

        private static async Task SeedCitaAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            int? servicioId,
            DateTime fechaHoraCita,
            string nombreCliente,
            string telefonoCliente,
            string tipo = "CITA")
        {
            context.Citas.Add(new Cita
            {
                FuncionarioId = funcionarioId,
                ServicioId = servicioId,
                FechaHoraCita = fechaHoraCita,
                NombreCliente = nombreCliente,
                TelefonoCliente = telefonoCliente,
                Tipo = tipo
            });

            await context.SaveChangesAsync();
        }

        private static async Task SeedClienteConVisitasAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            string telefono,
            int totalVisitas)
        {
            var cliente = new ClientesModel
            {
                Nombre = nombre,
                NumeroTelefono = telefono,
                FrecuenciaVisita = 1,
                FechaUltimaVisita = new DateTime(2026, 4, 1)
            };

            // Guardamos el cliente primero para que reciba su TenantId e Id antes de validar
            // las visitas; el guard de tenant valida la relacion hijo->principal en SaveChanges.
            context.Clientes.Add(cliente);
            await context.SaveChangesAsync();

            for (var index = 0; index < totalVisitas; index++)
            {
                context.Set<ClienteVisitas>().Add(new ClienteVisitas
                {
                    ClienteId = cliente.Id,
                    NumeroTelefono = telefono,
                    FechaVisita = new DateTime(2026, 4, 1).AddDays(index)
                });
            }

            await context.SaveChangesAsync();
        }

        private static async Task SeedCobroAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            DateTime fechaCobro,
            decimal monto,
            string metodoPago,
            int productoId)
        {
            context.Cobros.Add(new Cobro
            {
                FuncionarioId = funcionarioId,
                FechaCobro = fechaCobro,
                NombreCliente = "Cliente Cobro",
                Monto = monto,
                MetodoPago = metodoPago,
                ProductoId = productoId
            });

            await context.SaveChangesAsync();
        }
    }
}
