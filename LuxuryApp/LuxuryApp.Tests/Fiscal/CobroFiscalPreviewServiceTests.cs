using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Fiscal;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// Verifica el desglose fiscal (IVA incluido) que alimenta el modal de cobro. El servicio
    /// reutiliza el motor fiscal central; aquí se prueba la resolución por cita.
    /// </summary>
    public class CobroFiscalPreviewServiceTests
    {
        private static CobroFiscalPreviewService CreateSut(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TestTenantProvider tenantProvider) =>
            new(
                context,
                new TenantFiscalConfigService(context, tenantProvider),
                new TaxCalculationService());

        [Fact]
        public async Task PreviewCitaAsync_ServicioConIvaIncluido_SeparaBaseEIva()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context);
            var servicio = new Servicio { Nombre = "Corte", Precio = 10000m, Activo = true };
            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();

            var cita = new Cita
            {
                NombreCliente = "Cliente",
                ServicioId = servicio.Id,
                FuncionarioId = funcionario.IdFuncionario,
                FechaHoraCita = new DateTime(2026, 6, 30, 9, 0, 0),
                Tipo = "CITA"
            };
            context.Citas.Add(cita);
            await context.SaveChangesAsync();

            var sut = CreateSut(context, tenantProvider);
            var preview = await sut.PreviewCitaAsync(cita.Id, 10000m);

            Assert.NotNull(preview);
            Assert.Equal(10000m, preview!.Total);
            Assert.Equal(8849.56m, preview.BaseSinIva);
            Assert.Equal(1150.44m, preview.IvaIncluido);
            Assert.True(preview.AplicaIva);
            Assert.Equal("Servicio", preview.TipoLinea);
        }

        [Fact]
        public async Task PreviewCitaAsync_MontoParcial_AjustaDesglose()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context);
            var servicio = new Servicio { Nombre = "Tinte", Precio = 36000m, Activo = true };
            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();

            var cita = new Cita
            {
                NombreCliente = "Cliente",
                ServicioId = servicio.Id,
                FuncionarioId = funcionario.IdFuncionario,
                FechaHoraCita = new DateTime(2026, 6, 30, 10, 0, 0),
                Tipo = "CITA"
            };
            context.Citas.Add(cita);
            await context.SaveChangesAsync();

            var sut = CreateSut(context, tenantProvider);
            // Pago parcial: el desglose debe basarse en el monto cobrado real, no en el precio.
            var preview = await sut.PreviewCitaAsync(cita.Id, 36000m);

            Assert.NotNull(preview);
            Assert.Equal(31858.41m, preview!.BaseSinIva);
            Assert.Equal(4141.59m, preview.IvaIncluido);
        }

        [Fact]
        public async Task PreviewCitaAsync_CitaInexistente_RetornaNull()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var sut = CreateSut(context, tenantProvider);
            var preview = await sut.PreviewCitaAsync(999, 10000m);

            Assert.Null(preview);
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context)
        {
            context.Puestos.Add(new Puesto { NombrePuesto = "Barbero", Detalle = "General", Activo = true });
            await context.SaveChangesAsync();
            var puesto = await context.Puestos.FirstAsync();

            var funcionario = new Funcionario
            {
                Nombre = "Ana",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = 50m,
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }
    }
}
