using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddInvestorDistributionAndRecurringSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvestorProfitPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExcluirIva = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IncluirLiquidaciones = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    BaseLiquidaciones = table.Column<int>(type: "int", nullable: false),
                    ModoCategoriasGasto = table.Column<int>(type: "int", nullable: false),
                    TratamientoPerdidasPorDefecto = table.Column<int>(type: "int", nullable: false),
                    FrecuenciaPorDefecto = table.Column<int>(type: "int", nullable: false),
                    GeneracionAutomatica = table.Column<bool>(type: "bit", nullable: false),
                    EnvioAutomatico = table.Column<bool>(type: "bit", nullable: false),
                    DiasEsperaGeneracion = table.Column<int>(type: "int", nullable: false),
                    HoraGeneracion = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestorProfitPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurringScheduleRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    HoraInicio = table.Column<TimeOnly>(type: "time", nullable: false),
                    HoraFin = table.Column<TimeOnly>(type: "time", nullable: false),
                    DiasSemanaMask = table.Column<int>(type: "int", nullable: false),
                    VigenteDesde = table.Column<DateOnly>(type: "date", nullable: false),
                    VigenteHasta = table.Column<DateOnly>(type: "date", nullable: true),
                    Activa = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Alcance = table.Column<int>(type: "int", nullable: false),
                    IncluirNuevosColaboradores = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    EtiquetaCalendario = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Motivo = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ReglaOrigenId = table.Column<int>(type: "int", nullable: true),
                    CreadoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ActualizadoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringScheduleRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringScheduleRules_RecurringScheduleRules_ReglaOrigenId",
                        column: x => x.ReglaOrigenId,
                        principalTable: "RecurringScheduleRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantInvestors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Telefono = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    NotasInternas = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantInvestors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvestorPolicyExpenseCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<int>(type: "int", nullable: false),
                    CategoriaId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestorPolicyExpenseCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestorPolicyExpenseCategories_Categorias_CategoriaId",
                        column: x => x.CategoriaId,
                        principalTable: "Categorias",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvestorPolicyExpenseCategories_InvestorProfitPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "InvestorProfitPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecurringScheduleExceptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<int>(type: "int", nullable: false),
                    FuncionarioId = table.Column<int>(type: "int", nullable: true),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    HoraInicioAlternativa = table.Column<TimeOnly>(type: "time", nullable: true),
                    HoraFinAlternativa = table.Column<TimeOnly>(type: "time", nullable: true),
                    Motivo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreadoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringScheduleExceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringScheduleExceptions_Funcionarios_FuncionarioId",
                        column: x => x.FuncionarioId,
                        principalTable: "Funcionarios",
                        principalColumn: "IdFuncionario",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurringScheduleExceptions_RecurringScheduleRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "RecurringScheduleRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecurringScheduleRuleTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<int>(type: "int", nullable: false),
                    FuncionarioId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringScheduleRuleTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringScheduleRuleTargets_Funcionarios_FuncionarioId",
                        column: x => x.FuncionarioId,
                        principalTable: "Funcionarios",
                        principalColumn: "IdFuncionario",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurringScheduleRuleTargets_RecurringScheduleRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "RecurringScheduleRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvestorAgreements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvestorId = table.Column<int>(type: "int", nullable: false),
                    ParticipacionPorcentaje = table.Column<decimal>(type: "decimal(9,4)", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Frecuencia = table.Column<int>(type: "int", nullable: false),
                    TratamientoPerdidas = table.Column<int>(type: "int", nullable: false),
                    EnvioAutomatico = table.Column<bool>(type: "bit", nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Notas = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestorAgreements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestorAgreements_TenantInvestors_InvestorId",
                        column: x => x.InvestorId,
                        principalTable: "TenantInvestors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvestorStatements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvestorId = table.Column<int>(type: "int", nullable: false),
                    AgreementId = table.Column<int>(type: "int", nullable: true),
                    PeriodoInicio = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodoFin = table.Column<DateOnly>(type: "date", nullable: false),
                    Frecuencia = table.Column<int>(type: "int", nullable: false),
                    IngresosCobrados = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IvaExcluido = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IngresosNetos = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    GastosElegibles = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Liquidaciones = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AjustesPositivos = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AjustesNegativos = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PerdidaArrastrada = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PerdidaPendiente = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    GananciaDistribuible = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ParticipacionPorcentaje = table.Column<decimal>(type: "decimal(9,4)", nullable: false),
                    ParticipacionCalculada = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalPagado = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SaldoPendiente = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Estado = table.Column<int>(type: "int", nullable: false),
                    FechaCalculoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneradoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    PoliticaVersion = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FinalizadoAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinalizadoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    EnviadoAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AnuladoAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AnuladoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    MotivoAnulacion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReabiertoAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReabiertoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    MotivoReapertura = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestorStatements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestorStatements_InvestorAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "InvestorAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InvestorStatements_TenantInvestors_InvestorId",
                        column: x => x.InvestorId,
                        principalTable: "TenantInvestors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvestorDistributionPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatementId = table.Column<int>(type: "int", nullable: false),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MetodoPago = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Referencia = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Notas = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EsReversion = table.Column<bool>(type: "bit", nullable: false),
                    Motivo = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RegistradoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RegistradoPorEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestorDistributionPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestorDistributionPayments_InvestorStatements_StatementId",
                        column: x => x.StatementId,
                        principalTable: "InvestorStatements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvestorStatementAdjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatementId = table.Column<int>(type: "int", nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Descripcion = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreadoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreadoPorEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestorStatementAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestorStatementAdjustments_InvestorStatements_StatementId",
                        column: x => x.StatementId,
                        principalTable: "InvestorStatements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvestorStatementEmailLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatementId = table.Column<int>(type: "int", nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsTest = table.Column<bool>(type: "bit", nullable: false),
                    ResendSequence = table.Column<int>(type: "int", nullable: false),
                    TriggeredByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestorStatementEmailLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestorStatementEmailLogs_InvestorStatements_StatementId",
                        column: x => x.StatementId,
                        principalTable: "InvestorStatements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvestorAgreements_InvestorId",
                table: "InvestorAgreements",
                column: "InvestorId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorAgreements_TenantId",
                table: "InvestorAgreements",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorAgreements_TenantId_Activo_EffectiveFrom",
                table: "InvestorAgreements",
                columns: new[] { "TenantId", "Activo", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_InvestorAgreements_TenantId_EffectiveFrom",
                table: "InvestorAgreements",
                columns: new[] { "TenantId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_InvestorAgreements_TenantId_InvestorId",
                table: "InvestorAgreements",
                columns: new[] { "TenantId", "InvestorId" });

            migrationBuilder.CreateIndex(
                name: "IX_InvestorDistributionPayments_StatementId",
                table: "InvestorDistributionPayments",
                column: "StatementId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorDistributionPayments_TenantId",
                table: "InvestorDistributionPayments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorDistributionPayments_TenantId_Fecha",
                table: "InvestorDistributionPayments",
                columns: new[] { "TenantId", "Fecha" });

            migrationBuilder.CreateIndex(
                name: "IX_InvestorDistributionPayments_TenantId_StatementId",
                table: "InvestorDistributionPayments",
                columns: new[] { "TenantId", "StatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_InvestorPolicyExpenseCategories_CategoriaId",
                table: "InvestorPolicyExpenseCategories",
                column: "CategoriaId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorPolicyExpenseCategories_PolicyId",
                table: "InvestorPolicyExpenseCategories",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorPolicyExpenseCategories_TenantId",
                table: "InvestorPolicyExpenseCategories",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_InvestorPolicyExpenseCategories_Policy_Categoria",
                table: "InvestorPolicyExpenseCategories",
                columns: new[] { "TenantId", "PolicyId", "CategoriaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_InvestorProfitPolicies_TenantId",
                table: "InvestorProfitPolicies",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatementAdjustments_StatementId",
                table: "InvestorStatementAdjustments",
                column: "StatementId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatementAdjustments_TenantId",
                table: "InvestorStatementAdjustments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatementAdjustments_TenantId_StatementId",
                table: "InvestorStatementAdjustments",
                columns: new[] { "TenantId", "StatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatementEmailLogs_StatementId",
                table: "InvestorStatementEmailLogs",
                column: "StatementId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatementEmailLogs_TenantId",
                table: "InvestorStatementEmailLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatementEmailLogs_TenantId_StatementId",
                table: "InvestorStatementEmailLogs",
                columns: new[] { "TenantId", "StatementId" });

            migrationBuilder.CreateIndex(
                name: "UX_InvestorStatementEmailLogs_RealSent",
                table: "InvestorStatementEmailLogs",
                columns: new[] { "TenantId", "StatementId", "RecipientEmail", "ResendSequence" },
                unique: true,
                filter: "[IsTest] = 0 AND [Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatements_AgreementId",
                table: "InvestorStatements",
                column: "AgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatements_InvestorId",
                table: "InvestorStatements",
                column: "InvestorId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatements_TenantId",
                table: "InvestorStatements",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatements_TenantId_Estado",
                table: "InvestorStatements",
                columns: new[] { "TenantId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_InvestorStatements_TenantId_Periodo",
                table: "InvestorStatements",
                columns: new[] { "TenantId", "PeriodoInicio", "PeriodoFin" });

            migrationBuilder.CreateIndex(
                name: "UX_InvestorStatements_Investor_Periodo",
                table: "InvestorStatements",
                columns: new[] { "TenantId", "InvestorId", "PeriodoInicio", "PeriodoFin" },
                unique: true,
                filter: "[Estado] <> 5");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleExceptions_FuncionarioId",
                table: "RecurringScheduleExceptions",
                column: "FuncionarioId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleExceptions_RuleId",
                table: "RecurringScheduleExceptions",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleExceptions_TenantId",
                table: "RecurringScheduleExceptions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleExceptions_TenantId_Rule_Fecha",
                table: "RecurringScheduleExceptions",
                columns: new[] { "TenantId", "RuleId", "Fecha" });

            migrationBuilder.CreateIndex(
                name: "UX_RecurringScheduleExceptions_Rule_Fecha_Funcionario",
                table: "RecurringScheduleExceptions",
                columns: new[] { "TenantId", "RuleId", "Fecha", "FuncionarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleRules_ReglaOrigenId",
                table: "RecurringScheduleRules",
                column: "ReglaOrigenId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleRules_TenantId",
                table: "RecurringScheduleRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleRules_TenantId_Activa",
                table: "RecurringScheduleRules",
                columns: new[] { "TenantId", "Activa" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleRules_TenantId_Vigencia",
                table: "RecurringScheduleRules",
                columns: new[] { "TenantId", "VigenteDesde", "VigenteHasta" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleRuleTargets_FuncionarioId",
                table: "RecurringScheduleRuleTargets",
                column: "FuncionarioId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleRuleTargets_RuleId",
                table: "RecurringScheduleRuleTargets",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleRuleTargets_TenantId",
                table: "RecurringScheduleRuleTargets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringScheduleRuleTargets_TenantId_FuncionarioId",
                table: "RecurringScheduleRuleTargets",
                columns: new[] { "TenantId", "FuncionarioId" });

            migrationBuilder.CreateIndex(
                name: "UX_RecurringScheduleRuleTargets_Rule_Funcionario",
                table: "RecurringScheduleRuleTargets",
                columns: new[] { "TenantId", "RuleId", "FuncionarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvestors_TenantId",
                table: "TenantInvestors",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvestors_TenantId_Activo",
                table: "TenantInvestors",
                columns: new[] { "TenantId", "Activo" });

            migrationBuilder.CreateIndex(
                name: "UX_TenantInvestors_TenantId_Email",
                table: "TenantInvestors",
                columns: new[] { "TenantId", "Email" },
                unique: true);

            AttachRowLevelSecurity(migrationBuilder);
        }

        /// <summary>
        /// Engancha las tablas nuevas a la Row-Level Security existente (misma función
        /// <c>fnTenantAccess</c> y misma política que usan las demás tablas del tenant).
        /// Sin esto, las tablas nuevas quedarían fuera del RLS aunque el filtro global de EF sí
        /// aplique: la defensa en profundidad de base de datos se perdería.
        ///
        /// <para>
        /// El script es idempotente y no falla si la base todavía no tiene RLS configurado
        /// (entornos de desarrollo). Solo corre en SQL Server; los tests usan SQLite con
        /// <c>EnsureCreated()</c>, que no ejecuta migraciones.
        /// </para>
        /// </summary>
        private static void AttachRowLevelSecurity(MigrationBuilder migrationBuilder)
        {
            // Un SOLO lote con las variables declaradas una vez y un recorrido sobre la lista de
            // tablas. Repetir el bloque por tabla rompería el despliegue: en T-SQL las variables son
            // de lote, no de bloque, y el segundo DECLARE del mismo nombre es un error.
            var lista = string.Join(
                "," + Environment.NewLine + "                         ",
                TenantTables.Select(table => $"(N'{table}')"));

            migrationBuilder.Sql(
                $"""
                 IF OBJECT_ID(N'[dbo].[fnTenantAccess]') IS NOT NULL
                 BEGIN
                     DECLARE @tablas TABLE (Nombre sysname NOT NULL);
                     INSERT INTO @tablas (Nombre) VALUES
                         {lista};

                     DECLARE @policySchema sysname;
                     DECLARE @policyName sysname;
                     DECLARE @qualifiedPolicy nvarchar(300);
                     DECLARE @sql nvarchar(max);
                     DECLARE @wasEnabled bit;
                     DECLARE @tabla sysname;

                     SELECT TOP (1)
                         @policySchema = SCHEMA_NAME(policy.schema_id),
                         @policyName = policy.name,
                         @wasEnabled = policy.is_enabled
                     FROM sys.security_policies AS policy
                     INNER JOIN sys.security_predicates AS predicate
                         ON predicate.object_id = policy.object_id
                     WHERE predicate.predicate_definition LIKE N'%fnTenantAccess%'
                     ORDER BY policy.name;

                     IF @policyName IS NOT NULL
                     BEGIN
                         SET @qualifiedPolicy = QUOTENAME(@policySchema) + N'.' + QUOTENAME(@policyName);

                         -- La política se apaga una sola vez para todo el lote y se restaura al final.
                         IF @wasEnabled = 1
                         BEGIN
                             SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy + N' WITH (STATE = OFF);';
                             EXEC sp_executesql @sql;
                         END

                         DECLARE tablas_cursor CURSOR LOCAL FAST_FORWARD FOR
                             SELECT Nombre FROM @tablas;

                         OPEN tablas_cursor;
                         FETCH NEXT FROM tablas_cursor INTO @tabla;

                         WHILE @@FETCH_STATUS = 0
                         BEGIN
                             -- Idempotente: si la tabla ya tiene predicados, no se vuelve a enganchar.
                             IF OBJECT_ID(N'[dbo].' + QUOTENAME(@tabla), N'U') IS NOT NULL
                                AND NOT EXISTS (
                                    SELECT 1
                                    FROM sys.security_predicates
                                    WHERE target_object_id = OBJECT_ID(N'[dbo].' + QUOTENAME(@tabla), N'U')
                                )
                             BEGIN
                                 SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy
                                     + N' ADD FILTER PREDICATE [dbo].[fnTenantAccess]([TenantId]) ON [dbo].'
                                     + QUOTENAME(@tabla) + N';';
                                 EXEC sp_executesql @sql;

                                 SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy
                                     + N' ADD BLOCK PREDICATE [dbo].[fnTenantAccess]([TenantId]) ON [dbo].'
                                     + QUOTENAME(@tabla) + N' AFTER INSERT;';
                                 EXEC sp_executesql @sql;

                                 SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy
                                     + N' ADD BLOCK PREDICATE [dbo].[fnTenantAccess]([TenantId]) ON [dbo].'
                                     + QUOTENAME(@tabla) + N' AFTER UPDATE;';
                                 EXEC sp_executesql @sql;
                             END

                             FETCH NEXT FROM tablas_cursor INTO @tabla;
                         END

                         CLOSE tablas_cursor;
                         DEALLOCATE tablas_cursor;

                         IF @wasEnabled = 1
                         BEGIN
                             SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy + N' WITH (STATE = ON);';
                             EXEC sp_executesql @sql;
                         END
                     END
                 END
                 """);
        }

        /// <summary>Suelta los predicados de RLS antes de borrar las tablas (orden obligatorio).</summary>
        private static void DetachRowLevelSecurity(MigrationBuilder migrationBuilder)
        {
            var lista = string.Join(
                "," + Environment.NewLine + "                     ",
                TenantTables.Select(table => $"(N'{table}')"));

            migrationBuilder.Sql(
                $"""
                 DECLARE @tablasDrop TABLE (Nombre sysname NOT NULL);
                 INSERT INTO @tablasDrop (Nombre) VALUES
                     {lista};

                 DECLARE @dropSql nvarchar(max) = N'';

                 SELECT @dropSql = @dropSql + N'ALTER SECURITY POLICY '
                     + QUOTENAME(SCHEMA_NAME(policy.schema_id)) + N'.' + QUOTENAME(policy.name)
                     + CASE
                         WHEN predicate.type_desc = N'FILTER' THEN N' DROP FILTER PREDICATE ON [dbo].'
                         ELSE N' DROP BLOCK PREDICATE ON [dbo].'
                       END
                     + QUOTENAME(OBJECT_NAME(predicate.target_object_id)) + N';'
                 FROM sys.security_predicates AS predicate
                 INNER JOIN sys.security_policies AS policy
                     ON policy.object_id = predicate.object_id
                 WHERE OBJECT_NAME(predicate.target_object_id) IN (SELECT Nombre FROM @tablasDrop);

                 IF LEN(@dropSql) > 0
                 BEGIN
                     EXEC sp_executesql @dropSql;
                 END
                 """);
        }

        /// <summary>Tablas nuevas que llevan TenantId y por tanto entran al RLS.</summary>
        private static readonly string[] TenantTables =
        [
            "TenantInvestors",
            "InvestorAgreements",
            "InvestorProfitPolicies",
            "InvestorPolicyExpenseCategories",
            "InvestorStatements",
            "InvestorStatementAdjustments",
            "InvestorDistributionPayments",
            "InvestorStatementEmailLogs",
            "RecurringScheduleRules",
            "RecurringScheduleRuleTargets",
            "RecurringScheduleExceptions"
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DetachRowLevelSecurity(migrationBuilder);

            migrationBuilder.DropTable(
                name: "InvestorDistributionPayments");

            migrationBuilder.DropTable(
                name: "InvestorPolicyExpenseCategories");

            migrationBuilder.DropTable(
                name: "InvestorStatementAdjustments");

            migrationBuilder.DropTable(
                name: "InvestorStatementEmailLogs");

            migrationBuilder.DropTable(
                name: "RecurringScheduleExceptions");

            migrationBuilder.DropTable(
                name: "RecurringScheduleRuleTargets");

            migrationBuilder.DropTable(
                name: "InvestorProfitPolicies");

            migrationBuilder.DropTable(
                name: "InvestorStatements");

            migrationBuilder.DropTable(
                name: "RecurringScheduleRules");

            migrationBuilder.DropTable(
                name: "InvestorAgreements");

            migrationBuilder.DropTable(
                name: "TenantInvestors");
        }
    }
}
