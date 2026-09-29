using ClosedXML.Excel;
using LuxuryApp.Controllers.Finanzas;
using LuxuryApp.Controllers.Funcionarios;
using LuxuryApp.Models.Comprobantes;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Account;
using LuxuryApp.Services.Comprobantes;
using LuxuryApp.Services.Exports;
using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ExcelDisplayNameTests
    {
        [Fact]
        public async Task CobrosExportarExcel_ShouldUseTenantDisplayNameInHeader()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedTenantDisplayNameAsync(context, tenantId, "Jorhanna Diaz", "Barberia jor");

            var controller = new CobrosController(
                ControllerTestSupport.CreateCobroService(context),
                ControllerTestSupport.CreateCobroQueryService(context, tenantProvider),
                new NoOpComprobanteCobroService(),
                ControllerTestSupport.BusinessDateTimeProvider,
                ControllerTestSupport.CreateTenantDisplayNameService(context, tenantProvider));

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("user-cobros", tenantId));

            var result = await controller.ExportarExcel(new CobroFiltroViewModel { VistaTiempo = "todo" });

            var file = Assert.IsType<FileContentResult>(result);
            Assert.Equal("BarberiaJor_ReporteCobros_2026-05-26.xlsx", file.FileDownloadName);
            Assert.DoesNotContain("LuxeReporteCobros", file.FileDownloadName, StringComparison.OrdinalIgnoreCase);
            using var workbook = new XLWorkbook(new MemoryStream(file.FileContents));
            Assert.Equal("Barberia jor", workbook.Worksheet("Reporte Cobros").Cell("A1").GetString());
        }

        [Fact]
        public async Task FuncionariosExportarPagosExcel_ShouldUseTenantDisplayNameInHeader()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedTenantDisplayNameAsync(context, tenantId, "Jorhanna Diaz", "Barberia jor");

            var controller = new FuncionariosController(
                context,
                ControllerTestSupport.CreateLiquidacionSemanalService(context, tenantProvider),
                new NoOpPortalAccessService(),
                new NoOpPortalPermissionService(),
                new NoOpAccountEmailService(),
                ControllerTestSupport.BusinessDateTimeProvider,
                ControllerTestSupport.CreateTenantDisplayNameService(context, tenantProvider),
                ControllerTestSupport.CreateFuncionarioPhotoStorageService(),
                tenantProvider,
                NullLogger<FuncionariosController>.Instance,
                new LuxuryApp.Services.Finanzas.LegacyFinancialImpactService(context));

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("user-funcionarios", tenantId));

            var result = await controller.ExportarPagosExcel(
                new DateTime(2026, 5, 25),
                new DateTime(2026, 5, 31));

            var file = Assert.IsType<FileContentResult>(result);
            Assert.Equal("BarberiaJor_LiquidacionSemanal_2026-05-26.xlsx", file.FileDownloadName);
            Assert.DoesNotContain("LuxeLiquidacionSemanal", file.FileDownloadName, StringComparison.OrdinalIgnoreCase);
            using var workbook = new XLWorkbook(new MemoryStream(file.FileContents));
            // La hoja "Resumen" lleva el nombre del negocio en A1.
            Assert.Equal("Barberia jor", workbook.Worksheet("Resumen").Cell("A1").GetString());
        }

        [Theory]
        [InlineData("Barberia jor", "Reporte Cobros", "BarberiaJor_ReporteCobros_2026-06-18.xlsx")]
        [InlineData("Barbería Jor Premium", "Reporte Egresos", "BarberiaJorPremium_ReporteEgresos_2026-06-18.xlsx")]
        [InlineData("Barbería Niño", "Pagos Funcionarios", "BarberiaNino_PagosFuncionarios_2026-06-18.xlsx")]
        [InlineData("Barbería / Test * 2026", "Reporte Cobros", "BarberiaTest2026_ReporteCobros_2026-06-18.xlsx")]
        [InlineData("", "Reporte Egresos", "LuxuryCloud_ReporteEgresos_2026-06-18.xlsx")]
        public void ExcelReportFileNameBuilder_ShouldGenerateSafeCrossPlatformNames(
            string tenantDisplayName,
            string reportName,
            string expected)
        {
            var fileName = ExcelReportFileNameBuilder.Build(
                tenantDisplayName,
                reportName,
                new DateTime(2026, 6, 18, 14, 30, 0));

            Assert.Equal(expected, fileName);
            Assert.DoesNotContain("/", fileName, StringComparison.Ordinal);
            Assert.DoesNotContain("*", fileName, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", fileName, StringComparison.Ordinal);
        }

        private static async Task SeedTenantDisplayNameAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            string tenantName,
            string displayName)
        {
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = tenantName,
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = $"owner-{tenantId:N}",
                TenantId = tenantId,
                UserName = $"owner-{tenantId:N}@test.local",
                Email = $"owner-{tenantId:N}@test.local",
                Name = displayName,
                State = true
            });

            await context.SaveChangesAsync();
        }

        private sealed class NoOpComprobanteCobroService : IComprobanteCobroService
        {
            public Task<ComprobanteCobro?> CrearYEnviarDesdeCobroAsync(
                int cobroId,
                string emailDestino,
                bool guardarEmailEnCliente,
                string? createdByUserId,
                int? funcionarioScopeId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<ComprobanteCobro?>(null);

            public Task<ComprobanteCobro?> ReenviarAsync(
                int comprobanteId,
                int? funcionarioScopeId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<ComprobanteCobro?>(null);

            public Task<ComprobanteCobro?> ObtenerParaAppAsync(
                int comprobanteId,
                int? funcionarioScopeId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<ComprobanteCobro?>(null);

            public Task<ComprobanteCobro?> ObtenerPorTokenPublicoAsync(
                string token,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<ComprobanteCobro?>(null);

            public byte[] GenerarPdf(ComprobanteCobro comprobante) => [];
        }

        private sealed class NoOpPortalAccessService : IFuncionarioPortalAccessService
        {
            public Task<FuncionarioAccesoViewModel> ObtenerEstadoAsync(
                int funcionarioId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new FuncionarioAccesoViewModel { FuncionarioId = funcionarioId });

            public Task<FuncionarioAccesoResultado> ActivarAccesoAsync(
                int funcionarioId,
                string email,
                FuncionarioAccesoCredencialModo modo,
                string? contrasenaTemporal,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(FuncionarioAccesoResultado.Falla("No usado en esta prueba."));

            public Task<FuncionarioAccesoResultado> DesactivarAccesoAsync(
                int funcionarioId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(FuncionarioAccesoResultado.Falla("No usado en esta prueba."));

            public Task<FuncionarioAccesoResultado> ReactivarAccesoAsync(
                int funcionarioId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(FuncionarioAccesoResultado.Falla("No usado en esta prueba."));

            public Task<FuncionarioAccesoResultado> GenerarEnlaceInvitacionAsync(
                int funcionarioId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(FuncionarioAccesoResultado.Falla("No usado en esta prueba."));

            public Task<FuncionarioAccesoResultado> CambiarCorreoAsync(
                int funcionarioId,
                string nuevoEmail,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(FuncionarioAccesoResultado.Falla("No usado en esta prueba."));
        }

        private sealed class NoOpPortalPermissionService : IFuncionarioPortalPermissionService
        {
            public Task<FuncionarioPortalPermisosSet> ObtenerAsync(
                int funcionarioId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(FuncionarioPortalPermisosSet.Defaults());

            public Task<bool> TienePermisoAsync(
                int funcionarioId,
                string permiso,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(true);

            public Task CrearDefaultsAsync(
                int funcionarioId,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<bool> GuardarAsync(
                int funcionarioId,
                IReadOnlyDictionary<string, bool> valores,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(true);
        }

        private sealed class NoOpAccountEmailService : IAccountEmailService
        {
            public Task SendPasswordResetEmailAsync(
                string toEmail,
                string displayName,
                string resetLink,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task SendEmailConfirmationEmailAsync(
                string toEmail,
                string displayName,
                string confirmationLink,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task SendFuncionarioInvitationEmailAsync(
                string toEmail,
                string displayName,
                string setPasswordLink,
                string businessName,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task SendAccessInvitationEmailAsync(
                string toEmail,
                string displayName,
                string setPasswordLink,
                string businessName,
                string accessDescription,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
