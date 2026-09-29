using LuxuryApp.Controllers.Finanzas;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// Los cobros legacy (sin snapshot) SIGUEN dependiendo del catálogo actual, así que editar el
    /// IVA de un servicio todavía puede mover reportes de meses cerrados. No se bloquea —el caso
    /// LIMPIEZA FACIAL fue una corrección legítima y necesaria— pero se avisa con números concretos
    /// y se exige confirmación explícita.
    ///
    /// <para>
    /// La comprobación es de SERVIDOR: esconder el aviso con JavaScript, o mandar el formulario por
    /// fuera de la pantalla, no salta la validación.
    /// </para>
    /// </summary>
    public class AvisoConfiguracionHistoricaTests
    {
        private static readonly DateTime Fecha = new(2026, 3, 10, 9, 0, 0);

        [Fact]
        public async Task EditarFiscalidadServicio_ConCobrosLegacy_ExigeConfirmacion()
        {
            var (context, connection, controller, servicio) = await ArmarAsync(conCobroLegacy: true);
            using var c = context;
            using var cn = connection;

            var resultado = await controller.Save(
                Editado(servicio, aplicaIva: false),
                confirmarImpactoHistorico: false);

            // Se devuelve el formulario, NO se guarda…
            Assert.IsType<PartialViewResult>(resultado);
            Assert.False(controller.ModelState.IsValid);

            // …con un aviso que dice cuántas transacciones están en juego.
            var aviso = Assert.IsType<string>(controller.ViewData[AvisoImpactoHistorico.CampoConfirmacion]);
            Assert.Contains("1 cobro histórico", aviso);

            var enBase = await context.Servicios.AsNoTracking().SingleAsync(s => s.Id == servicio.Id);
            Assert.True(enBase.AplicaIva);
        }

        [Fact]
        public async Task EditarFiscalidadServicio_Confirmado_Guarda()
        {
            var (context, connection, controller, servicio) = await ArmarAsync(conCobroLegacy: true);
            using var c = context;
            using var cn = connection;

            var resultado = await controller.Save(
                Editado(servicio, aplicaIva: false),
                confirmarImpactoHistorico: true);

            Assert.IsType<OkResult>(resultado);

            var enBase = await context.Servicios.AsNoTracking().SingleAsync(s => s.Id == servicio.Id);
            Assert.False(enBase.AplicaIva);
        }

        /// <summary>
        /// §30: nada de avisos constantes sin sentido. Renombrar el servicio no reinterpreta
        /// ninguna transacción, así que no se molesta al usuario.
        /// </summary>
        [Fact]
        public async Task EditarSoloElNombre_NoPideConfirmacion()
        {
            var (context, connection, controller, servicio) = await ArmarAsync(conCobroLegacy: true);
            using var c = context;
            using var cn = connection;

            var editado = Editado(servicio, aplicaIva: true);
            editado.Nombre = "Corte Premium";

            Assert.IsType<OkResult>(await controller.Save(editado, confirmarImpactoHistorico: false));
            Assert.Null(controller.ViewData[AvisoImpactoHistorico.CampoConfirmacion]);
        }

        /// <summary>
        /// Y cuando TODOS los cobros del servicio ya tienen snapshot, cambiar el catálogo no puede
        /// tocar el pasado: tampoco hay nada que advertir.
        /// </summary>
        [Fact]
        public async Task ServicioSinCobrosLegacy_NoPideConfirmacion()
        {
            var (context, connection, controller, servicio) = await ArmarAsync(conCobroLegacy: false);
            using var c = context;
            using var cn = connection;

            Assert.IsType<OkResult>(await controller.Save(
                Editado(servicio, aplicaIva: false),
                confirmarImpactoHistorico: false));

            Assert.Null(controller.ViewData[AvisoImpactoHistorico.CampoConfirmacion]);
        }

        [Fact]
        public async Task ContarCobrosLegacy_NoCuentaLosQueYaTienenSnapshot()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var c = context;
            using var cn = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Drew");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", 7_000m);

            // Legacy: insertado directo, sin pasar por CobroService.
            await FinanzasTestSupport.SeedCobroServicioAsync(context, funcionario, servicio, Fecha, 7_000m);

            // Con snapshot: registrado por el camino real.
            var cobroNuevo = await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, Fecha, 7_000m);
            cobroNuevo.AplicaIvaSnapshot = true;
            cobroNuevo.TarifaIvaSnapshot = 13m;
            cobroNuevo.PrecioIncluyeIvaSnapshot = true;
            await context.SaveChangesAsync();

            var servicioImpacto = new LegacyFinancialImpactService(context);

            Assert.Equal(1, await servicioImpacto.ContarCobrosLegacyDeServicioAsync(servicio.Id));
            Assert.Equal(1, await servicioImpacto.ContarCobrosLegacyDelNegocioAsync());

            // La producción del colaborador cuenta TODO: el snapshot de comisión todavía no existe.
            Assert.Equal(2, await servicioImpacto.ContarProduccionHistoricaDeColaboradorAsync(
                funcionario.IdFuncionario));
        }

        private static Servicio Editado(Servicio original, bool aplicaIva) => new()
        {
            Id = original.Id,
            Nombre = original.Nombre,
            Precio = original.Precio,
            DuracionMinutos = original.DuracionMinutos,
            AplicaIva = aplicaIva,
            TarifaIva = original.TarifaIva,
            PrecioIncluyeIva = original.PrecioIncluyeIva
        };

        private static async Task<(ApplicationDbContext Context,
            Microsoft.Data.Sqlite.SqliteConnection Connection,
            ServiciosController Controller,
            Servicio Servicio)> ArmarAsync(bool conCobroLegacy)
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Drew");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", 7_000m);

            var cobro = await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, Fecha, 7_000m);

            if (!conCobroLegacy)
            {
                cobro.AplicaIvaSnapshot = true;
                cobro.TarifaIvaSnapshot = 13m;
                cobro.PrecioIncluyeIvaSnapshot = true;
                await context.SaveChangesAsync();
            }

            context.ChangeTracker.Clear();

            var controller = new ServiciosController(
                context,
                NullLogger<ServiciosController>.Instance,
                new LegacyFinancialImpactService(context));

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("user-servicios", tenantProvider.TenantId));

            return (context, connection, controller, servicio);
        }
    }
}
