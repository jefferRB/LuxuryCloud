using ClosedXML.Excel;
using LuxuryApp.Controllers.Funcionarios;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class FuncionariosControllerTests
    {
        [Fact]
        public async Task Create_ShouldPersistFuncionario_WhenTenantPlanAllowsIt()
        {
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedPlanAsync(context, planId, maxFuncionarios: 5);
            var puesto = await SeedPuestoAsync(context, "Estilista", "Atencion general");

            var controller = CreateController(context, tenantId, planId);

            var result = await controller.Create(new Funcionario
            {
                Nombre = "Ana",
                Telefono = "8888-0000",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = 40,
                PorcentajeProducto = 10,
                FechaIngreso = new DateTime(2026, 4, 13)
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(FuncionariosController.Index), redirect.ActionName);

            var funcionario = await context.Funcionarios.SingleAsync();
            Assert.Equal("Ana", funcionario.Nombre);
            Assert.Equal(puesto.IdPuesto, funcionario.IdPuesto);
            Assert.True(funcionario.Activo);
        }

        [Fact]
        public async Task Create_ShouldExposeError_WhenCommercialAccessCannotBeResolved()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var puesto = await SeedPuestoAsync(context, "Recepcion", "Front desk");
            var controller = CreateController(context, tenantId);

            var result = await controller.Create(new Funcionario
            {
                Nombre = "Carlos",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#222222",
                PorcentajeGanancia = 30,
                PorcentajeProducto = 5,
                FechaIngreso = new DateTime(2026, 4, 13)
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(FuncionariosController.Index), redirect.ActionName);
            Assert.Equal(
                "No fue posible resolver el acceso comercial del tenant para validar el limite de funcionarios.",
                controller.TempData["Error"]);

            Assert.Empty(await context.Funcionarios.ToListAsync());
        }

        [Fact]
        public async Task Create_ShouldRejectCrossTenantPuesto()
        {
            var tenantId = Guid.NewGuid();
            var otherTenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = otherTenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Puestos.Add(new Puesto
            {
                NombrePuesto = "Puesto Externo",
                Detalle = "Otro tenant"
            });
            await context.SaveChangesAsync();

            var foreignPuestoId = await context.Puestos
                .Select(p => p.IdPuesto)
                .SingleAsync();

            context.ChangeTracker.Clear();
            tenantProvider.TenantId = tenantId;

            await SeedPlanAsync(context, planId, maxFuncionarios: 5);
            var controller = CreateController(context, tenantId, planId);

            var result = await controller.Create(new Funcionario
            {
                Nombre = "Paola",
                Telefono = "8888-4444",
                IdPuesto = foreignPuestoId,
                ColorCalendario = "#333333",
                PorcentajeGanancia = 35,
                PorcentajeProducto = 10,
                FechaIngreso = new DateTime(2026, 4, 13)
            });

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<Funcionario>(view.Model);

            Assert.Equal("Paola", model.Nombre);
            Assert.False(controller.ModelState.IsValid);
            Assert.Contains(nameof(Funcionario.IdPuesto), controller.ModelState.Keys);
            Assert.Empty(await context.Funcionarios.ToListAsync());
        }

        [Fact]
        public async Task Create_ShouldCountOnlyActiveFuncionariosAgainstPlanLimit()
        {
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedPlanAsync(context, planId, maxFuncionarios: 1);
            var puesto = await SeedPuestoAsync(context, "Colorista", "Cabello");

            context.Funcionarios.Add(new Funcionario
            {
                Nombre = "Inactiva",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#101010",
                PorcentajeGanancia = 40,
                PorcentajeProducto = 10,
                FechaIngreso = new DateTime(2026, 4, 1),
                Activo = false
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId, planId);

            var result = await controller.Create(new Funcionario
            {
                Nombre = "Nueva",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#202020",
                PorcentajeGanancia = 35,
                PorcentajeProducto = 8,
                FechaIngreso = new DateTime(2026, 4, 13)
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(FuncionariosController.Index), redirect.ActionName);

            var funcionarios = await context.Funcionarios
                .OrderBy(f => f.Nombre)
                .ToListAsync();

            Assert.Equal(2, funcionarios.Count);
            Assert.Single(funcionarios, f => f.Activo);
            Assert.Contains(funcionarios, f => f.Nombre == "Nueva" && f.Activo);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(7)]
        public async Task Create_ShouldBlockWhenActiveFuncionariosReachConfiguredPlanLimit(int maxFuncionarios)
        {
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedPlanAsync(context, planId, maxFuncionarios);
            var puesto = await SeedPuestoAsync(context, $"Puesto {maxFuncionarios}", "Operaciones");

            for (var index = 1; index <= maxFuncionarios; index++)
            {
                context.Funcionarios.Add(new Funcionario
                {
                    Nombre = $"Activa {index}",
                    IdPuesto = puesto.IdPuesto,
                    ColorCalendario = $"#AA{index:D2}AA",
                    PorcentajeGanancia = 40,
                    PorcentajeProducto = 10,
                    FechaIngreso = new DateTime(2026, 4, 1).AddDays(index),
                    Activo = true
                });
            }

            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId, planId);
            var result = await controller.Create(new Funcionario
            {
                Nombre = "Extra",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#333333",
                PorcentajeGanancia = 35,
                PorcentajeProducto = 8,
                FechaIngreso = new DateTime(2026, 4, 13)
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(FuncionariosController.Index), redirect.ActionName);
            Assert.Equal(
                $"Tu plan actual permite hasta {maxFuncionarios} funcionarios. Para agregar mas, actualiza tu plan.",
                controller.TempData["Error"]);
            Assert.Equal(maxFuncionarios, await context.Funcionarios.CountAsync());
        }

        [Fact]
        public async Task Edit_ShouldPreserveActivoState_WhenStateIsManagedExternally()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var puesto = await SeedPuestoAsync(context, "Barbero", "Cabina 1");
            var funcionario = new Funcionario
            {
                Nombre = "Luis",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#444444",
                PorcentajeGanancia = 35,
                PorcentajeProducto = 5,
                FechaIngreso = new DateTime(2026, 4, 13),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.Edit(new Funcionario
            {
                IdFuncionario = funcionario.IdFuncionario,
                Nombre = "Luis Actualizado",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#555555",
                PorcentajeGanancia = 35,
                PorcentajeProducto = 5,
                FechaIngreso = funcionario.FechaIngreso
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(FuncionariosController.Index), redirect.ActionName);

            var persisted = await context.Funcionarios.SingleAsync();
            Assert.True(persisted.Activo);
            Assert.Equal("Luis Actualizado", persisted.Nombre);
        }

        [Fact]
        public async Task Edit_ShouldUpdateRebajarImpuestosFlag()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var puesto = await SeedPuestoAsync(context, "Barbero", "Cabina 1");
            var funcionario = new Funcionario
            {
                Nombre = "Luis",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#444444",
                PorcentajeGanancia = 35,
                PorcentajeProducto = 5,
                FechaIngreso = new DateTime(2026, 4, 13),
                Activo = true,
                RebajarImpuestosAntesDeComision = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.Edit(new Funcionario
            {
                IdFuncionario = funcionario.IdFuncionario,
                Nombre = "Luis",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#444444",
                PorcentajeGanancia = 35,
                PorcentajeProducto = 5,
                FechaIngreso = funcionario.FechaIngreso,
                // Fuente de verdad: comisión sobre total → el flag histórico debe quedar en false.
                ComisionCalculadaSobre = LuxuryApp.Models.Fiscal.ComisionCalculadaSobre.TotalCobrado
            });

            Assert.IsType<RedirectToActionResult>(result);

            var persisted = await context.Funcionarios.SingleAsync();
            Assert.Equal(LuxuryApp.Models.Fiscal.ComisionCalculadaSobre.TotalCobrado, persisted.ComisionCalculadaSobre);
            Assert.False(persisted.RebajarImpuestosAntesDeComision);
        }

        [Fact]
        public async Task Create_ShouldPersistRebajarImpuestosFlagAsFalse()
        {
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedPlanAsync(context, planId, maxFuncionarios: 5);
            var puesto = await SeedPuestoAsync(context, "Estilista", "Atencion general");

            var controller = CreateController(context, tenantId, planId);
            var result = await controller.Create(new Funcionario
            {
                Nombre = "Sin Rebaja",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = 50,
                PorcentajeProducto = 50,
                FechaIngreso = new DateTime(2026, 4, 13),
                // Fuente de verdad: comisión sobre total → el flag histórico debe quedar en false.
                ComisionCalculadaSobre = LuxuryApp.Models.Fiscal.ComisionCalculadaSobre.TotalCobrado
            });

            Assert.IsType<RedirectToActionResult>(result);

            var persisted = await context.Funcionarios.SingleAsync();
            Assert.Equal(LuxuryApp.Models.Fiscal.ComisionCalculadaSobre.TotalCobrado, persisted.ComisionCalculadaSobre);
            Assert.False(persisted.RebajarImpuestosAntesDeComision);
        }

        [Fact]
        public async Task Activar_ShouldBlock_WhenActivePlanLimitIsReached()
        {
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedPlanAsync(context, planId, maxFuncionarios: 1);
            var puesto = await SeedPuestoAsync(context, "Masajista", "Spa");

            context.Funcionarios.AddRange(
                new Funcionario
                {
                    Nombre = "Activa",
                    IdPuesto = puesto.IdPuesto,
                    ColorCalendario = "#111111",
                    PorcentajeGanancia = 40,
                    PorcentajeProducto = 10,
                    FechaIngreso = new DateTime(2026, 4, 1),
                    Activo = true
                },
                new Funcionario
                {
                    Nombre = "Pendiente",
                    IdPuesto = puesto.IdPuesto,
                    ColorCalendario = "#222222",
                    PorcentajeGanancia = 40,
                    PorcentajeProducto = 10,
                    FechaIngreso = new DateTime(2026, 4, 1),
                    Activo = false
                });
            await context.SaveChangesAsync();

            var inactivo = await context.Funcionarios
                .OrderBy(f => f.Nombre)
                .LastAsync();

            var controller = CreateController(context, tenantId, planId);
            var result = await controller.Activar(inactivo.IdFuncionario);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(FuncionariosController.Index), redirect.ActionName);
            Assert.Equal(
                "Tu plan actual permite hasta 1 funcionarios. Para agregar mas, actualiza tu plan.",
                controller.TempData["Error"]);

            var persisted = await context.Funcionarios
                .SingleAsync(f => f.IdFuncionario == inactivo.IdFuncionario);
            Assert.False(persisted.Activo);
        }

        [Fact]
        public async Task PagosSemana_ShouldReturnTypedViewModel_WithWeekKpis()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var puesto = await SeedPuestoAsync(context, "Estilista", "General");
            var funcionario = new Funcionario
            {
                Nombre = "Ana",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = 50,
                PorcentajeProducto = 10,
                FechaIngreso = new DateTime(2026, 4, 13),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();

            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 14), 200m);

            var controller = CreateController(context, tenantId);
            var result = await controller.PagosSemana(new DateTime(2026, 4, 13));

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<PagosSemanaPageViewModel>(view.Model);

            Assert.Equal(new DateTime(2026, 4, 13), model.InicioSemana);
            Assert.Equal(new DateTime(2026, 4, 19), model.FinSemana);
            Assert.Equal(200m, model.TotalGeneradoServicios);
            Assert.Equal(200m, model.TotalGeneradoGeneral);
            Assert.Single(model.Funcionarios);
            Assert.Equal(3, model.MetodosPago.Count);
        }

        [Fact]
        public async Task ExportarPagosExcel_ShouldReturnWorkbook_WithSummarySheet()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var puesto = await SeedPuestoAsync(context, "Estilista", "General");
            var funcionario = new Funcionario
            {
                Nombre = "Ana",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = 50,
                PorcentajeProducto = 10,
                FechaIngreso = new DateTime(2026, 4, 13),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();

            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 14), 200m);

            var controller = CreateController(context, tenantId, tenantDisplayName: "NORTHSIDE STUDIO");
            var result = await controller.ExportarPagosExcel(
                new DateTime(2026, 4, 13),
                new DateTime(2026, 4, 19));

            var file = Assert.IsType<FileContentResult>(result);
            Assert.Equal(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                file.ContentType);
            // Nombre de archivo centralizado: {TenantSegment}_{Reporte}_{fecha}.xlsx
            Assert.StartsWith("NORTHSIDESTUDIO_LiquidacionSemanal_", file.FileDownloadName, StringComparison.Ordinal);
            Assert.EndsWith(".xlsx", file.FileDownloadName, StringComparison.Ordinal);

            using var workbook = new XLWorkbook(new MemoryStream(file.FileContents));

            // Hoja 1: Resumen del periodo (total cobrado a clientes = 200).
            var wsResumen = workbook.Worksheet("Resumen");
            Assert.Equal("Total cobrado a clientes", wsResumen.Cell(5, 1).GetString());
            Assert.Equal(200m, wsResumen.Cell(5, 2).GetValue<decimal>());

            // Hoja 2: Funcionarios (fila de datos en headerRow + 1 = 4).
            var worksheet = workbook.Worksheet("Funcionarios");
            Assert.Contains("NORTHSIDE STUDIO", worksheet.Cell("A1").GetString(), StringComparison.Ordinal);
            Assert.Equal("Ana", worksheet.Cell(4, 1).GetString());
            Assert.Equal(200m, worksheet.Cell(4, 5).GetValue<decimal>()); // Total cobrado (col 5)
        }

        [Fact]
        public async Task Eliminar_ShouldBlockDeletion_WhenFuncionarioHasRelatedPayments()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var puesto = await SeedPuestoAsync(context, "Masajista", "Spa");
            var funcionario = new Funcionario
            {
                Nombre = "Andrea",
                Telefono = "8888-5555",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#444444",
                PorcentajeGanancia = 40,
                PorcentajeProducto = 10,
                FechaIngreso = new DateTime(2026, 4, 13),
                Activo = true
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();

            context.PagosFuncionarios.Add(new PagoFuncionario
            {
                FuncionarioId = funcionario.IdFuncionario,
                MontoPagado = 250,
                FechaPago = new DateTime(2026, 4, 13),
                InicioSemana = new DateTime(2026, 4, 13),
                FinSemana = new DateTime(2026, 4, 19),
                Observacion = "Pago semanal"
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.Eliminar(funcionario.IdFuncionario);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(FuncionariosController.Index), redirect.ActionName);
            Assert.Equal(
                "No se puede eliminar el funcionario porque tiene citas, cobros, pagos o liquidaciones asociadas. Puedes dejarlo inactivo si ya no trabaja en el negocio.",
                controller.TempData["Error"]);
            Assert.Single(await context.Funcionarios.ToListAsync());
        }

        [Fact]
        public async Task Eliminar_ShouldBlockDeletion_WhenFuncionarioHasLiquidacionDetalle()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Categorias.Add(new Categoria
            {
                Nombre = LiquidacionSemanalDefaults.CategoriaPagoFuncionarios,
                Detalle = "Auto"
            });
            var puesto = await SeedPuestoAsync(context, "Barbero", "General");
            await context.SaveChangesAsync();

            var categoria = await context.Categorias.SingleAsync();
            var funcionario = new Funcionario
            {
                Nombre = "David",
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#000000",
                PorcentajeGanancia = 40,
                PorcentajeProducto = 10,
                FechaIngreso = new DateTime(2026, 4, 13),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();

            context.Egresos.Add(new Egreso
            {
                FechaEgreso = new DateTime(2026, 4, 19),
                CategoriaId = categoria.Id,
                Monto = 120,
                MetodoPago = "EFECTIVO",
                Detalle = "Pago semanal"
            });
            await context.SaveChangesAsync();

            var egreso = await context.Egresos.SingleAsync();
            context.LiquidacionesSemanales.Add(new LiquidacionSemanal
            {
                SemanaInicio = new DateTime(2026, 4, 13),
                SemanaFin = new DateTime(2026, 4, 19),
                FechaPago = new DateTime(2026, 4, 19),
                MontoTotal = 120,
                Estado = LiquidacionSemanalDefaults.EstadoPagada,
                EgresoId = egreso.IdEgreso,
                FechaCreacion = new DateTime(2026, 4, 19)
            });
            await context.SaveChangesAsync();

            var liquidacion = await context.LiquidacionesSemanales.SingleAsync();
            context.LiquidacionesSemanalesDetalle.Add(new LiquidacionSemanalDetalle
            {
                LiquidacionSemanalId = liquidacion.Id,
                FuncionarioId = funcionario.IdFuncionario,
                MontoServicios = 300,
                MontoProductos = 0,
                Impuestos = 39,
                MontoNeto = 261,
                MontoPagado = 120,
                Pendiente = 0
            });
            await context.SaveChangesAsync();

            var controller = CreateController(context, tenantId);
            var result = await controller.Eliminar(funcionario.IdFuncionario);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(FuncionariosController.Index), redirect.ActionName);
            Assert.Equal(
                "No se puede eliminar el funcionario porque tiene citas, cobros, pagos o liquidaciones asociadas. Puedes dejarlo inactivo si ya no trabaja en el negocio.",
                controller.TempData["Error"]);
        }

        private static FuncionariosController CreateController(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            Guid? planId = null,
            string? tenantDisplayName = null)
        {
            var tenantDisplayNameService = string.IsNullOrEmpty(tenantDisplayName)
                ? ControllerTestSupport.CreateTenantDisplayNameService()
                : ControllerTestSupport.CreateTenantDisplayNameService(tenantDisplayName);

            var controller = new FuncionariosController(
                context,
                ControllerTestSupport.CreateLiquidacionSemanalService(
                    context, new TestTenantProvider { TenantId = tenantId }),
                ControllerTestSupport.CreateFuncionarioPortalAccessService(),
                ControllerTestSupport.CreateFuncionarioPortalPermissionService(),
                ControllerTestSupport.CreateAccountEmailService(),
                ControllerTestSupport.BusinessDateTimeProvider,
                tenantDisplayNameService,
                ControllerTestSupport.CreateFuncionarioPhotoStorageService(),
                new TestTenantProvider { TenantId = tenantId },
                NullLogger<FuncionariosController>.Instance,
                new LuxuryApp.Services.Finanzas.LegacyFinancialImpactService(context));

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("user-funcionarios", tenantId));

            if (planId.HasValue)
            {
                var maxFuncionarios = context.Planes
                    .IgnoreQueryFilters()
                    .Where(plan => plan.Id == planId.Value)
                    .Select(plan => plan.MaxFuncionarios)
                    .SingleOrDefault();

                controller.HttpContext.Items["TenantCommercialAccess"] = new TenantCommercialAccessResult
                {
                    CanAccessApp = true,
                    TenantId = tenantId,
                    EffectivePlanId = planId.Value,
                    EffectivePlanName = "Full",
                    EffectiveEmployeeLimit = maxFuncionarios
                };
            }

            return controller;
        }

        private static async Task SeedPlanAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid planId,
            int maxFuncionarios)
        {
            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Full",
                PrecioMensual = 99,
                Moneda = "CRC",
                Activo = true,
                MaxFuncionarios = maxFuncionarios
            });

            await context.SaveChangesAsync();
        }

        private static async Task<Puesto> SeedPuestoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            string detalle)
        {
            context.Puestos.Add(new Puesto
            {
                NombrePuesto = nombre,
                Detalle = detalle,
                Activo = true
            });

            await context.SaveChangesAsync();
            return await context.Puestos.SingleAsync(p => p.NombrePuesto == nombre);
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
