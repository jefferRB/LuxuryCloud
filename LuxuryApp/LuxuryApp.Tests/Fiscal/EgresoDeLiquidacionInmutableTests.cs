using LuxuryApp.Models.Finanzas;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// El egreso que nace de una liquidación es parte de esa operación financiera y no se edita
    /// ni se borra desde Egresos: hacerlo dejaría <c>Liquidacion.MontoTotal != Egreso.Monto</c>.
    /// La corrección válida es revertir el pago.
    ///
    /// <para>
    /// La defensa vive en el SERVICIO. Esconder el botón es usabilidad, no seguridad: estos tests
    /// llaman al servicio directamente, como lo haría un POST fabricado a mano.
    /// </para>
    /// </summary>
    public class EgresoDeLiquidacionInmutableTests
    {
        private const int Anio = 2026;
        private const int Mes = 8;
        private const decimal MontoCobro = 100_000m;
        private const decimal ComisionDevengada = 44_247.79m;

        [Fact]
        public async Task EditarEgresoDeLiquidacion_DebeSerRechazado()
        {
            var esc = await EscenarioConPagoAsync();
            using var c = esc.Context;
            using var cn = esc.Connection;

            var egreso = await esc.Context.Egresos.AsNoTracking().SingleAsync();
            var categoriaLibre = await FinanzasTestSupport.SeedCategoriaAsync(esc.Context, "Alquiler");

            var ex = await Assert.ThrowsAsync<EgresoValidationException>(() =>
                ControllerTestSupport.CreateEgresoService(esc.Context).ActualizarAsync(new EgresoUpdateRequest
                {
                    IdEgreso = egreso.IdEgreso,
                    FechaEgreso = egreso.FechaEgreso.AddDays(5),
                    Detalle = "Editado a mano",
                    Monto = 1m,
                    MetodoPago = "SINPE",
                    CategoriaId = categoriaLibre.Id
                }));

            Assert.Contains("revertí el pago", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Nada cambió: monto, fecha, método y categoría intactos.
            var despues = await esc.Context.Egresos.AsNoTracking().SingleAsync();
            Assert.Equal(egreso.Monto, despues.Monto);
            Assert.Equal(egreso.FechaEgreso, despues.FechaEgreso);
            Assert.Equal(egreso.MetodoPago, despues.MetodoPago);
            Assert.Equal(egreso.CategoriaId, despues.CategoriaId);

            // Y la reconciliación sigue cuadrando.
            var liquidacion = await esc.Context.LiquidacionesSemanales.AsNoTracking().SingleAsync();
            Assert.Equal(liquidacion.MontoTotal, despues.Monto);
        }

        [Fact]
        public async Task EliminarEgresoDeLiquidacion_DebeSerRechazado()
        {
            var esc = await EscenarioConPagoAsync();
            using var c = esc.Context;
            using var cn = esc.Connection;

            var egreso = await esc.Context.Egresos.AsNoTracking().SingleAsync();

            var ex = await Assert.ThrowsAsync<EgresoValidationException>(() =>
                ControllerTestSupport.CreateEgresoService(esc.Context).EliminarAsync(egreso.IdEgreso));

            Assert.Contains("revertí el pago", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await esc.Context.Egresos.ToListAsync());
            Assert.Single(await esc.Context.LiquidacionesSemanales.ToListAsync());
        }

        [Fact]
        public async Task EgresoNormal_SigueSiendoEditable()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var c = context;
            using var cn = connection;

            var categoria = await FinanzasTestSupport.SeedCategoriaAsync(context, "Alquiler");
            var service = ControllerTestSupport.CreateEgresoService(context);

            await service.RegistrarAsync(new EgresoCreateRequest
            {
                FechaEgreso = new DateTime(Anio, Mes, 4, 9, 0, 0),
                Detalle = "Alquiler agosto",
                Monto = 150_000m,
                MetodoPago = "SINPE",
                CategoriaId = categoria.Id
            });

            var egreso = await context.Egresos.AsNoTracking().SingleAsync();
            var otra = await FinanzasTestSupport.SeedCategoriaAsync(context, "Servicios públicos");

            var actualizado = await service.ActualizarAsync(new EgresoUpdateRequest
            {
                IdEgreso = egreso.IdEgreso,
                FechaEgreso = new DateTime(Anio, Mes, 6, 9, 0, 0),
                Detalle = "Alquiler agosto (corregido)",
                Monto = 155_000m,
                MetodoPago = "EFECTIVO",
                CategoriaId = otra.Id
            });

            Assert.True(actualizado);

            var despues = await context.Egresos.AsNoTracking().SingleAsync();
            Assert.Equal(155_000m, despues.Monto);
            Assert.Equal("EFECTIVO", despues.MetodoPago);
            Assert.Equal(otra.Id, despues.CategoriaId);
        }

        [Fact]
        public async Task EgresoNormal_SigueSiendoEliminable()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var c = context;
            using var cn = connection;

            var categoria = await FinanzasTestSupport.SeedCategoriaAsync(context, "Alquiler");
            var service = ControllerTestSupport.CreateEgresoService(context);

            await service.RegistrarAsync(new EgresoCreateRequest
            {
                FechaEgreso = new DateTime(Anio, Mes, 4, 9, 0, 0),
                Detalle = "Gasto puntual",
                Monto = 5_000m,
                MetodoPago = "EFECTIVO",
                CategoriaId = categoria.Id
            });

            var egreso = await context.Egresos.AsNoTracking().SingleAsync();

            Assert.True(await service.EliminarAsync(egreso.IdEgreso));
            Assert.Empty(await context.Egresos.ToListAsync());
        }

        [Fact]
        public async Task TrasRevertirElPago_ElEgresoYaNoExiste_YSePuedeRegistrarDeNuevo()
        {
            var esc = await EscenarioConPagoAsync();
            using var c = esc.Context;
            using var cn = esc.Connection;

            var liquidacionId = await esc.Context.LiquidacionesSemanales
                .AsNoTracking().Select(l => l.Id).SingleAsync();

            await ControllerTestSupport
                .CreateLiquidacionSemanalService(
                    esc.Context, esc.TenantProvider, ControllerTestSupport.CreatePlatformAuditService(esc.Context))
                .RevertirPagoAsync(liquidacionId, "Monto equivocado", "admin");

            Assert.Empty(await esc.Context.Egresos.ToListAsync());

            // Y el camino correcto —volver a pagar— queda disponible.
            await FinanzasTestSupport.PagarAsync(
                esc.Context, esc.TenantProvider, esc.Funcionario,
                new DateTime(Anio, Mes, 3), new DateTime(Anio, Mes, 9),
                new DateTime(Anio, Mes, 11, 10, 0, 0), ComisionDevengada,
                auditService: ControllerTestSupport.CreatePlatformAuditService(esc.Context));

            var egreso = Assert.Single(await esc.Context.Egresos.ToListAsync());
            var liquidacion = Assert.Single(await esc.Context.LiquidacionesSemanales.ToListAsync());
            Assert.Equal(liquidacion.MontoTotal, egreso.Monto);
        }

        private static async Task<(ProyectoIdentity.Datos.ApplicationDbContext Context,
            Microsoft.Data.Sqlite.SqliteConnection Connection,
            TestTenantProvider TenantProvider,
            LuxuryApp.Models.Funcionarios.Funcionario Funcionario)> EscenarioConPagoAsync()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(Anio, Mes, 5, 9, 0, 0), MontoCobro);

            await FinanzasTestSupport.PagarAsync(
                context, tenantProvider, funcionario,
                new DateTime(Anio, Mes, 3), new DateTime(Anio, Mes, 9),
                new DateTime(Anio, Mes, 10, 10, 0, 0), ComisionDevengada);

            return (context, connection, tenantProvider, funcionario);
        }
    }
}
