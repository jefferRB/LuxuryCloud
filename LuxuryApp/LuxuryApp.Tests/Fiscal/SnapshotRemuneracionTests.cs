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
    /// La remuneración también es HISTORIA. Si un cobro se hizo cuando el colaborador ganaba 50 %,
    /// ese cobro se liquida al 50 % para siempre, aunque mañana pase a 55 %.
    ///
    /// <para>
    /// El catálogo describe el FUTURO; el snapshot describe lo que se acordó cuando se hizo el
    /// trabajo. Antes de esta fase, subirle el porcentaje a un colaborador reescribía la planilla de
    /// todos los periodos anteriores — incluidos los ya pagados.
    /// </para>
    /// </summary>
    public class SnapshotRemuneracionTests
    {
        private const decimal Monto = 10_000m;
        private static readonly DateTime Inicio = new(2026, 9, 1);
        private static readonly DateTime Fin = new(2026, 9, 15);

        [Fact]
        public async Task NuevoCobro_GuardaSnapshotRemuneracion()
        {
            using var e = await Escenario.CrearAsync();

            var cobro = await e.CobroAsync(await e.RegistrarServicioAsync());

            Assert.Equal(50m, cobro.PorcentajeServicioSnapshot);
            Assert.Equal(11m, cobro.PorcentajeProductoSnapshot);
            Assert.Equal(ComisionCalculadaSobre.TotalCobrado, cobro.ComisionCalculadaSobreSnapshot);
            Assert.Equal(TipoRelacionColaborador.Empleado, cobro.TipoRelacionColaboradorSnapshot);
            Assert.Equal(ModalidadIvaColaborador.NoFactura, cobro.ModalidadIvaColaboradorSnapshot);
            Assert.NotNull(cobro.TarifaIvaColaboradorSnapshot);
        }

        /// <summary>
        /// §22 — Los seis van juntos. Un snapshot a medias liquidaría con el porcentaje viejo y la
        /// modalidad de IVA nueva: una cifra que no existió nunca.
        /// </summary>
        [Fact]
        public async Task SnapshotRemuneracion_NoPuedeQuedarParcial()
        {
            using var e = await Escenario.CrearAsync();
            var cobro = await e.CobroAsync(await e.RegistrarServicioAsync());

            var seis = new decimal?[]
            {
                cobro.PorcentajeServicioSnapshot,
                cobro.PorcentajeProductoSnapshot,
                cobro.ComisionCalculadaSobreSnapshot is null ? null : 1m,
                cobro.TipoRelacionColaboradorSnapshot is null ? null : 1m,
                cobro.ModalidadIvaColaboradorSnapshot is null ? null : 1m,
                cobro.TarifaIvaColaboradorSnapshot
            };

            Assert.True(seis.All(v => v is not null) || seis.All(v => v is null));

            // Y la base lo impide: dejar uno en NULL viola CK_Cobros_SnapshotRemuneracion.
            var tracked = await e.Context.Cobros.SingleAsync(c => c.IdCobro == cobro.IdCobro);
            tracked.PorcentajeProductoSnapshot = null;

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => e.Context.SaveChangesAsync());
        }

        [Fact]
        public async Task CambiarPorcentajeServicio_NoCambiaCobroAnterior()
        {
            using var e = await Escenario.CrearAsync();
            await e.RegistrarServicioAsync();

            var antes = await e.PlanillaAsync();
            await e.CambiarFuncionarioAsync(f => f.PorcentajeGanancia = 55m);
            var despues = await e.PlanillaAsync();

            Assert.Equal(5_000m, antes);      // 50 % de 10.000
            Assert.Equal(antes, despues);
        }

        [Fact]
        public async Task CambiarPorcentajeProducto_NoCambiaCobroAnterior()
        {
            using var e = await Escenario.CrearAsync();
            await e.RegistrarProductoAsync();

            var antes = await e.PlanillaAsync();
            await e.CambiarFuncionarioAsync(f => f.PorcentajeProducto = 30m);

            Assert.Equal(1_100m, antes);      // 11 % de 10.000
            Assert.Equal(antes, await e.PlanillaAsync());
        }

        [Fact]
        public async Task CobroPosterior_CapturaNuevoPorcentaje()
        {
            using var e = await Escenario.CrearAsync();
            await e.RegistrarServicioAsync(dia: 3);

            await e.CambiarFuncionarioAsync(f => f.PorcentajeGanancia = 55m);
            await e.RegistrarServicioAsync(dia: 12);

            // 50 % del primero + 55 % del segundo = 5.000 + 5.500
            Assert.Equal(10_500m, await e.PlanillaAsync());
        }

        [Fact]
        public async Task CambiarComisionCalculadaSobre_NoCambiaAnterior()
        {
            using var e = await Escenario.CrearAsync();
            await e.RegistrarServicioAsync();

            var antes = await e.PlanillaAsync();
            await e.CambiarFuncionarioAsync(f => f.ComisionCalculadaSobre = ComisionCalculadaSobre.BaseSinIva);

            Assert.Equal(antes, await e.PlanillaAsync());
        }

        [Fact]
        public async Task CambiarTipoRelacion_NoCambiaAnterior()
        {
            using var e = await Escenario.CrearAsync();
            await e.RegistrarServicioAsync();

            var antes = await e.PlanillaAsync();
            await e.CambiarFuncionarioAsync(f =>
            {
                f.TipoRelacionColaborador = TipoRelacionColaborador.Independiente;
                f.ModalidadIvaColaborador = ModalidadIvaColaborador.IvaAdicional;
                f.TarifaIvaFacturaColaborador = 13m;
            });

            Assert.Equal(antes, await e.PlanillaAsync());
        }

        [Fact]
        public async Task CambiarModalidadIvaColaborador_NoCambiaAnterior()
        {
            using var e = await Escenario.CrearAsync(
                tipoRelacion: TipoRelacionColaborador.Independiente,
                modalidadIva: ModalidadIvaColaborador.NoFactura);

            await e.RegistrarServicioAsync();

            var antes = await e.PlanillaAsync();
            await e.CambiarFuncionarioAsync(f => f.ModalidadIvaColaborador = ModalidadIvaColaborador.IvaAdicional);

            Assert.Equal(antes, await e.PlanillaAsync());
        }

        [Fact]
        public async Task CambiarTarifaIvaColaborador_NoCambiaAnterior()
        {
            using var e = await Escenario.CrearAsync(
                tipoRelacion: TipoRelacionColaborador.Independiente,
                modalidadIva: ModalidadIvaColaborador.IvaAdicional);

            await e.RegistrarServicioAsync();

            var antes = await e.PlanillaAsync();
            await e.CambiarFuncionarioAsync(f => f.TarifaIvaFacturaColaborador = 4m);

            Assert.Equal(antes, await e.PlanillaAsync());
        }

        /// <summary>
        /// §21 — Un cobro anterior al deploy sigue liquidándose con la configuración vigente del
        /// colaborador, exactamente como antes. No se le inventa una historia que no consta.
        /// </summary>
        [Fact]
        public async Task LegacySinSnapshot_SigueUsandoFuncionarioActual()
        {
            using var e = await Escenario.CrearAsync();

            // Insertado a mano: así nace un cobro legacy, sin snapshot de remuneración.
            await FinanzasTestSupport.SeedCobroServicioAsync(
                e.Context, e.Funcionario, e.Servicio, new DateTime(2026, 9, 5, 10, 0, 0), Monto);

            Assert.Equal(5_000m, await e.PlanillaAsync());

            await e.CambiarFuncionarioAsync(f => f.PorcentajeGanancia = 55m);

            // El legacy SÍ se mueve con el catálogo. No es lo ideal, pero es el comportamiento
            // anterior: preferimos eso a fabricarle un porcentaje histórico.
            Assert.Equal(5_500m, await e.PlanillaAsync());
        }

        // ─────────────── §27 — Liquidación con configuraciones mixtas ───────────────

        /// <summary>
        /// §16 — El caso central: el porcentaje cambia a mitad de quincena. La planilla es la suma
        /// exacta de los dos grupos, no un único porcentaje aplicado a todo.
        /// </summary>
        [Fact]
        public async Task Periodo50Y55_LiquidaCadaGrupoConSuSnapshot()
        {
            using var e = await Escenario.CrearAsync();

            await e.RegistrarServicioAsync(dia: 3);
            await e.RegistrarServicioAsync(dia: 8);
            await e.CambiarFuncionarioAsync(f => f.PorcentajeGanancia = 55m);
            await e.RegistrarServicioAsync(dia: 12);
            await e.RegistrarServicioAsync(dia: 14);

            // 2 × 5.000 (50 %) + 2 × 5.500 (55 %) = 21.000
            Assert.Equal(21_000m, await e.PlanillaAsync());

            // Y NO es ninguno de los dos extremos: ni todo al 50 % ni todo al 55 %.
            Assert.NotEqual(20_000m, await e.PlanillaAsync());
            Assert.NotEqual(22_000m, await e.PlanillaAsync());
        }

        [Fact]
        public async Task PeriodoConProducto11Y15_RespetaAmbosSnapshots()
        {
            using var e = await Escenario.CrearAsync();

            await e.RegistrarProductoAsync(dia: 3);
            await e.CambiarFuncionarioAsync(f => f.PorcentajeProducto = 15m);
            await e.RegistrarProductoAsync(dia: 12);

            // 11 % + 15 % de 10.000 = 1.100 + 1.500
            Assert.Equal(2_600m, await e.PlanillaAsync());
        }

        /// <summary>§25 — El orden de los cobros no puede cambiar el resultado.</summary>
        [Fact]
        public async Task OrdenDeCobros_NoCambiaLiquidacion()
        {
            var directo = await LiquidarEnOrdenAsync(new[] { 3, 8, 12, 14 });
            var inverso = await LiquidarEnOrdenAsync(new[] { 14, 12, 8, 3 });
            var mezclado = await LiquidarEnOrdenAsync(new[] { 12, 3, 14, 8 });

            Assert.Equal(directo, inverso);
            Assert.Equal(directo, mezclado);
        }

        /// <summary>§27.15 — Legacy y snapshot conviviendo en el mismo periodo.</summary>
        [Fact]
        public async Task MezclaLegacyYSnapshot_SeResuelveCorrectamente()
        {
            using var e = await Escenario.CrearAsync();

            // Legacy: sin snapshot, se liquidará con la configuración VIGENTE al consultar.
            await FinanzasTestSupport.SeedCobroServicioAsync(
                e.Context, e.Funcionario, e.Servicio, new DateTime(2026, 9, 3, 10, 0, 0), Monto);

            // Con snapshot al 50 %.
            await e.RegistrarServicioAsync(dia: 5);

            await e.CambiarFuncionarioAsync(f => f.PorcentajeGanancia = 55m);

            // legacy → 55 % (configuración actual) = 5.500
            // snapshot → 50 % congelado          = 5.000
            Assert.Equal(10_500m, await e.PlanillaAsync());
        }

        private static async Task<decimal> LiquidarEnOrdenAsync(IReadOnlyList<int> dias)
        {
            using var e = await Escenario.CrearAsync();

            // El cambio de porcentaje ocurre "el día 10": los cobros de antes quedan al 50 %.
            foreach (var dia in dias.Where(d => d < 10))
            {
                await e.RegistrarServicioAsync(dia);
            }

            await e.CambiarFuncionarioAsync(f => f.PorcentajeGanancia = 55m);

            foreach (var dia in dias.Where(d => d >= 10))
            {
                await e.RegistrarServicioAsync(dia);
            }

            return await e.PlanillaAsync();
        }

        private sealed class Escenario : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

            private Escenario(
                ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider,
                Funcionario funcionario,
                Servicio servicio,
                Models.Productos.Producto producto)
            {
                Context = context;
                _connection = connection;
                TenantProvider = tenantProvider;
                Funcionario = funcionario;
                Servicio = servicio;
                Producto = producto;
            }

            public ApplicationDbContext Context { get; }

            public TestTenantProvider TenantProvider { get; }

            public Funcionario Funcionario { get; }

            public Servicio Servicio { get; }

            public Models.Productos.Producto Producto { get; }

            public static async Task<Escenario> CrearAsync(
                TipoRelacionColaborador tipoRelacion = TipoRelacionColaborador.Empleado,
                ModalidadIvaColaborador modalidadIva = ModalidadIvaColaborador.NoFactura)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(
                    context, "Jordan",
                    porcentajeServicio: 50m,
                    porcentajeProducto: 11m,
                    comisionSobre: ComisionCalculadaSobre.TotalCobrado,
                    tipoRelacion: tipoRelacion,
                    modalidadIva: modalidadIva);

                var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", Monto);
                var producto = await FinanzasTestSupport.SeedProductoAsync(context, "Cera", Monto, stock: 50);

                return new Escenario(context, connection, tenantProvider, funcionario, servicio, producto);
            }

            public Task<int> RegistrarServicioAsync(int dia = 5) =>
                ControllerTestSupport.CreateCobroService(Context, TenantProvider)
                    .RegistrarAsync(new CobroCreateRequest
                    {
                        FechaCobro = new DateTime(2026, 9, dia, 10, 0, 0),
                        NombreCliente = "Cliente",
                        FuncionarioId = Funcionario.IdFuncionario,
                        ServicioId = Servicio.Id,
                        Monto = Monto,
                        MetodoPago = "EFECTIVO"
                    });

            public Task<int> RegistrarProductoAsync(int dia = 5) =>
                ControllerTestSupport.CreateCobroService(Context, TenantProvider)
                    .RegistrarAsync(new CobroCreateRequest
                    {
                        FechaCobro = new DateTime(2026, 9, dia, 10, 0, 0),
                        NombreCliente = "Cliente",
                        FuncionarioId = Funcionario.IdFuncionario,
                        ProductoId = Producto.IdProducto,
                        Monto = Monto,
                        MetodoPago = "EFECTIVO"
                    });

            public Task<Cobro> CobroAsync(int id) =>
                Context.Cobros.AsNoTracking().SingleAsync(c => c.IdCobro == id);

            public async Task CambiarFuncionarioAsync(Action<Funcionario> cambio)
            {
                var funcionario = await Context.Funcionarios
                    .SingleAsync(f => f.IdFuncionario == Funcionario.IdFuncionario);
                cambio(funcionario);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            /// <summary>Total de planilla devengada del periodo.</summary>
            public async Task<decimal> PlanillaAsync()
            {
                var resumen = await ControllerTestSupport
                    .CreateLiquidacionSemanalService(Context, TenantProvider)
                    .ObtenerResumenSemanaAsync(Inicio, Fin);

                return resumen.TotalAPagarColaboradoresGeneral;
            }

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }
    }
}
