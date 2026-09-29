using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Verifica los datasets de gráficos de "Mis Ganancias" (Fase 2). Punto clave: las series
    /// se construyen reutilizando la fórmula canónica de liquidaciones, así que NO deben divergir
    /// de los KPIs existentes (la suma de comisión semanal del mes == comisión mensual del KPI).
    /// </summary>
    public class MisGananciasGraficosTests
    {
        // El proveedor de tiempo fijo de pruebas sitúa "hoy" en 2026-05-26 → mes actual = mayo 2026.

        [Fact]
        public async Task ObtenerGanancias_SerieSemanal_DebeCuadrarConComisionMensual()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            // 50% de comisión, rebajando IVA (13%) antes de comisión.
            var funcionario = await SeedFuncionarioAsync(context, "Ana", 50m);
            // Dos servicios en semanas distintas de mayo 2026.
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 5, 6), 1000m);  // semana 2
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 5, 20), 2000m); // semana 4

            var portal = CreatePortalService(context, tenantProvider);

            var model = await portal.ObtenerGananciasAsync(funcionario.IdFuncionario, null, null, 6, default);

            // Comisión mensual esperada (IVA incluido): base 3000 / 1.13 = 2654.87; * 50% = 1327.44.
            Assert.Equal(1327.44m, model.Mes.TotalEstimado);

            // La serie semanal debe existir y su comisión total debe coincidir con la mensual.
            Assert.NotEmpty(model.SemanasDelMes);
            var sumaComisionSemanal = model.SemanasDelMes.Sum(s => s.Comision);
            Assert.Equal(model.Mes.TotalEstimado, sumaComisionSemanal);

            // Comisión = Pagado + Pendiente en cada punto (no se pierde dinero en el desglose).
            foreach (var s in model.SemanasDelMes)
            {
                Assert.Equal(s.Comision, s.Pagado + s.Pendiente);
            }

            // Sin pagos registrados: todo es pendiente.
            Assert.Equal(1327.44m, model.SemanasDelMes.Sum(s => s.Pendiente));
            Assert.Equal(0m, model.SemanasDelMes.Sum(s => s.Pagado));
        }

        [Fact]
        public async Task ObtenerGanancias_EvolucionMensual_RespetaRango6o12()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Beto", 40m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 5, 10), 1000m);

            var portal = CreatePortalService(context, tenantProvider);

            var seis = await portal.ObtenerGananciasAsync(funcionario.IdFuncionario, null, null, 6, default);
            Assert.Equal(6, seis.MesesEvolucion);
            Assert.Equal(6, seis.EvolucionMensual.Count);
            // El último punto es el mes actual (mayo 2026) y refleja la comisión del mes.
            Assert.Equal(seis.Mes.TotalEstimado, seis.EvolucionMensual.Last().Comision);

            var doce = await portal.ObtenerGananciasAsync(funcionario.IdFuncionario, null, null, 12, default);
            Assert.Equal(12, doce.MesesEvolucion);
            Assert.Equal(12, doce.EvolucionMensual.Count);

            // Un valor inválido cae a 6 (defensa en el servicio).
            var invalido = await portal.ObtenerGananciasAsync(funcionario.IdFuncionario, null, null, 99, default);
            Assert.Equal(6, invalido.MesesEvolucion);
            Assert.Equal(6, invalido.EvolucionMensual.Count);
        }

        [Fact]
        public async Task ObtenerGanancias_ProduccionPorDia_CuadraConProduccionSemanal()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Caro", 50m);
            // Servicio en la semana actual (la del "hoy" fijo = 2026-05-26).
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 5, 26), 24500m);

            var portal = CreatePortalService(context, tenantProvider);
            var model = await portal.ObtenerGananciasAsync(funcionario.IdFuncionario, null, null, 6, default);

            Assert.NotEmpty(model.DetalleDiasSemana);
            // La producción diaria de la semana debe sumar exactamente la producción de servicios del KPI semanal.
            Assert.Equal(model.Semana.ProduccionServicios, model.DetalleDiasSemana.Sum(d => d.Monto));
            Assert.True(model.DetalleDiasSemana.Sum(d => d.Monto) > 0);
        }

        [Fact]
        public async Task ObtenerCobros_OpcionDeServicio_DebeIncluirPrecio()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Dina", 50m);
            // Crea un servicio activo de catálogo con precio 5000 (el cobro asociado es irrelevante aquí).
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 5, 12), 5000m);

            var portal = CreatePortalService(context, tenantProvider);
            // puedeRegistrarManual:true → se cargan las opciones de servicio para el modal de cobro manual.
            var model = await portal.ObtenerCobrosAsync(
                funcionario.IdFuncionario, 1, "mes", "", "", true, true, default);

            var opcion = Assert.Single(model.Servicios);
            Assert.Equal(5000m, opcion.Precio);
        }

        // ── Helpers ──

        private static FuncionarioPortalQueryService CreatePortalService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            LuxuryApp.Services.Tenant.ITenantProvider tenantProvider)
        {
            var liquidacion = new LiquidacionSemanalService(
                context,
                ControllerTestSupport.BusinessDateTimeProvider,
                new LuxuryApp.Services.Fiscal.TaxCalculationService(),
                new LuxuryApp.Services.Fiscal.LiquidacionFuncionarioService(),
                new LuxuryApp.Services.Fiscal.TenantFiscalConfigService(context, tenantProvider),
                ControllerTestSupport.CreateSystemCategoryService(context),
                new FakePlatformAuditService(),
                tenantProvider,
                NullLogger<LiquidacionSemanalService>.Instance);

            return new FuncionarioPortalQueryService(
                context,
                liquidacion,
                ControllerTestSupport.BusinessDateTimeProvider);
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            decimal porcentajeGanancia)
        {
            context.Puestos.Add(new Puesto { NombrePuesto = $"Puesto {nombre}", Detalle = "General", Activo = true });
            await context.SaveChangesAsync();
            var puesto = await context.Puestos.SingleAsync(p => p.NombrePuesto == $"Puesto {nombre}");

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = porcentajeGanancia,
                PorcentajeProducto = porcentajeGanancia,
                RebajarImpuestosAntesDeComision = true,
                FechaIngreso = new DateTime(2025, 1, 1),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task SeedCobroServicioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            DateTime fecha,
            decimal monto)
        {
            var servicio = new Servicio
            {
                Nombre = $"Servicio {fecha:yyyyMMddHHmmss}",
                Precio = monto,
                DuracionMinutos = 60,
                Activo = true
            };
            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();

            context.Cobros.Add(new Cobro
            {
                NombreCliente = "Cliente Test",
                FuncionarioId = funcionarioId,
                FechaCobro = fecha,
                Monto = monto,
                MetodoPago = "EFECTIVO",
                ServicioId = servicio.Id
            });
            await context.SaveChangesAsync();
        }
    }
}
