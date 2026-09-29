using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.Inversionistas
{
    /// <summary>
    /// Ciclo de vida del estado de cuenta contra base de datos real (SQLite en memoria), usando el
    /// motor fiscal y el servicio de liquidaciones de producción.
    /// </summary>
    public class InvestorStatementServiceTests
    {
        private static readonly DateOnly PeriodoJunio = new(2026, 6, 1);
        private static readonly DateTime FechaCobro = new(2026, 6, 10);

        // ─────────────── Cálculo ───────────────

        [Fact]
        public async Task Generar_ConGananciaDeUnMillon_Y45Porciento_Da450Mil()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            // Servicio exento de IVA para que el ingreso neto sea exactamente 1.000.000.
            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio Northside", "partner@northside-studio.example", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(1_000_000m, statement.IngresosNetos);
            Assert.Equal(0m, statement.GastosElegibles);
            Assert.Equal(0m, statement.Liquidaciones);
            Assert.Equal(1_000_000m, statement.GananciaDistribuible);
            Assert.Equal(45m, statement.ParticipacionPorcentaje);
            Assert.Equal(450_000m, statement.ParticipacionCalculada);
            Assert.Equal(450_000m, statement.SaldoPendiente);
        }

        [Fact]
        public async Task Generar_ExcluyeElIvaDeLosIngresos()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            // Cobro de 1.130 con IVA incluido → base 1.000, IVA 130.
            await InvestorTestSupport.SeedCobroConIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_130m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 50m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(1_130m, statement.IngresosCobrados);
            Assert.Equal(1_000m, statement.IngresosNetos);
            Assert.Equal(130m, statement.IvaExcluido);
        }

        [Fact]
        public async Task Generar_RestaLosGastosElegiblesYRespetaLasCategoriasExcluidas()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            await InvestorTestSupport.SeedEgresoAsync(fixture.Context, "Alquiler", FechaCobro, 200_000m);

            // Estas dos categorías NUNCA cuentan: pagos al equipo (ya van en liquidaciones) y
            // pagos al inversionista (recursividad).
            await InvestorTestSupport.SeedEgresoAsync(
                fixture.Context, LiquidacionSemanalDefaults.CategoriaPagoFuncionarios, FechaCobro, 300_000m);
            await InvestorTestSupport.SeedEgresoAsync(
                fixture.Context, InvestorDefaults.CategoriaDistribucionInversionistas, FechaCobro, 450_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(200_000m, statement.GastosElegibles);
            Assert.Equal(800_000m, statement.GananciaDistribuible);
            Assert.Equal(360_000m, statement.ParticipacionCalculada);
        }

        [Fact]
        public async Task Generar_UnPagoAlInversionistaNoReduceRecursivamenteLaGanancia()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.FinalizeAsync(statementId, "admin", default);

            await fixture.Statements.RegisterPaymentAsync(
                new InvestorPaymentFormViewModel
                {
                    StatementId = statementId,
                    Fecha = new DateTime(2026, 6, 20),
                    Monto = 450_000m,
                    MetodoPago = "SINPE"
                },
                "admin", "admin@test.cr", default);

            // El negocio registra la salida de caja en la categoría reservada.
            await InvestorTestSupport.SeedEgresoAsync(
                fixture.Context,
                InvestorDefaults.CategoriaDistribucionInversionistas,
                new DateTime(2026, 6, 20),
                450_000m);

            // Un periodo nuevo con los mismos ingresos debe dar la MISMA ganancia: el pago no la tocó.
            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, new DateTime(2026, 7, 10), 1_000_000m);

            var julioId = await fixture.Statements.GenerateDraftAsync(
                investorId, new DateOnly(2026, 7, 1), "admin", default);

            var julio = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == julioId);

            Assert.Equal(0m, julio.GastosElegibles);
            Assert.Equal(1_000_000m, julio.GananciaDistribuible);
            Assert.Equal(450_000m, julio.ParticipacionCalculada);
        }

        [Fact]
        public async Task Generar_IncluyeLasLiquidacionesDelEquipo()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            // Colaborador con 50 % de comisión sobre la base sin IVA.
            var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(
                fixture.Context, "Comisionista", porcentajeGanancia: 50m);

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, funcionario.IdFuncionario, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(500_000m, statement.Liquidaciones);
            Assert.Equal(500_000m, statement.GananciaDistribuible);
            Assert.Equal(225_000m, statement.ParticipacionCalculada);
        }

        [Fact]
        public async Task Generar_ConLiquidacionesDesactivadas_NoLasResta()
        {
            using var fixture = await InvestorFixture.CreateAsync(policy =>
            {
                policy.IncluirLiquidaciones = false;
            });

            var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(
                fixture.Context, "Comisionista", porcentajeGanancia: 50m);

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, funcionario.IdFuncionario, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(0m, statement.Liquidaciones);
            Assert.Equal(1_000_000m, statement.GananciaDistribuible);
        }

        [Fact]
        public async Task Generar_ConCategoriasSoloSeleccionadas_IgnoraElRestoDeLosGastos()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var alquilerId = await InvestorTestSupport.SeedEgresoAsync(
                fixture.Context, "Alquiler", FechaCobro, 200_000m);
            await InvestorTestSupport.SeedEgresoAsync(fixture.Context, "Marketing", FechaCobro, 100_000m);

            var policy = await fixture.Context.InvestorProfitPolicies.SingleAsync();
            policy.ModoCategoriasGasto = InvestorExpenseCategoryMode.SoloSeleccionadas;
            fixture.Context.InvestorPolicyExpenseCategories.Add(new InvestorPolicyExpenseCategory
            {
                PolicyId = policy.Id,
                CategoriaId = alquilerId
            });
            await fixture.Context.SaveChangesAsync();

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(200_000m, statement.GastosElegibles);
        }

        // ─────────────── Ajustes ───────────────

        [Fact]
        public async Task Ajustes_PositivoYNegativo_SeAplicanAlBorrador()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 50m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);

            await fixture.Statements.AddAdjustmentAsync(
                new InvestorAdjustmentFormViewModel
                {
                    StatementId = statementId,
                    Monto = 100_000m,
                    Descripcion = "Reintegro de proveedor"
                },
                "admin", "admin@test.cr", default);

            await fixture.Statements.AddAdjustmentAsync(
                new InvestorAdjustmentFormViewModel
                {
                    StatementId = statementId,
                    Monto = -40_000m,
                    Descripcion = "Corrección de un cobro duplicado"
                },
                "admin", "admin@test.cr", default);

            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(100_000m, statement.AjustesPositivos);
            Assert.Equal(40_000m, statement.AjustesNegativos);
            Assert.Equal(1_060_000m, statement.GananciaDistribuible);
            Assert.Equal(530_000m, statement.ParticipacionCalculada);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.InvestorStatementAdjustmentAdded));
        }

        [Fact]
        public async Task Ajuste_SinDescripcion_EsRechazado()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 50m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);

            await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Statements.AddAdjustmentAsync(
                    new InvestorAdjustmentFormViewModel
                    {
                        StatementId = statementId,
                        Monto = 1_000m,
                        Descripcion = "   "
                    },
                    "admin", "admin@test.cr", default));
        }

        // ─────────────── Pérdidas ───────────────

        [Fact]
        public async Task Perdida_ConArrastre_SeDescuentaDelPeriodoSiguiente()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            // Junio en pérdida: 200.000 de gasto y sin ingresos.
            await InvestorTestSupport.SeedEgresoAsync(fixture.Context, "Alquiler", FechaCobro, 200_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context,
                "Socio",
                "socio@test.cr",
                50m,
                new DateOnly(2026, 1, 1),
                perdidas: InvestorLossTreatment.CarryForward);

            var junioId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.FinalizeAsync(junioId, "admin", default);

            var junio = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == junioId);
            Assert.Equal(0m, junio.GananciaDistribuible);
            Assert.Equal(200_000m, junio.PerdidaPendiente);

            // Julio con 500.000 de ingreso: se descuenta la pérdida antes de repartir.
            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, new DateTime(2026, 7, 10), 500_000m);

            var julioId = await fixture.Statements.GenerateDraftAsync(
                investorId, new DateOnly(2026, 7, 1), "admin", default);

            var julio = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == julioId);

            Assert.Equal(200_000m, julio.PerdidaArrastrada);
            Assert.Equal(300_000m, julio.GananciaDistribuible);
            Assert.Equal(150_000m, julio.ParticipacionCalculada);
            Assert.Equal(0m, julio.PerdidaPendiente);
        }

        [Fact]
        public async Task Perdida_SinArrastre_NoAfectaAlPeriodoSiguiente()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedEgresoAsync(fixture.Context, "Alquiler", FechaCobro, 200_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 50m, new DateOnly(2026, 1, 1));

            var junioId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.FinalizeAsync(junioId, "admin", default);

            var junio = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == junioId);
            Assert.Equal(0m, junio.PerdidaPendiente);

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, new DateTime(2026, 7, 10), 500_000m);

            var julioId = await fixture.Statements.GenerateDraftAsync(
                investorId, new DateOnly(2026, 7, 1), "admin", default);

            var julio = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == julioId);

            Assert.Equal(0m, julio.PerdidaArrastrada);
            Assert.Equal(500_000m, julio.GananciaDistribuible);
        }

        // ─────────────── Borrador vs finalizado ───────────────

        [Fact]
        public async Task Borrador_SeRecalculaConLosDatosActuales()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 500_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 50m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 500_000m);

            await fixture.Statements.RecalculateAsync(statementId, "admin", default);

            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(1_000_000m, statement.IngresosNetos);
            Assert.Equal(500_000m, statement.ParticipacionCalculada);
        }

        [Fact]
        public async Task Finalizado_EsInmutableAunqueCambienLosCobros()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.FinalizeAsync(statementId, "admin", default);

            // Alguien edita el pasado: el snapshot NO se mueve.
            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 5_000_000m);

            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);
            Assert.Equal(1_000_000m, statement.IngresosNetos);
            Assert.Equal(450_000m, statement.ParticipacionCalculada);

            // Y un recálculo explícito tampoco: hay que reabrir o anular.
            var error = await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Statements.RecalculateAsync(statementId, "admin", default));

            Assert.Contains("borrador", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.InvestorStatementFinalized));
        }

        [Fact]
        public async Task Reabrir_DevuelveElEstadoABorradorYQuedaAuditado()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.FinalizeAsync(statementId, "admin", default);
            await fixture.Statements.ReopenAsync(statementId, "Faltó registrar un gasto", "admin", default);

            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(InvestorStatementStatus.Draft, statement.Estado);
            Assert.Null(statement.FinalizadoAtUtc);
            Assert.Equal("Faltó registrar un gasto", statement.MotivoReapertura);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.InvestorStatementReopened));
        }

        [Fact]
        public async Task Anular_ExigeMotivoYBloqueaSiHayPagos()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.FinalizeAsync(statementId, "admin", default);

            await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Statements.VoidAsync(statementId, "  ", "admin", default));

            await fixture.Statements.RegisterPaymentAsync(
                new InvestorPaymentFormViewModel
                {
                    StatementId = statementId,
                    Fecha = new DateTime(2026, 6, 20),
                    Monto = 100_000m,
                    MetodoPago = "EFECTIVO"
                },
                "admin", "admin@test.cr", default);

            await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Statements.VoidAsync(statementId, "Error de cálculo", "admin", default));
        }

        // ─────────────── Idempotencia ───────────────

        [Fact]
        public async Task Generar_DosVeces_DevuelveElMismoEstado()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var primero = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            var segundo = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);

            Assert.Equal(primero, segundo);
            Assert.Equal(1, await fixture.Context.InvestorStatements.CountAsync());
        }

        [Fact]
        public async Task Generar_TrasAnular_PermiteUnEstadoNuevoDelMismoPeriodo()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var primero = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.VoidAsync(primero, "Se generó por error", "admin", default);

            var segundo = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);

            Assert.NotEqual(primero, segundo);
            Assert.Equal(2, await fixture.Context.InvestorStatements.CountAsync());
        }

        // ─────────────── Pagos ───────────────

        [Fact]
        public async Task Pago_Parcial_DejaElEstadoEnPagoParcial()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.FinalizeAsync(statementId, "admin", default);

            await fixture.Statements.RegisterPaymentAsync(
                new InvestorPaymentFormViewModel
                {
                    StatementId = statementId,
                    Fecha = new DateTime(2026, 6, 20),
                    Monto = 200_000m,
                    MetodoPago = "SINPE"
                },
                "admin", "admin@test.cr", default);

            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(InvestorStatementStatus.PartiallyPaid, statement.Estado);
            Assert.Equal(200_000m, statement.TotalPagado);
            Assert.Equal(250_000m, statement.SaldoPendiente);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.InvestorPaymentRegistered));
        }

        [Fact]
        public async Task Pago_Total_DejaElEstadoEnPagado()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.FinalizeAsync(statementId, "admin", default);

            await fixture.Statements.RegisterPaymentAsync(
                new InvestorPaymentFormViewModel
                {
                    StatementId = statementId,
                    Fecha = new DateTime(2026, 6, 20),
                    Monto = 450_000m,
                    MetodoPago = "SINPE"
                },
                "admin", "admin@test.cr", default);

            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);

            Assert.Equal(InvestorStatementStatus.Paid, statement.Estado);
            Assert.Equal(0m, statement.SaldoPendiente);
        }

        [Fact]
        public async Task Pago_NoPuedeSuperarElSaldoPendiente()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.FinalizeAsync(statementId, "admin", default);

            var error = await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Statements.RegisterPaymentAsync(
                    new InvestorPaymentFormViewModel
                    {
                        StatementId = statementId,
                        Fecha = new DateTime(2026, 6, 20),
                        Monto = 450_001m,
                        MetodoPago = "SINPE"
                    },
                    "admin", "admin@test.cr", default));

            Assert.Contains("supera el saldo pendiente", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Pago_SobreUnBorrador_EsRechazado()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);

            await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Statements.RegisterPaymentAsync(
                    new InvestorPaymentFormViewModel
                    {
                        StatementId = statementId,
                        Fecha = new DateTime(2026, 6, 20),
                        Monto = 1_000m,
                        MetodoPago = "SINPE"
                    },
                    "admin", "admin@test.cr", default));
        }

        [Fact]
        public async Task RevertirPago_CreaUnMovimientoCompensatorioAuditado()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            await InvestorTestSupport.SeedCobroSinIvaAsync(
                fixture.Context, fixture.FuncionarioId, FechaCobro, 1_000_000m);

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio", "socio@test.cr", 45m, new DateOnly(2026, 1, 1));

            var statementId = await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);
            await fixture.Statements.FinalizeAsync(statementId, "admin", default);

            await fixture.Statements.RegisterPaymentAsync(
                new InvestorPaymentFormViewModel
                {
                    StatementId = statementId,
                    Fecha = new DateTime(2026, 6, 20),
                    Monto = 450_000m,
                    MetodoPago = "SINPE"
                },
                "admin", "admin@test.cr", default);

            var pago = await fixture.Context.InvestorDistributionPayments.SingleAsync();
            await fixture.Statements.ReversePaymentAsync(pago.Id, "Se pagó al socio equivocado", "admin", "admin@test.cr", default);

            var statement = await fixture.Context.InvestorStatements.SingleAsync(s => s.Id == statementId);
            var movimientos = await fixture.Context.InvestorDistributionPayments.ToListAsync();

            // El pago original NO se borra: queda la traza completa.
            Assert.Equal(2, movimientos.Count);
            Assert.Contains(movimientos, m => m.EsReversion && m.Monto == -450_000m);
            Assert.Equal(0m, statement.TotalPagado);
            Assert.Equal(450_000m, statement.SaldoPendiente);
            Assert.Equal(InvestorStatementStatus.Finalized, statement.Estado);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.InvestorPaymentReversed));
        }

        // ─────────────── Aislamiento entre tenants ───────────────

        [Fact]
        public async Task Estados_NoSeVenEntreTenants()
        {
            using var fixture = await InvestorFixture.CreateAsync();

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context, "Socio A", "socio@a.test", 45m, new DateOnly(2026, 1, 1));

            await fixture.Statements.GenerateDraftAsync(investorId, PeriodoJunio, "admin", default);

            Assert.Equal(1, await fixture.Context.InvestorStatements.CountAsync());

            // El mismo contexto, otro tenant: el filtro global no debe dejar ver nada.
            fixture.TenantProvider.TenantId = Guid.NewGuid();

            Assert.Equal(0, await fixture.Context.InvestorStatements.CountAsync());
            Assert.Equal(0, await fixture.Context.TenantInvestors.CountAsync());

            var otroFixtureStatements = InvestorTestSupport.CreateStatementService(
                fixture.Context, fixture.TenantProvider, fixture.Audit);

            await Assert.ThrowsAsync<InvestorValidationException>(() =>
                otroFixtureStatements.PreviewAsync(investorId, PeriodoJunio, default));
        }

        /// <summary>Contexto SQLite + servicios reales, con un tenant y un colaborador base.</summary>
        private sealed class InvestorFixture : IDisposable
        {
            public required ProyectoIdentity.Datos.ApplicationDbContext Context { get; init; }

            public required Microsoft.Data.Sqlite.SqliteConnection Connection { get; init; }

            public required TestTenantProvider TenantProvider { get; init; }

            public required FakePlatformAuditService Audit { get; init; }

            public required InvestorStatementService Statements { get; init; }

            public required InvestorService Investors { get; init; }

            public int FuncionarioId { get; init; }

            public static async Task<InvestorFixture> CreateAsync(Action<InvestorProfitPolicy>? policy = null)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
                var audit = new FakePlatformAuditService();

                await InvestorTestSupport.SeedPolicyAsync(context, policy);

                // Colaborador sin comisión: aísla el cálculo salvo cuando el test la quiere.
                var funcionario = await InvestorTestSupport.SeedFuncionarioAsync(context, "Base");

                var investors = InvestorTestSupport.CreateInvestorService(context, audit);

                return new InvestorFixture
                {
                    Context = context,
                    Connection = connection,
                    TenantProvider = tenantProvider,
                    Audit = audit,
                    Investors = investors,
                    Statements = InvestorTestSupport.CreateStatementService(context, tenantProvider, audit, investors),
                    FuncionarioId = funcionario.IdFuncionario
                };
            }

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }
    }
}
