using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// "No cobra comisión" y "no existe" son dos cosas distintas.
    ///
    /// <para>
    /// La Fase 5 usaba <c>remuneracion == default</c> como prueba de existencia del colaborador.
    /// <see cref="CobroRemuneracionEfectiva"/> es un <c>record struct</c>, así que un colaborador
    /// perfectamente válido con TODA su configuración en el valor cero —0 % de servicio, 0 % de
    /// producto, comisión sobre el total, empleado, no factura IVA, tarifa 0— era indistinguible de
    /// "no encontrado", y registrarle un cobro fallaba con "el funcionario no existe".
    /// </para>
    ///
    /// <para>
    /// Es una configuración real: una recepcionista o un asistente que produce cobros pero no gana
    /// comisión. Estos tests fijan que la presencia se determine por presencia, no por el valor.
    /// </para>
    /// </summary>
    public class FuncionarioConfiguracionCeroTests
    {
        private const decimal Monto = 10_000m;
        private static readonly DateTime Fecha = new(2026, 9, 10, 10, 0, 0);

        /// <summary>El caso de F-1: se registra igual, y su comisión es ₡0 porque así se configuró.</summary>
        [Fact]
        public async Task FuncionarioConfiguracionTodoCero_EsValidoParaRegistrarCobro()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var c = context;
            using var cn = connection;

            var funcionario = await SeedFuncionarioTodoCeroAsync(context);
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", Monto);

            var cobroId = await ControllerTestSupport.CreateCobroService(context, tenantProvider)
                .RegistrarAsync(new CobroCreateRequest
                {
                    FechaCobro = Fecha,
                    NombreCliente = "Cliente",
                    FuncionarioId = funcionario.IdFuncionario,
                    ServicioId = servicio.Id,
                    Monto = Monto,
                    MetodoPago = "EFECTIVO"
                });

            Assert.True(cobroId > 0);

            // La producción existe y vale lo que se cobró…
            var resumen = await ControllerTestSupport
                .CreateLiquidacionSemanalService(context, tenantProvider)
                .ObtenerResumenSemanaAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 15));

            var fila = Assert.Single(resumen.Funcionarios);
            Assert.Equal(Monto, fila.TotalGenerado);

            // …y la comisión es cero, que es exactamente lo configurado. No es un error.
            Assert.Equal(0m, fila.TotalAPagarColaborador);
            Assert.Equal(0m, resumen.TotalAPagarColaboradoresGeneral);
        }

        /// <summary>
        /// §7 — El snapshot del todo-cero tiene que quedar COMPLETO, no legacy: si quedara en NULL,
        /// mañana el cobro se recalcularía con la configuración vigente del colaborador.
        /// </summary>
        [Fact]
        public async Task FuncionarioTodoCero_GuardaSnapshotCompletoNoLegacy()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var c = context;
            using var cn = connection;

            var funcionario = await SeedFuncionarioTodoCeroAsync(context);
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", Monto);

            var cobroId = await ControllerTestSupport.CreateCobroService(context, tenantProvider)
                .RegistrarAsync(new CobroCreateRequest
                {
                    FechaCobro = Fecha,
                    NombreCliente = "Cliente",
                    FuncionarioId = funcionario.IdFuncionario,
                    ServicioId = servicio.Id,
                    Monto = Monto,
                    MetodoPago = "EFECTIVO"
                });

            var cobro = await context.Cobros.AsNoTracking().SingleAsync(x => x.IdCobro == cobroId);

            Assert.Equal(0m, cobro.PorcentajeServicioSnapshot);
            Assert.Equal(0m, cobro.PorcentajeProductoSnapshot);
            Assert.Equal(ComisionCalculadaSobre.TotalCobrado, cobro.ComisionCalculadaSobreSnapshot);
            Assert.Equal(TipoRelacionColaborador.Empleado, cobro.TipoRelacionColaboradorSnapshot);
            Assert.Equal(ModalidadIvaColaborador.NoFactura, cobro.ModalidadIvaColaboradorSnapshot);
            Assert.Equal(0m, cobro.TarifaIvaColaboradorSnapshot);

            // Los SEIS con valor ⇒ CK_Cobros_SnapshotRemuneracion lo considera completo.
            // Un cero es un valor, no una ausencia.
            Assert.NotNull(cobro.PorcentajeServicioSnapshot);
            Assert.NotNull(cobro.TarifaIvaColaboradorSnapshot);

            // Y sigue siendo un cobro con snapshot, no legacy: cambiar la configuración del
            // colaborador después no lo mueve.
            var enBase = await context.Funcionarios.SingleAsync(f => f.IdFuncionario == funcionario.IdFuncionario);
            enBase.PorcentajeGanancia = 60m;
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var resumen = await ControllerTestSupport
                .CreateLiquidacionSemanalService(context, tenantProvider)
                .ObtenerResumenSemanaAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 15));

            Assert.Equal(0m, resumen.TotalAPagarColaboradoresGeneral);
        }

        /// <summary>Corregir el centinela no puede hacer que un id inventado pase.</summary>
        [Fact]
        public async Task FuncionarioInexistente_SigueSiendoRechazado()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var c = context;
            using var cn = connection;

            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", Monto);

            var exception = await Assert.ThrowsAsync<CobroValidationException>(() =>
                ControllerTestSupport.CreateCobroService(context, tenantProvider)
                    .RegistrarAsync(new CobroCreateRequest
                    {
                        FechaCobro = Fecha,
                        NombreCliente = "Cliente",
                        FuncionarioId = 987654,
                        ServicioId = servicio.Id,
                        Monto = Monto,
                        MetodoPago = "EFECTIVO"
                    }));

            Assert.Equal("Cobro.FuncionarioId", exception.ModelStateKey);
            Assert.Empty(await context.Cobros.ToListAsync());
        }

        /// <summary>El fix del centinela NO puede debilitar el aislamiento entre tenants.</summary>
        [Fact]
        public async Task FuncionarioDeOtroTenant_SigueSiendoRechazado()
        {
            var tenantAjeno = Guid.NewGuid();
            var tenantPropio = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantAjeno };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var c = context;
            using var cn = connection;

            // El colaborador ajeno es ADEMÁS todo-cero: así el test cubre el cruce exacto entre
            // el caso de F-1 y el aislamiento de tenants.
            var funcionarioAjeno = await SeedFuncionarioTodoCeroAsync(context);

            tenantProvider.TenantId = tenantPropio;
            context.ChangeTracker.Clear();

            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", Monto);

            var exception = await Assert.ThrowsAsync<CobroValidationException>(() =>
                ControllerTestSupport.CreateCobroService(context, tenantProvider)
                    .RegistrarAsync(new CobroCreateRequest
                    {
                        FechaCobro = Fecha,
                        NombreCliente = "Cliente",
                        FuncionarioId = funcionarioAjeno.IdFuncionario,
                        ServicioId = servicio.Id,
                        Monto = Monto,
                        MetodoPago = "EFECTIVO"
                    }));

            Assert.Equal("Cobro.FuncionarioId", exception.ModelStateKey);
            Assert.Empty(await context.Cobros.ToListAsync());
        }

        /// <summary>
        /// Colaborador cuya configuración financiera completa coincide con <c>default</c> del
        /// record struct: los dos porcentajes en cero, los tres enums en su miembro cero y la
        /// tarifa de su factura en cero.
        /// </summary>
        private static async Task<Funcionario> SeedFuncionarioTodoCeroAsync(ApplicationDbContext context)
        {
            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(
                context,
                "Recepción",
                porcentajeServicio: 0m,
                porcentajeProducto: 0m,
                comisionSobre: ComisionCalculadaSobre.TotalCobrado,
                tipoRelacion: TipoRelacionColaborador.Empleado,
                modalidadIva: ModalidadIvaColaborador.NoFactura);

            // El helper deja la tarifa en el default de CR (13). Acá se busca el cero exacto.
            funcionario.TarifaIvaFacturaColaborador = 0m;
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            return funcionario;
        }
    }
}
