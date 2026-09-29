using System.Linq;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Productos;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class CobroQueryServiceTests
    {
        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldApplyWeekFilter_FromMondayToSunday()
        {
            var businessToday = ControllerTestSupport.BusinessDateTimeProvider.Today();
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Semana");
            var monday = StartOfWeek(businessToday);
            var sunday = monday.AddDays(6);
            var nextMonday = monday.AddDays(7);

            await SeedCobroServicioAsync(context, funcionario, "Lunes", monday.AddHours(9), 50m, "EFECTIVO", "Cliente Lunes");
            await SeedCobroServicioAsync(context, funcionario, "Domingo", sunday.AddHours(18), 60m, "EFECTIVO", "Cliente Domingo");
            await SeedCobroServicioAsync(context, funcionario, "Siguiente", nextMonday.AddHours(9), 70m, "EFECTIVO", "Cliente Siguiente");

            var queryService = ControllerTestSupport.CreateCobroQueryService(context, tenantProvider);
            var result = await queryService.BuildIndexViewModelAsync(new CobroFiltroViewModel { VistaTiempo = "semana" }, includeFilterOptions: false);

            Assert.Equal(2, result.Cobros.Count);
            Assert.Contains(result.Cobros, c => c.NombreCliente == "Cliente Lunes");
            Assert.Contains(result.Cobros, c => c.NombreCliente == "Cliente Domingo");
            Assert.DoesNotContain(result.Cobros, c => c.NombreCliente == "Cliente Siguiente");
        }

        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldApplyFuncionarioMetodoYTipoFilters()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var targetFuncionario = await SeedFuncionarioAsync(context, "Objetivo");
            var otherFuncionario = await SeedFuncionarioAsync(context, "Otro");

            await SeedCobroServicioAsync(context, targetFuncionario, "Servicio", new DateTime(2026, 4, 23, 8, 0, 0), 90m, "EFECTIVO", "Servicio");
            await SeedCobroProductoAsync(context, targetFuncionario, "Producto Target", new DateTime(2026, 4, 23, 9, 0, 0), 35m, "SINPE", "Producto Target");
            await SeedCobroProductoAsync(context, otherFuncionario, "Producto Otro", new DateTime(2026, 4, 23, 10, 0, 0), 45m, "SINPE", "Producto Otro");

            var queryService = ControllerTestSupport.CreateCobroQueryService(context, tenantProvider);
            var result = await queryService.BuildIndexViewModelAsync(new CobroFiltroViewModel
            {
                VistaTiempo = "todo",
                FuncionarioId = targetFuncionario.IdFuncionario,
                MetodoPago = "sinpe",
                MostrarServicios = false,
                MostrarProductos = true
            }, includeFilterOptions: false);

            var row = Assert.Single(result.Cobros);
            Assert.Equal("Producto Target", row.NombreCliente);
            Assert.False(row.EsServicio);
            Assert.Equal(35m, result.TotalProductos);
        }

        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldCalculateKpisConsistently()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "KPIs", porcentajeGanancia: 50m, porcentajeProducto: 10m);
            await SeedCobroServicioAsync(context, funcionario, "Servicio KPI", new DateTime(2026, 4, 23, 8, 0, 0), 100m, "EFECTIVO", "Servicio KPI");
            await SeedCobroProductoAsync(context, funcionario, "Producto KPI", new DateTime(2026, 4, 23, 9, 0, 0), 200m, "TARJETA", "Producto KPI");

            var queryService = ControllerTestSupport.CreateCobroQueryService(context, tenantProvider);
            var result = await queryService.BuildIndexViewModelAsync(new CobroFiltroViewModel { VistaTiempo = "todo" }, includeFilterOptions: false);

            Assert.Equal(100m, result.TotalServicios);
            Assert.Equal(200m, result.TotalProductos);
            Assert.Equal(300m, result.TotalGenerado);
            // IVA incluido (base = Total / 1.13): base 265.49, IVA 34.51 (antes usaba Total*13% = 39/261, incorrecto).
            Assert.Equal(265.49m, result.TotalSinImpuestos);
            Assert.Equal(34.51m, result.TotalImpuestos);
            // Comisión sobre base sin IVA: 100/1.13*50% + 200/1.13*10% ≈ 61.95.
            Assert.Equal(61.95m, Math.Round(result.PagoColaboradores, 2));
            Assert.Equal(203.54m, Math.Round(result.GananciaNegocio, 2));
            Assert.Equal(100m, result.GananciaEfectivo);
            Assert.Equal(200m, result.GananciaTarjeta);
            Assert.Equal(0m, result.GananciaSinpe);
        }

        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldPaginateRows_ButKeepKpisOverAllFiltered()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Paginado", porcentajeGanancia: 50m, porcentajeProducto: 50m);
            for (var i = 0; i < 25; i++)
            {
                await SeedCobroServicioAsync(
                    context, funcionario, $"Servicio {i}",
                    new DateTime(2026, 4, 10, 8, 0, 0).AddHours(i), 100m, "EFECTIVO", $"Cliente {i}");
            }

            var queryService = ControllerTestSupport.CreateCobroQueryService(context, tenantProvider);

            // Página 1: 20 filas, pero los KPIs suman los 25 (2500).
            var page1 = await queryService.BuildIndexViewModelAsync(
                new CobroFiltroViewModel { VistaTiempo = "todo", Page = 1, PageSize = 20 }, includeFilterOptions: false);

            Assert.Equal(20, page1.Cobros.Count);
            Assert.Equal(25, page1.TotalRegistros);
            Assert.Equal(2, page1.TotalPaginas);
            Assert.Equal(1, page1.Page);
            Assert.Equal(2500m, page1.TotalGenerado);

            // Página 2: 5 filas restantes; KPIs idénticos (no dependen de la página).
            var page2 = await queryService.BuildIndexViewModelAsync(
                new CobroFiltroViewModel { VistaTiempo = "todo", Page = 2, PageSize = 20 }, includeFilterOptions: false);

            Assert.Equal(5, page2.Cobros.Count);
            Assert.Equal(2500m, page2.TotalGenerado);

            // Cambiar page size a 50 trae todo en una página; KPIs no cambian.
            var todos = await queryService.BuildIndexViewModelAsync(
                new CobroFiltroViewModel { VistaTiempo = "todo", Page = 1, PageSize = 50 }, includeFilterOptions: false);

            Assert.Equal(25, todos.Cobros.Count);
            Assert.Equal(1, todos.TotalPaginas);
            Assert.Equal(2500m, todos.TotalGenerado);
        }

        [Fact]
        public async Task BuildExportAsync_ShouldReturnAllFilteredRows_WithFiscalBreakdown()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Export", porcentajeGanancia: 50m, porcentajeProducto: 50m);
            for (var i = 0; i < 25; i++)
            {
                await SeedCobroServicioAsync(
                    context, funcionario, $"Servicio {i}",
                    new DateTime(2026, 4, 10, 8, 0, 0).AddHours(i), 12000m, "EFECTIVO", $"Cliente {i}");
            }

            var queryService = ControllerTestSupport.CreateCobroQueryService(context, tenantProvider);
            var export = await queryService.BuildExportAsync(new CobroFiltroViewModel { VistaTiempo = "todo", Page = 1, PageSize = 20 });

            // Excel exporta TODAS las filas filtradas, no solo la página.
            Assert.Equal(25, export.Filas.Count);
            Assert.Equal(25, export.Resumen.TotalRegistros);

            // Escenario del ejemplo: 12 000 → base 10 619.47, IVA 1 380.53.
            var fila = export.Filas.First();
            Assert.Equal(12000m, fila.Monto);
            Assert.Equal(10619.47m, fila.BaseSinIva);
            Assert.Equal(1380.53m, fila.IvaIncluido);
        }

        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldRespectTenantIsolation()
        {
            var currentTenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = foreignTenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var foreignFuncionario = await SeedFuncionarioAsync(context, "Externo");
            await SeedCobroServicioAsync(context, foreignFuncionario, "Servicio Externo", new DateTime(2026, 4, 23, 8, 0, 0), 80m, "EFECTIVO", "Cliente Externo");

            tenantProvider.TenantId = currentTenantId;
            context.ChangeTracker.Clear();

            var currentFuncionario = await SeedFuncionarioAsync(context, "Interno");
            await SeedCobroServicioAsync(context, currentFuncionario, "Servicio Interno", new DateTime(2026, 4, 23, 9, 0, 0), 120m, "SINPE", "Cliente Interno");

            var queryService = ControllerTestSupport.CreateCobroQueryService(context, tenantProvider);
            var result = await queryService.BuildIndexViewModelAsync(new CobroFiltroViewModel { VistaTiempo = "todo" }, includeFilterOptions: false);

            var row = Assert.Single(result.Cobros);
            Assert.Equal("Cliente Interno", row.NombreCliente);
            Assert.Equal(120m, result.TotalGenerado);
        }

        private static DateTime StartOfWeek(DateTime today)
        {
            var diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
            return today.AddDays(-diff).Date;
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            decimal porcentajeGanancia = 40m,
            decimal porcentajeProducto = 10m)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                Detalle = "Operativo",
                Activo = true
            };

            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#333333",
                PorcentajeGanancia = porcentajeGanancia,
                PorcentajeProducto = porcentajeProducto,
                FechaIngreso = new DateTime(2026, 4, 1),
                Activo = true
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task SeedCobroServicioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Funcionario funcionario,
            string servicioNombre,
            DateTime fechaCobro,
            decimal monto,
            string metodoPago,
            string cliente)
        {
            var servicio = new Servicio
            {
                Nombre = servicioNombre,
                Precio = monto,
                DuracionMinutos = 45,
                Activo = true
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();

            context.Cobros.Add(new Cobro
            {
                FechaCobro = fechaCobro,
                NombreCliente = cliente,
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Monto = monto,
                MetodoPago = metodoPago
            });

            await context.SaveChangesAsync();
        }

        private static async Task SeedCobroProductoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Funcionario funcionario,
            string productoNombre,
            DateTime fechaCobro,
            decimal monto,
            string metodoPago,
            string cliente)
        {
            var producto = new Producto
            {
                NombreProducto = productoNombre,
                PrecioProducto = monto,
                CantidadProducto = 5,
                Activo = true,
                FechaRegistro = new DateTime(2026, 4, 1)
            };

            context.Productos.Add(producto);
            await context.SaveChangesAsync();

            context.Cobros.Add(new Cobro
            {
                FechaCobro = fechaCobro,
                NombreCliente = cliente,
                FuncionarioId = funcionario.IdFuncionario,
                ProductoId = producto.IdProducto,
                Monto = monto,
                MetodoPago = metodoPago
            });

            await context.SaveChangesAsync();
        }
    }
}
