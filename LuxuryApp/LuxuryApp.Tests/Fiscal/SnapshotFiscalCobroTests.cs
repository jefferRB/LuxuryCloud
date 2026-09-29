using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// UNA TRANSACCIÓN FINANCIERA NUEVA ES HISTORIA, no una interpretación dinámica del catálogo.
    ///
    /// <para>
    /// El caso LIMPIEZA FACIAL lo demostró: corregir hoy el IVA de un servicio reescribía los
    /// reportes de meses ya cerrados, porque cada lectura resolvía la fiscalidad con la
    /// configuración VIGENTE. Desde esta versión el cobro congela su fiscalidad efectiva al
    /// registrarse; cambiar el catálogo mañana afecta las ventas de mañana.
    /// </para>
    ///
    /// <para>
    /// Los cobros anteriores al deploy quedan con snapshot NULL y siguen leyéndose con el catálogo
    /// actual, exactamente igual que antes: no se les inventa una historia que no podemos demostrar.
    /// </para>
    /// </summary>
    public class SnapshotFiscalCobroTests
    {
        private const decimal Monto = 7_000m;
        private static readonly DateTime Fecha = new(2026, 9, 14, 10, 0, 0);

        // 7.000 con IVA 13 % incluido.
        private const decimal BaseCon13 = 6_194.69m;
        private const decimal IvaCon13 = 805.31m;

        [Fact]
        public async Task NuevoCobro_GuardaSnapshotFiscal()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true);

            var id = await escenario.RegistrarServicioAsync(servicio.Id);
            var cobro = await escenario.CobroAsync(id);

            Assert.True(cobro.AplicaIvaSnapshot);
            Assert.Equal(13m, cobro.TarifaIvaSnapshot);
            Assert.True(cobro.PrecioIncluyeIvaSnapshot);
        }

        /// <summary>Exento explícito: el snapshot guarda la exención, no "13 % apagado por error".</summary>
        [Fact]
        public async Task NuevoCobro_ServicioExento_GuardaSnapshotExento()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Consulta", aplicaIva: false);

            var cobro = await escenario.CobroAsync(await escenario.RegistrarServicioAsync(servicio.Id));

            Assert.False(cobro.AplicaIvaSnapshot);
            // La tarifa igual se persiste: el snapshot nunca queda a medias (CK_Cobros_SnapshotFiscal).
            Assert.NotNull(cobro.TarifaIvaSnapshot);
        }

        [Fact]
        public async Task CambiarServicioDespues_NoCambiaIvaDelCobroNuevo()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true);
            await escenario.RegistrarServicioAsync(servicio.Id);

            var antes = await escenario.IngresosAsync();

            // El administrador corrige el catálogo: de hoy en adelante el servicio es exento.
            await escenario.CambiarServicioAsync(servicio.Id, aplicaIva: false);

            var despues = await escenario.IngresosAsync();

            Assert.Equal(IvaCon13, antes.TotalImpuestos);
            Assert.Equal(IvaCon13, despues.TotalImpuestos);
            Assert.Equal(BaseCon13, despues.TotalSinImpuestos);
        }

        [Fact]
        public async Task CambiarTarifaTenantDespues_NoCambiaIvaDelCobroNuevo()
        {
            using var escenario = await Escenario.CrearAsync();
            // Servicio sin override: hereda la tarifa del negocio.
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true, tarifaIva: null);
            await escenario.RegistrarServicioAsync(servicio.Id);

            await escenario.CambiarTarifaTenantAsync(4m);

            var despues = await escenario.IngresosAsync();

            // Sigue interpretándose al 13 %, que es lo que regía cuando se vendió.
            Assert.Equal(IvaCon13, despues.TotalImpuestos);
            Assert.Equal(BaseCon13, despues.TotalSinImpuestos);
        }

        [Fact]
        public async Task ServicioPersonalizado_GuardaSnapshotFiscal()
        {
            using var escenario = await Escenario.CrearAsync();

            var cobro = await escenario.CobroAsync(await escenario.RegistrarServicioPersonalizadoAsync("Corte especial"));

            // No tiene ServicioId, pero NO puede quedar legacy por eso: hereda la config del tenant.
            Assert.Null(cobro.ServicioId);
            Assert.True(cobro.AplicaIvaSnapshot);
            Assert.Equal(13m, cobro.TarifaIvaSnapshot);
            Assert.True(cobro.PrecioIncluyeIvaSnapshot);
        }

        [Fact]
        public async Task Producto_GuardaSnapshotFiscal()
        {
            using var escenario = await Escenario.CrearAsync();
            var producto = await FinanzasTestSupport.SeedProductoAsync(escenario.Context, "Cera", Monto);

            var cobro = await escenario.CobroAsync(await escenario.RegistrarProductoAsync(producto.IdProducto));

            Assert.True(cobro.AplicaIvaSnapshot);
            Assert.Equal(13m, cobro.TarifaIvaSnapshot);
            Assert.True(cobro.PrecioIncluyeIvaSnapshot);
        }

        /// <summary>
        /// Un cobro anterior al deploy (snapshot NULL) SIGUE comportándose como siempre: lo
        /// interpreta el catálogo actual. Es lo que garantiza que la migración no mueva ni un
        /// número de los periodos ya cerrados.
        /// </summary>
        [Fact]
        public async Task LegacySinSnapshot_SigueUsandoFallbackActual()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true);

            // Insertado a mano, sin pasar por CobroService: así nace un cobro legacy.
            await FinanzasTestSupport.SeedCobroServicioAsync(
                escenario.Context, escenario.Funcionario, servicio, Fecha, Monto);

            Assert.Equal(IvaCon13, (await escenario.IngresosAsync()).TotalImpuestos);

            await escenario.CambiarServicioAsync(servicio.Id, aplicaIva: false);

            // Cambió el catálogo ⇒ el legacy cambia con él. No es lo ideal, pero es EXACTAMENTE el
            // comportamiento anterior: preferimos eso a inventarle una fiscalidad que no consta.
            Assert.Equal(0m, (await escenario.IngresosAsync()).TotalImpuestos);
        }

        [Fact]
        public async Task Dashboard_NuevoCobro_UsaSnapshot()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true);
            await escenario.RegistrarServicioAsync(servicio.Id);

            await escenario.CambiarServicioAsync(servicio.Id, aplicaIva: false);

            var dashboard = await escenario.DashboardAsync();

            Assert.Equal(BaseCon13, dashboard.TotalSinImpuestos);
            Assert.Equal(IvaCon13, dashboard.TotalImpuestos);
        }

        [Fact]
        public async Task Ingresos_NuevoCobro_UsaSnapshot()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true);
            await escenario.RegistrarServicioAsync(servicio.Id);

            await escenario.CambiarServicioAsync(servicio.Id, aplicaIva: false);

            var ingresos = await escenario.IngresosAsync();

            Assert.Equal(BaseCon13, ingresos.TotalSinImpuestos);
            Assert.Equal(IvaCon13, ingresos.TotalImpuestos);
        }

        [Fact]
        public async Task Excel_NuevoCobro_UsaSnapshot()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true);
            await escenario.RegistrarServicioAsync(servicio.Id);

            await escenario.CambiarServicioAsync(servicio.Id, aplicaIva: false);

            var excel = await escenario.ExcelAsync();

            Assert.Equal(BaseCon13, excel.Resumen.TotalSinImpuestos);
            Assert.Equal(IvaCon13, excel.Resumen.TotalImpuestos);
        }

        /// <summary>
        /// La invariante de la Fase 2, ahora también sobre el snapshot: los tres módulos tienen que
        /// leer el MISMO cobro con la MISMA regla. Si uno leyera el snapshot y otro el catálogo,
        /// volveríamos a tener pantallas que se contradicen.
        /// </summary>
        [Fact]
        public async Task DashboardIngresosExcel_MismoCobroSnapshot_MismosValores()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true);
            await escenario.RegistrarServicioAsync(servicio.Id);

            await escenario.CambiarServicioAsync(servicio.Id, aplicaIva: false);
            await escenario.CambiarTarifaTenantAsync(4m);

            var dashboard = await escenario.DashboardAsync();
            var ingresos = await escenario.IngresosAsync();
            var excel = await escenario.ExcelAsync();

            Assert.Equal(dashboard.TotalSinImpuestos, ingresos.TotalSinImpuestos);
            Assert.Equal(dashboard.TotalSinImpuestos, excel.Resumen.TotalSinImpuestos);
            Assert.Equal(dashboard.TotalImpuestos, ingresos.TotalImpuestos);
            Assert.Equal(dashboard.TotalImpuestos, excel.Resumen.TotalImpuestos);
        }

        [Fact]
        public async Task RenombrarServicio_NoCambiaNombreHistoricoNuevoCobro()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true);
            await escenario.RegistrarServicioAsync(servicio.Id);

            await escenario.RenombrarServicioAsync(servicio.Id, "Corte Premium");

            Assert.Equal("Corte", (await escenario.ExcelAsync()).Filas.Single().Detalle);
        }

        [Fact]
        public async Task RenombrarProducto_NoCambiaNombreHistoricoNuevoCobro()
        {
            using var escenario = await Escenario.CrearAsync();
            var producto = await FinanzasTestSupport.SeedProductoAsync(escenario.Context, "Cera", Monto);
            await escenario.RegistrarProductoAsync(producto.IdProducto);

            var enBase = await escenario.Context.Productos.SingleAsync(p => p.IdProducto == producto.IdProducto);
            enBase.NombreProducto = "Cera Premium";
            await escenario.Context.SaveChangesAsync();

            Assert.Equal("Cera", (await escenario.ExcelAsync()).Filas.Single().Detalle);
        }

        [Fact]
        public async Task LegacySinSnapshot_SigueMostrandoNombreActual()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true);

            await FinanzasTestSupport.SeedCobroServicioAsync(
                escenario.Context, escenario.Funcionario, servicio, Fecha, Monto);

            await escenario.RenombrarServicioAsync(servicio.Id, "Corte Premium");

            Assert.Equal("Corte Premium", (await escenario.ExcelAsync()).Filas.Single().Detalle);
        }

        /// <summary>
        /// §34 — LA ESENCIA DE LA FASE 4. Dos cobros del mismo servicio, con un cambio de
        /// configuración en medio: cada uno conserva la suya.
        /// </summary>
        [Fact]
        public async Task CobroAntesYDespuesDelCambio_CadaUnoConservaSuConfiguracion()
        {
            using var escenario = await Escenario.CrearAsync();
            var servicio = await escenario.SeedServicioAsync("Corte", aplicaIva: true, tarifaIva: null);

            var cobroA = await escenario.CobroAsync(await escenario.RegistrarServicioAsync(servicio.Id));

            // Cambia la fiscalidad del negocio: 13 % → 4 %.
            await escenario.CambiarTarifaTenantAsync(4m);

            var cobroB = await escenario.CobroAsync(
                await escenario.RegistrarServicioAsync(servicio.Id, fecha: Fecha.AddDays(1)));

            Assert.Equal(13m, cobroA.TarifaIvaSnapshot);
            Assert.Equal(4m, cobroB.TarifaIvaSnapshot);

            // Y los totales reflejan las dos realidades sumadas, no una sola aplicada a todo:
            // 7.000/1,13 = 6.194,69   +   7.000/1,04 = 6.730,77
            var ingresos = await escenario.IngresosAsync();
            Assert.Equal(BaseCon13 + 6_730.77m, ingresos.TotalSinImpuestos);
        }

        private sealed class Escenario : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

            private Escenario(
                ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider,
                Funcionario funcionario)
            {
                Context = context;
                _connection = connection;
                TenantProvider = tenantProvider;
                Funcionario = funcionario;
            }

            public ApplicationDbContext Context { get; }

            public TestTenantProvider TenantProvider { get; }

            public Funcionario Funcionario { get; }

            public static async Task<Escenario> CrearAsync()
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                // Negocio con la configuración típica de CR: precios con IVA incluido al 13 %.
                context.Tenants.Add(new Tenant
                {
                    Id = tenantProvider.TenantId,
                    Nombre = "Northside Studio",
                    Activo = true,
                    PreciosIncluyenIva = true,
                    TarifaIvaPorDefecto = 13m
                });
                await context.SaveChangesAsync();

                var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Drew");

                return new Escenario(context, connection, tenantProvider, funcionario);
            }

            public Task<Servicio> SeedServicioAsync(
                string nombre, bool aplicaIva, decimal? tarifaIva = null) =>
                FinanzasTestSupport.SeedServicioAsync(Context, nombre, Monto, aplicaIva, tarifaIva);

            public Task<int> RegistrarServicioAsync(int servicioId, DateTime? fecha = null) =>
                CobroService().RegistrarAsync(new CobroCreateRequest
                {
                    FechaCobro = fecha ?? Fecha,
                    NombreCliente = "Cliente",
                    FuncionarioId = Funcionario.IdFuncionario,
                    ServicioId = servicioId,
                    Monto = Monto,
                    MetodoPago = "EFECTIVO"
                });

            public Task<int> RegistrarProductoAsync(int productoId) =>
                CobroService().RegistrarAsync(new CobroCreateRequest
                {
                    FechaCobro = Fecha,
                    NombreCliente = "Cliente",
                    FuncionarioId = Funcionario.IdFuncionario,
                    ProductoId = productoId,
                    Monto = Monto,
                    MetodoPago = "EFECTIVO"
                });

            public async Task<int> RegistrarServicioPersonalizadoAsync(string nombre)
            {
                var cita = new Models.Calendar.Cita
                {
                    NombreCliente = "Cliente",
                    FuncionarioId = Funcionario.IdFuncionario,
                    ServicioNombrePersonalizado = nombre,
                    FechaHoraCita = Fecha,
                    Tipo = "CITA",
                    DuracionMinutos = 30
                };

                Context.Citas.Add(cita);
                await Context.SaveChangesAsync();

                return await CobroService().RegistrarAsync(new CobroCreateRequest
                {
                    FechaCobro = Fecha,
                    NombreCliente = "Cliente",
                    FuncionarioId = Funcionario.IdFuncionario,
                    ServicioNombrePersonalizado = nombre,
                    CitaId = cita.Id,
                    Monto = Monto,
                    MetodoPago = "EFECTIVO"
                });
            }

            public Task<Cobro> CobroAsync(int id) =>
                Context.Cobros.AsNoTracking().SingleAsync(c => c.IdCobro == id);

            public async Task CambiarServicioAsync(int servicioId, bool aplicaIva)
            {
                var servicio = await Context.Servicios.SingleAsync(s => s.Id == servicioId);
                servicio.AplicaIva = aplicaIva;
                await Context.SaveChangesAsync();
            }

            public async Task RenombrarServicioAsync(int servicioId, string nombre)
            {
                var servicio = await Context.Servicios.SingleAsync(s => s.Id == servicioId);
                servicio.Nombre = nombre;
                await Context.SaveChangesAsync();
            }

            public async Task CambiarTarifaTenantAsync(decimal tarifa)
            {
                var tenant = await Context.Tenants.SingleAsync(t => t.Id == TenantProvider.TenantId);
                tenant.TarifaIvaPorDefecto = tarifa;
                await Context.SaveChangesAsync();
            }

            public Task<CobroIndexViewModel> IngresosAsync() =>
                ControllerTestSupport
                    .CreateCobroQueryService(Context, TenantProvider)
                    .BuildIndexViewModelAsync(FinanzasTestSupport.FiltroMes(2026, 9));

            public Task<CobroExportViewModel> ExcelAsync() =>
                ControllerTestSupport
                    .CreateCobroQueryService(Context, TenantProvider)
                    .BuildExportAsync(FinanzasTestSupport.FiltroMes(2026, 9));

            public Task<DashboardViewModel> DashboardAsync() =>
                ControllerTestSupport
                    .CreateDashboardFinancieroQueryService(Context, TenantProvider)
                    .BuildViewModelAsync(9, 2026);

            private ICobroService CobroService() =>
                ControllerTestSupport.CreateCobroService(Context, TenantProvider);

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }
    }
}
