using System.Text.Json;
using LuxuryApp.Controllers;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class InformacionControllerTests
    {
        [Fact]
        public async Task ObtenerCitasSemana_ShouldReturnCurrentContract_AndNormalizeWeekToMonday()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 13, 9, 0, 0));
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 15, 10, 0, 0));
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 19, 11, 0, 0));
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 20, 9, 0, 0));

            var controller = new InformacionController(
                ControllerTestSupport.CreateInformacionNegocioQueryService(context));
            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("info-user", tenantId));

            var result = await controller.ObtenerCitasSemana(new DateTime(2026, 4, 15));

            var json = Assert.IsType<JsonResult>(result);
            var payload = JsonSerializer.Serialize(json.Value);

            Assert.Contains("\"Dias\":[\"Lun\",\"Mar\",\"Mi\\u00E9\",\"Jue\",\"Vie\",\"S\\u00E1b\",\"Dom\"]", payload, StringComparison.Ordinal);
            Assert.Contains("\"Citas\":[1,0,1,0,0,0,1]", payload, StringComparison.Ordinal);
            Assert.Contains("\"Inicio\":\"13\"", payload, StringComparison.Ordinal);
            Assert.Contains("\"Fin\":\"19\"", payload, StringComparison.Ordinal);
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
                ColorCalendario = "#222222",
                PorcentajeGanancia = 40m,
                PorcentajeProducto = 10m,
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = true
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task SeedCitaAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            DateTime fechaHora)
        {
            context.Citas.Add(new Cita
            {
                FuncionarioId = funcionarioId,
                FechaHoraCita = fechaHora,
                NombreCliente = "Cliente Test",
                TelefonoCliente = "9999",
                Tipo = "CITA"
            });

            await context.SaveChangesAsync();
        }
    }
}
