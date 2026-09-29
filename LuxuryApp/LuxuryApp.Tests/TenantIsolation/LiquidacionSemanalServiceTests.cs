using LuxuryApp.Controllers.Finanzas;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class LiquidacionSemanalServiceTests
    {
        [Fact]
        public async Task RegistrarPagoAsync_ShouldCreateCategoriaEgresoLiquidacionAndDistribution_FromRealCobrosByMonth()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana", 50m, 15m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 28), 700m);

            var service = CreateService(context, tenantProvider);

            var liquidacionId = await service.RegistrarPagoAsync(new RegistrarLiquidacionSemanalCommand
            {
                SemanaInicio = new DateTime(2026, 4, 27),
                SemanaFin = new DateTime(2026, 5, 3),
                FechaPago = new DateTime(2026, 5, 3, 9, 15, 0),
                MetodoPago = "SINPE",
                Observacion = "Pago parcial",
                CreadoPor = "user-test",
                Detalles =
                {
                    new RegistrarLiquidacionSemanalDetalleCommand
                    {
                        FuncionarioId = funcionario.IdFuncionario,
                        MontoPagado = 100m
                    }
                }
            });

            var categoria = await context.Categorias.SingleAsync();
            Assert.Equal(LiquidacionSemanalDefaults.CategoriaPagoFuncionarios, categoria.Nombre);
            Assert.True(categoria.Activo);

            var egreso = await context.Egresos.SingleAsync();
            Assert.Equal(100m, egreso.Monto);
            Assert.Equal("SINPE", egreso.MetodoPago);

            var liquidacion = await context.LiquidacionesSemanales
                .SingleAsync(l => l.Id == liquidacionId);

            Assert.Equal(egreso.IdEgreso, liquidacion.EgresoId);
            Assert.Equal(new DateTime(2026, 4, 27), liquidacion.SemanaInicio);
            Assert.Equal(new DateTime(2026, 5, 3), liquidacion.SemanaFin);
            Assert.Equal(100m, liquidacion.MontoTotal);

            var detalle = await context.LiquidacionesSemanalesDetalle.SingleAsync();
            Assert.Equal(funcionario.IdFuncionario, detalle.FuncionarioId);
            Assert.Equal(100m, detalle.MontoPagado);
            Assert.Equal(700m, detalle.MontoServicios);
            Assert.Equal(0m, detalle.MontoProductos);
            // IVA incluido: base = 700 / 1.13 = 619.47; IVA = 80.53.
            Assert.Equal(80.53m, detalle.Impuestos);
            Assert.Equal(619.47m, detalle.MontoNeto);
            // PagoFinal = 619.47 * 50% = 309.74; pendiente tras pagar 100 = 309.74 - 100 = 209.74.
            Assert.Equal(209.74m, detalle.Pendiente);

            var distribuciones = await context.LiquidacionesSemanalesDistribucionMensual
                .OrderBy(d => d.Mes)
                .ToListAsync();

            Assert.Collection(
                distribuciones,
                abril =>
                {
                    Assert.Equal(2026, abril.Anio);
                    Assert.Equal(4, abril.Mes);
                    Assert.Equal(100m, abril.MontoAsignado);
                    Assert.Equal(1, abril.DiasAplicados);
                });
        }

        [Fact]
        public async Task RegistrarPagoAsync_ShouldReuseExistingCategoriaWithoutDuplicatingRows()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Categorias.Add(new Categoria
            {
                Nombre = LiquidacionSemanalDefaults.CategoriaPagoFuncionarios,
                Detalle = "Creada antes",
                Activo = false
            });
            await context.SaveChangesAsync();

            var funcionario = await SeedFuncionarioAsync(context, "Luis", 40m, 10m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 14), 500m);

            var service = CreateService(context, tenantProvider);

            await service.RegistrarPagoAsync(new RegistrarLiquidacionSemanalCommand
            {
                SemanaInicio = new DateTime(2026, 4, 13),
                SemanaFin = new DateTime(2026, 4, 19),
                MetodoPago = "EFECTIVO",
                Detalles =
                {
                    new RegistrarLiquidacionSemanalDetalleCommand
                    {
                        FuncionarioId = funcionario.IdFuncionario,
                        MontoPagado = 50m
                    }
                }
            });

            var categorias = await context.Categorias
                .Where(c => c.Nombre == LiquidacionSemanalDefaults.CategoriaPagoFuncionarios)
                .ToListAsync();

            Assert.Single(categorias);
            Assert.True(categorias[0].Activo);
        }

        [Fact]
        public async Task RegistrarPagoAsync_ShouldRejectWhenAmountExceedsPending()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Paola", 50m, 0m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 15), 100m);

            var service = CreateService(context, tenantProvider);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegistrarPagoAsync(
                new RegistrarLiquidacionSemanalCommand
                {
                    SemanaInicio = new DateTime(2026, 4, 13),
                    SemanaFin = new DateTime(2026, 4, 19),
                    MetodoPago = "TARJETA",
                    Detalles =
                    {
                        new RegistrarLiquidacionSemanalDetalleCommand
                        {
                            FuncionarioId = funcionario.IdFuncionario,
                            MontoPagado = 60m
                        }
                    }
                }));

            Assert.Contains("excede el pendiente", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await context.Egresos.ToListAsync());
            Assert.Empty(await context.LiquidacionesSemanales.ToListAsync());
        }

        [Fact]
        public async Task Dashboard_ShouldSeparateCashFlowFromAnalyticalDistribution()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Mario", 50m, 0m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 29), 700m);

            context.Categorias.Add(new Categoria
            {
                Nombre = "Alquiler",
                Detalle = "Local comercial"
            });
            await context.SaveChangesAsync();

            var categoriaAlquiler = await context.Categorias.SingleAsync(c => c.Nombre == "Alquiler");
            context.Egresos.Add(new Egreso
            {
                FechaEgreso = new DateTime(2026, 4, 20, 8, 0, 0),
                CategoriaId = categoriaAlquiler.Id,
                Monto = 50m,
                MetodoPago = "EFECTIVO",
                Detalle = "Pago de alquiler"
            });
            await context.SaveChangesAsync();

            var service = CreateService(context, tenantProvider);
            await service.RegistrarPagoAsync(new RegistrarLiquidacionSemanalCommand
            {
                SemanaInicio = new DateTime(2026, 4, 27),
                SemanaFin = new DateTime(2026, 5, 3),
                FechaPago = new DateTime(2026, 5, 3, 10, 0, 0),
                MetodoPago = "SINPE",
                Detalles =
                {
                    new RegistrarLiquidacionSemanalDetalleCommand
                    {
                        FuncionarioId = funcionario.IdFuncionario,
                        MontoPagado = 100m
                    }
                }
            });

            var controller = new DashboardController(
                ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider),
                new NoAssociateProfitAllocationService());
            var result = await controller.Index(4, 2026, CancellationToken.None);

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DashboardViewModel>(view.Model);

            // CAJA: en abril no salió plata hacia el funcionario (el pago se hizo el 3 de mayo)
            // y el único egreso del mes fue el alquiler.
            Assert.Equal(0m, model.TotalPagadoFuncionarios);
            Assert.Equal(50m, model.SalidasCajaMes);

            // ANALÍTICO: es el DEVENGADO del período, no lo pagado. El cobro de 700 con IVA deja
            // una base de 619,47 y el 50 % de comisión son 309,74. Es exactamente la misma línea
            // de "liquidaciones del equipo" que resta el estado de cuenta del inversionista: el
            // Dashboard y el reparto usan un solo motor.
            Assert.Equal(309.74m, model.TotalPagadoFuncionariosAnalitico);
            Assert.Equal(359.74m, model.TotalEgresosAnaliticos);
        }

        [Fact]
        public async Task Dashboard_ShouldAllocateLegacyPaymentsUsingRealCobroMonths_NotCalendarDays()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Luisa", 50m, 0m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 3, 30), 100m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 1), 100m);

            context.PagosFuncionarios.Add(new PagoFuncionario
            {
                FuncionarioId = funcionario.IdFuncionario,
                MontoPagado = 87m,
                FechaPago = new DateTime(2026, 4, 5, 9, 0, 0),
                InicioSemana = new DateTime(2026, 3, 30),
                FinSemana = new DateTime(2026, 4, 5),
                Observacion = "Pago legacy"
            });
            await context.SaveChangesAsync();

            var controller = new DashboardController(
                ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider),
                new NoAssociateProfitAllocationService());
            var result = await controller.Index(4, 2026, CancellationToken.None);

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DashboardViewModel>(view.Model);

            // Con el motor único la línea analítica es el DEVENGADO de abril, no el reparto del
            // pago legacy: el cobro del 1 de abril (100 con IVA → base 88,50) al 50 % da 44,25.
            // La atribución de pagos legacy sigue viva donde importa (liquidaciones y caja).
            Assert.Equal(44.25m, model.TotalPagadoFuncionariosAnalitico);
        }

        [Fact]
        public async Task CobrosIndex_ShouldIncludeInactiveFuncionarioPaymentsInDevengadoView()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Inactiva", 50m, 0m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 10), 100m);

            funcionario.Activo = false;
            await context.SaveChangesAsync();

            var controller = new CobrosController(
                ControllerTestSupport.CreateCobroService(context),
                ControllerTestSupport.CreateCobroQueryService(context, tenantProvider),
                ControllerTestSupport.CreateComprobanteCobroService(),
                ControllerTestSupport.BusinessDateTimeProvider,
                ControllerTestSupport.CreateTenantDisplayNameService());
            var result = await controller.Index(new CobroFiltroViewModel { VistaTiempo = "todo" });

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<CobroIndexViewModel>(view.Model);

            // Comisión con IVA incluido: base 100/1.13 × 50% ≈ 44.25 (antes 43.50 con el 0.87 incorrecto).
            Assert.Equal(44.25m, Math.Round(model.PagoColaboradores, 2));
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_ShouldCombineLegacyAndLiquidationPayments_WithoutDoubleCounting()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana", 50m, 0m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 14), 200m);

            context.PagosFuncionarios.Add(new PagoFuncionario
            {
                FuncionarioId = funcionario.IdFuncionario,
                MontoPagado = 20m,
                FechaPago = new DateTime(2026, 4, 18, 8, 0, 0),
                InicioSemana = new DateTime(2026, 4, 13),
                FinSemana = new DateTime(2026, 4, 19),
                Observacion = "Pago legacy"
            });

            context.Categorias.Add(new Categoria
            {
                Nombre = LiquidacionSemanalDefaults.CategoriaPagoFuncionarios,
                Detalle = "Auto"
            });
            await context.SaveChangesAsync();

            var categoria = await context.Categorias.SingleAsync();
            context.Egresos.Add(new Egreso
            {
                FechaEgreso = new DateTime(2026, 4, 19, 9, 0, 0),
                CategoriaId = categoria.Id,
                Monto = 30m,
                MetodoPago = "SINPE",
                Detalle = "Pago semanal"
            });
            await context.SaveChangesAsync();

            var egreso = await context.Egresos.SingleAsync();
            context.LiquidacionesSemanales.Add(new LiquidacionSemanal
            {
                SemanaInicio = new DateTime(2026, 4, 13),
                SemanaFin = new DateTime(2026, 4, 19),
                FechaPago = new DateTime(2026, 4, 19, 9, 0, 0),
                MontoTotal = 30m,
                Estado = LiquidacionSemanalDefaults.EstadoPagada,
                Observacion = "Liquidacion nueva",
                EgresoId = egreso.IdEgreso,
                FechaCreacion = new DateTime(2026, 4, 19, 9, 0, 0)
            });
            await context.SaveChangesAsync();

            var liquidacion = await context.LiquidacionesSemanales.SingleAsync();
            context.LiquidacionesSemanalesDetalle.Add(new LiquidacionSemanalDetalle
            {
                LiquidacionSemanalId = liquidacion.Id,
                FuncionarioId = funcionario.IdFuncionario,
                MontoServicios = 200m,
                MontoProductos = 0m,
                Impuestos = 26m,
                MontoNeto = 174m,
                MontoPagado = 30m,
                Pendiente = 37m
            });
            await context.SaveChangesAsync();

            var service = CreateService(context, tenantProvider);
            var resumen = await service.ObtenerResumenSemanaAsync(
                new DateTime(2026, 4, 13),
                new DateTime(2026, 4, 19));

            var pago = Assert.Single(resumen.Funcionarios);
            // IVA incluido: base = 200 / 1.13 = 176.99; comisión 50% = 88.50.
            Assert.Equal(88.50m, pago.PagoFinal);
            Assert.Equal(50m, pago.MontoPagado);
            Assert.Equal(38.50m, pago.MontoPendiente);
            Assert.Equal(2, pago.HistorialPagos.Count);
            Assert.Contains(pago.HistorialPagos, p => p.OrigenRegistro == "LEGACY");
            Assert.Contains(pago.HistorialPagos, p => p.OrigenRegistro == "LIQUIDACION");
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_Quincena_ShouldAggregateWeeklyPaymentWithinRange()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Camilo", 50m, 0m);
            // Producción dentro de la segunda quincena de junio (16–30).
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 6, 22), 200m);

            // Pago registrado por SEMANA (22–28 jun); su producción está toda dentro de la quincena 16–30.
            context.PagosFuncionarios.Add(new PagoFuncionario
            {
                FuncionarioId = funcionario.IdFuncionario,
                MontoPagado = 20m,
                FechaPago = new DateTime(2026, 6, 26, 8, 0, 0),
                InicioSemana = new DateTime(2026, 6, 22),
                FinSemana = new DateTime(2026, 6, 28),
                Observacion = "Pago semanal dentro de la quincena"
            });
            await context.SaveChangesAsync();

            var service = CreateService(context, tenantProvider);
            var resumen = await service.ObtenerResumenSemanaAsync(
                new DateTime(2026, 6, 16),
                new DateTime(2026, 6, 30));

            var pago = Assert.Single(resumen.Funcionarios);
            // Antes (coincidencia exacta de rango) esto era 0; ahora la quincena agrega el pago semanal.
            Assert.Equal(20m, pago.MontoPagado);
            Assert.Equal(20m, resumen.TotalPagadoGeneral);
            Assert.Single(pago.HistorialPagos);
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_ShouldAttributePaymentByProductionDate_NotByHeaderPeriod()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Comisión sobre total cobrado (sin IVA) → devengado = monto × 50%, aritmética limpia.
            var funcionario = await SeedFuncionarioAsync(context, "Cruz", 50m, 50m, rebajarImpuestos: false);

            // Producción de la semana 29 jun – 05 jul: parte en junio (quincena 16–30) y parte en julio.
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 6, 29), 1000m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 6, 30), 1000m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 7, 3), 2000m);

            // Pago por la semana completa (encabezado 29 jun – 05 jul), monto = planilla de la semana = 2000.
            context.PagosFuncionarios.Add(new PagoFuncionario
            {
                FuncionarioId = funcionario.IdFuncionario,
                MontoPagado = 2000m,
                FechaPago = new DateTime(2026, 7, 6, 8, 0, 0),
                InicioSemana = new DateTime(2026, 6, 29),
                FinSemana = new DateTime(2026, 7, 5),
                Observacion = "Pago semanal que cruza el fin de mes"
            });
            await context.SaveChangesAsync();

            var service = CreateService(context, tenantProvider);

            // Semana completa: aplica el pago completo (toda su producción está dentro).
            var semana = await service.ObtenerResumenSemanaAsync(new DateTime(2026, 6, 29), new DateTime(2026, 7, 5));
            var pagoSemana = Assert.Single(semana.Funcionarios);
            Assert.Equal(2000m, pagoSemana.PagoFinal);
            Assert.Equal(2000m, pagoSemana.MontoPagado);
            Assert.Equal(0m, pagoSemana.MontoPendiente);

            // Quincena 16–30 jun: SOLO cuenta la producción del 29 y 30 (mitad del pago), NO el pago completo.
            var quincena = await service.ObtenerResumenSemanaAsync(new DateTime(2026, 6, 16), new DateTime(2026, 6, 30));
            var pagoQuincena = Assert.Single(quincena.Funcionarios);
            Assert.Equal(1000m, pagoQuincena.PagoFinal);    // planilla de la producción 29–30
            Assert.Equal(1000m, pagoQuincena.MontoPagado);   // pago prorrateado: 2000 × (1000/2000)
            Assert.Equal(0m, pagoQuincena.MontoPendiente);
            Assert.Equal(0m, pagoQuincena.Excedente);

            // El diagnóstico debe mostrar el pago incluido con fracción 0.5 en la quincena.
            var diag = await service.ObtenerDiagnosticoPagosAsync(new DateTime(2026, 6, 16), new DateTime(2026, 6, 30));
            var entrada = Assert.Single(diag);
            Assert.True(entrada.Incluido);
            Assert.Equal(0.5m, entrada.Fraccion);
            Assert.Equal(1000m, entrada.MontoAplicado);
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_ShouldNotDoubleCount_WhenWeekPaidThenQuincenaRemainderPaid()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Nadia", 50m, 50m, rebajarImpuestos: false);

            // Producción quincena 16–30: 1000 el 16 (semana A) y 1000 el 25 (resto de la quincena).
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 6, 16), 1000m);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 6, 25), 1000m);

            // Pago 1: semana 16–22 pagada completa (500).
            context.PagosFuncionarios.Add(new PagoFuncionario
            {
                FuncionarioId = funcionario.IdFuncionario,
                MontoPagado = 500m,
                FechaPago = new DateTime(2026, 6, 22, 8, 0, 0),
                InicioSemana = new DateTime(2026, 6, 16),
                FinSemana = new DateTime(2026, 6, 22),
                Observacion = "Pago semana 16-22"
            });
            // Pago 2: "Pagar quincena" por el resto (500), encabezado 16–30 pero paga producción del 25.
            context.PagosFuncionarios.Add(new PagoFuncionario
            {
                FuncionarioId = funcionario.IdFuncionario,
                MontoPagado = 500m,
                FechaPago = new DateTime(2026, 6, 30, 8, 0, 0),
                InicioSemana = new DateTime(2026, 6, 16),
                FinSemana = new DateTime(2026, 6, 30),
                Observacion = "Pago quincena (resto)"
            });
            await context.SaveChangesAsync();

            var service = CreateService(context, tenantProvider);

            // Ver la semana 16–22: NO debe re-contar el pago de quincena (su producción real es del 25).
            var semana = await service.ObtenerResumenSemanaAsync(new DateTime(2026, 6, 16), new DateTime(2026, 6, 22));
            var pagoSemana = Assert.Single(semana.Funcionarios);
            Assert.Equal(500m, pagoSemana.PagoFinal);
            Assert.Equal(500m, pagoSemana.MontoPagado);   // solo el pago de la semana, sin bleed
            Assert.Equal(0m, pagoSemana.MontoPendiente);
            Assert.Equal(0m, pagoSemana.Excedente);

            // Ver la quincena 16–30: ambos pagos aplican, suma exacta, sin pendiente ni excedente.
            var quincena = await service.ObtenerResumenSemanaAsync(new DateTime(2026, 6, 16), new DateTime(2026, 6, 30));
            var pagoQuincena = Assert.Single(quincena.Funcionarios);
            Assert.Equal(1000m, pagoQuincena.PagoFinal);
            Assert.Equal(1000m, pagoQuincena.MontoPagado);
            Assert.Equal(0m, pagoQuincena.MontoPendiente);
            Assert.Equal(0m, pagoQuincena.Excedente);
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_Overpayment_ShouldClampPendingToZeroAndReportExcedente()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Sobrepago", 50m, 0m);
            // 100 => base 88.50, comisión 50% = 44.25.
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 15), 100m);

            // Pago existente (legacy) mayor a lo devengado: simula un sobrepago ya registrado.
            context.PagosFuncionarios.Add(new PagoFuncionario
            {
                FuncionarioId = funcionario.IdFuncionario,
                MontoPagado = 60m,
                FechaPago = new DateTime(2026, 4, 18, 8, 0, 0),
                InicioSemana = new DateTime(2026, 4, 13),
                FinSemana = new DateTime(2026, 4, 19),
                Observacion = "Sobrepago"
            });
            await context.SaveChangesAsync();

            var service = CreateService(context, tenantProvider);
            var resumen = await service.ObtenerResumenSemanaAsync(
                new DateTime(2026, 4, 13),
                new DateTime(2026, 4, 19));

            var pago = Assert.Single(resumen.Funcionarios);
            Assert.Equal(44.25m, pago.PagoFinal);
            Assert.Equal(60m, pago.MontoPagado);
            Assert.Equal(0m, pago.MontoPendiente);        // nunca negativo
            Assert.Equal(15.75m, pago.Excedente);          // 60 − 44.25
            Assert.Equal(44.25m, pago.MontoPagadoAplicado);
            Assert.Equal(0m, resumen.TotalPendienteGeneral);
            Assert.Equal(15.75m, resumen.TotalExcedenteGeneral);
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_ShouldRespectTenantIsolation()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionarioA = await SeedFuncionarioAsync(context, "Ana", 50m, 0m);
            await SeedCobroServicioAsync(context, funcionarioA.IdFuncionario, new DateTime(2026, 4, 14), 100m);

            context.ChangeTracker.Clear();
            tenantProvider.TenantId = tenantB;

            var funcionarioB = await SeedFuncionarioAsync(context, "Beto", 50m, 0m);
            await SeedCobroServicioAsync(context, funcionarioB.IdFuncionario, new DateTime(2026, 4, 14), 300m);

            context.ChangeTracker.Clear();
            tenantProvider.TenantId = tenantA;

            var service = CreateService(context, tenantProvider);
            var resumen = await service.ObtenerResumenSemanaAsync(
                new DateTime(2026, 4, 13),
                new DateTime(2026, 4, 19));

            var pago = Assert.Single(resumen.Funcionarios);
            Assert.Equal("Ana", pago.Nombre);
            Assert.Equal(100m, resumen.TotalGeneradoServicios);
            Assert.Equal(100m, resumen.TotalGeneradoGeneral);
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_WithRebajaImpuestos_ShouldComputePagoOnNetBase()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Caso 1: comisión sobre base sin IVA (IVA incluido). 70000 => base 61946.90,
            // IVA 8053.10, comisión 50% sobre base = 30973.45.
            var funcionario = await SeedFuncionarioAsync(context, "Ana", 50m, 50m, rebajarImpuestos: true);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 14), 70000m);

            var resumen = await CreateService(context, tenantProvider).ObtenerResumenSemanaAsync(
                new DateTime(2026, 4, 13),
                new DateTime(2026, 4, 19));

            var pago = Assert.Single(resumen.Funcionarios);
            Assert.Equal(70000m, pago.TotalGenerado);
            Assert.Equal(8053.10m, pago.Impuestos);
            Assert.Equal(61946.90m, pago.TotalNeto);
            Assert.Equal(30973.45m, pago.PagoFinal);
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_WithoutRebajaImpuestos_ShouldComputePagoOnGross()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Caso 2: comisión sobre total cobrado. 70000 * 50% => 35000. El IVA de la venta
            // (KPI del negocio) es siempre IVA incluido: base 61946.90, IVA 8053.10.
            var funcionario = await SeedFuncionarioAsync(context, "Beto", 50m, 50m, rebajarImpuestos: false);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 14), 70000m);

            var resumen = await CreateService(context, tenantProvider).ObtenerResumenSemanaAsync(
                new DateTime(2026, 4, 13),
                new DateTime(2026, 4, 19));

            var pago = Assert.Single(resumen.Funcionarios);
            Assert.Equal(70000m, pago.TotalGenerado);
            Assert.Equal(8053.10m, pago.Impuestos);
            Assert.Equal(61946.90m, pago.TotalNeto);
            Assert.Equal(35000m, pago.PagoFinal);
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_WithZeroPercent_ShouldPayZero()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Cero", 0m, 0m, rebajarImpuestos: false);
            await SeedCobroServicioAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 14), 70000m);

            var resumen = await CreateService(context, tenantProvider).ObtenerResumenSemanaAsync(
                new DateTime(2026, 4, 13),
                new DateTime(2026, 4, 19));

            var pago = Assert.Single(resumen.Funcionarios);
            Assert.Equal(0m, pago.PagoFinal);
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_WithZeroProduction_ShouldPayZero()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Funcionario activo sin cobros en la semana => pago 0, sin fallar.
            await SeedFuncionarioAsync(context, "SinProduccion", 50m, 50m, rebajarImpuestos: false);

            var resumen = await CreateService(context, tenantProvider).ObtenerResumenSemanaAsync(
                new DateTime(2026, 4, 13),
                new DateTime(2026, 4, 19));

            var pago = Assert.Single(resumen.Funcionarios);
            Assert.Equal(0m, pago.TotalGenerado);
            Assert.Equal(0m, pago.PagoFinal);
        }

        [Fact]
        public async Task ObtenerResumenSemanaAsync_Productos_ShouldRespectRebajaImpuestosFlag()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Comisión por producto: 70000 / 50% sin rebaja => 35000.
            var funcionario = await SeedFuncionarioAsync(context, "ProdSinRebaja", 0m, 50m, rebajarImpuestos: false);
            await SeedCobroProductoAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 14), 70000m);

            var resumen = await CreateService(context, tenantProvider).ObtenerResumenSemanaAsync(
                new DateTime(2026, 4, 13),
                new DateTime(2026, 4, 19));

            var pago = Assert.Single(resumen.Funcionarios);
            Assert.Equal(70000m, pago.TotalProductos);
            Assert.Equal(35000m, pago.PagoFinal);

            var producto = Assert.Single(pago.ProductosVendidos);
            Assert.Equal(35000m, producto.GananciaFuncionario);
        }

        private static LiquidacionSemanalService CreateService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            LuxuryApp.Services.Tenant.ITenantProvider tenantProvider) =>
            new(
                context,
                ControllerTestSupport.BusinessDateTimeProvider,
                new LuxuryApp.Services.Fiscal.TaxCalculationService(),
                new LuxuryApp.Services.Fiscal.LiquidacionFuncionarioService(),
                new LuxuryApp.Services.Fiscal.TenantFiscalConfigService(context, tenantProvider),
                ControllerTestSupport.CreateSystemCategoryService(context),
                new FakePlatformAuditService(),
                tenantProvider,
                NullLogger<LiquidacionSemanalService>.Instance);

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            decimal porcentajeGanancia,
            decimal porcentajeProducto,
            bool rebajarImpuestos = true)
        {
            context.Puestos.Add(new Puesto
            {
                NombrePuesto = $"Puesto {nombre}",
                Detalle = "General",
                Activo = true
            });
            await context.SaveChangesAsync();

            var puesto = await context.Puestos.SingleAsync(p => p.NombrePuesto == $"Puesto {nombre}");

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = porcentajeGanancia,
                PorcentajeProducto = porcentajeProducto,
                RebajarImpuestosAntesDeComision = rebajarImpuestos,
                // La fuente de verdad del cálculo es ComisionCalculadaSobre; se mantiene coherente
                // con el flag histórico (true → BaseSinIva, false → TotalCobrado).
                ComisionCalculadaSobre = rebajarImpuestos
                    ? LuxuryApp.Models.Fiscal.ComisionCalculadaSobre.BaseSinIva
                    : LuxuryApp.Models.Fiscal.ComisionCalculadaSobre.TotalCobrado,
                FechaIngreso = new DateTime(2026, 4, 13),
                Activo = true
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task SeedCobroProductoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            DateTime fecha,
            decimal monto)
        {
            var producto = new LuxuryApp.Models.Productos.Producto
            {
                NombreProducto = $"Producto {fecha:yyyyMMddHHmmss}",
                PrecioProducto = monto,
                CantidadProducto = 100,
                Activo = true
            };
            context.Productos.Add(producto);
            await context.SaveChangesAsync();

            context.Cobros.Add(new Cobro
            {
                NombreCliente = "Cliente Test",
                FuncionarioId = funcionarioId,
                FechaCobro = fecha,
                Monto = monto,
                MetodoPago = "EFECTIVO",
                ProductoId = producto.IdProducto
            });

            await context.SaveChangesAsync();
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
