using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class weeklyEmployeeLiquidations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE [Categorias]
                SET [Nombre] = LEFT(LTRIM(RTRIM(ISNULL([Nombre], N''))), 150)
                WHERE [Nombre] IS NULL OR LEN([Nombre]) > 150 OR [Nombre] <> LTRIM(RTRIM([Nombre]));

                UPDATE [Categorias]
                SET [Detalle] = LEFT(COALESCE(NULLIF(LTRIM(RTRIM([Detalle])), N''), N'Sin detalle'), 500)
                WHERE [Detalle] IS NULL OR LEN([Detalle]) > 500 OR [Detalle] <> LTRIM(RTRIM([Detalle]));
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Nombre",
                table: "Categorias",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Detalle",
                table: "Categorias",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateTable(
                name: "LiquidacionesSemanales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SemanaInicio = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SemanaFin = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaPago = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MontoTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Observacion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreadoPor = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    FechaCreacion = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EgresoId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiquidacionesSemanales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiquidacionesSemanales_Egresos_EgresoId",
                        column: x => x.EgresoId,
                        principalTable: "Egresos",
                        principalColumn: "IdEgreso",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LiquidacionesSemanalesDetalle",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LiquidacionSemanalId = table.Column<int>(type: "int", nullable: false),
                    FuncionarioId = table.Column<int>(type: "int", nullable: false),
                    MontoServicios = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MontoProductos = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Impuestos = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MontoNeto = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MontoPagado = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Pendiente = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiquidacionesSemanalesDetalle", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiquidacionesSemanalesDetalle_Funcionarios_FuncionarioId",
                        column: x => x.FuncionarioId,
                        principalTable: "Funcionarios",
                        principalColumn: "IdFuncionario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LiquidacionesSemanalesDetalle_LiquidacionesSemanales_LiquidacionSemanalId",
                        column: x => x.LiquidacionSemanalId,
                        principalTable: "LiquidacionesSemanales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiquidacionesSemanalesDistribucionMensual",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LiquidacionSemanalId = table.Column<int>(type: "int", nullable: false),
                    Anio = table.Column<int>(type: "int", nullable: false),
                    Mes = table.Column<int>(type: "int", nullable: false),
                    MontoAsignado = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DiasAplicados = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiquidacionesSemanalesDistribucionMensual", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiquidacionesSemanalesDistribucionMensual_LiquidacionesSemanales_LiquidacionSemanalId",
                        column: x => x.LiquidacionSemanalId,
                        principalTable: "LiquidacionesSemanales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categorias_TenantId_Nombre",
                table: "Categorias",
                columns: new[] { "TenantId", "Nombre" });

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanales_EgresoId",
                table: "LiquidacionesSemanales",
                column: "EgresoId",
                unique: true,
                filter: "[EgresoId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanales_TenantId",
                table: "LiquidacionesSemanales",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanales_TenantId_Semana",
                table: "LiquidacionesSemanales",
                columns: new[] { "TenantId", "SemanaInicio", "SemanaFin", "FechaPago" });

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanalesDetalle_FuncionarioId",
                table: "LiquidacionesSemanalesDetalle",
                column: "FuncionarioId");

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanalesDetalle_LiquidacionSemanalId",
                table: "LiquidacionesSemanalesDetalle",
                column: "LiquidacionSemanalId");

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanalesDetalle_TenantId",
                table: "LiquidacionesSemanalesDetalle",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanalesDetalle_TenantId_LiquidacionSemanalId_FuncionarioId",
                table: "LiquidacionesSemanalesDetalle",
                columns: new[] { "TenantId", "LiquidacionSemanalId", "FuncionarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanalesDistribucionMensual_LiquidacionSemanalId",
                table: "LiquidacionesSemanalesDistribucionMensual",
                column: "LiquidacionSemanalId");

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanalesDistribucionMensual_TenantId",
                table: "LiquidacionesSemanalesDistribucionMensual",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanalesDistribucionMensual_TenantId_Anio_Mes",
                table: "LiquidacionesSemanalesDistribucionMensual",
                columns: new[] { "TenantId", "Anio", "Mes" });

            migrationBuilder.CreateIndex(
                name: "IX_LiquidacionesSemanalesDistribucionMensual_TenantId_LiquidacionSemanalId_Anio_Mes",
                table: "LiquidacionesSemanalesDistribucionMensual",
                columns: new[] { "TenantId", "LiquidacionSemanalId", "Anio", "Mes" },
                unique: true);

            migrationBuilder.Sql(
                """
                ;WITH CategoriasDuplicadas AS
                (
                    SELECT
                        [Id],
                        ROW_NUMBER() OVER (PARTITION BY [TenantId], [Nombre] ORDER BY [Id]) AS [RowNumber]
                    FROM [Categorias]
                    WHERE [Nombre] = N'Pago Funcionarios'
                )
                UPDATE c
                SET [Nombre] = LEFT(CONCAT(N'Pago Funcionarios legado ', c.[Id]), 150)
                FROM [Categorias] c
                INNER JOIN CategoriasDuplicadas d
                    ON d.[Id] = c.[Id]
                WHERE d.[RowNumber] > 1;

                CREATE UNIQUE INDEX [UX_Categorias_TenantId_Nombre_PagoFuncionarios]
                ON [Categorias] ([TenantId], [Nombre])
                WHERE [Nombre] = N'Pago Funcionarios';
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE [LiquidacionesSemanales]
                ADD CONSTRAINT [CK_LiquidacionesSemanales_SemanaValida]
                CHECK ([SemanaFin] >= [SemanaInicio]);

                ALTER TABLE [LiquidacionesSemanales]
                ADD CONSTRAINT [CK_LiquidacionesSemanales_MontoTotal]
                CHECK ([MontoTotal] >= 0);

                ALTER TABLE [LiquidacionesSemanalesDetalle]
                ADD CONSTRAINT [CK_LiquidacionesSemanalesDetalle_Montos]
                CHECK (
                    [MontoServicios] >= 0 AND
                    [MontoProductos] >= 0 AND
                    [Impuestos] >= 0 AND
                    [MontoNeto] >= 0 AND
                    [MontoPagado] > 0
                );

                ALTER TABLE [LiquidacionesSemanalesDistribucionMensual]
                ADD CONSTRAINT [CK_LiquidacionesSemanalesDistribucionMensual_Valores]
                CHECK (
                    [MontoAsignado] >= 0 AND
                    [DiasAplicados] > 0 AND
                    [Mes] BETWEEN 1 AND 12
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Categorias_TenantId_Nombre_PagoFuncionarios' AND object_id = OBJECT_ID(N'[dbo].[Categorias]'))
                BEGIN
                    DROP INDEX [UX_Categorias_TenantId_Nombre_PagoFuncionarios] ON [Categorias];
                END

                IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_LiquidacionesSemanalesDetalle_Montos')
                BEGIN
                    ALTER TABLE [LiquidacionesSemanalesDetalle] DROP CONSTRAINT [CK_LiquidacionesSemanalesDetalle_Montos];
                END

                IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_LiquidacionesSemanalesDistribucionMensual_Valores')
                BEGIN
                    ALTER TABLE [LiquidacionesSemanalesDistribucionMensual] DROP CONSTRAINT [CK_LiquidacionesSemanalesDistribucionMensual_Valores];
                END

                IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_LiquidacionesSemanales_SemanaValida')
                BEGIN
                    ALTER TABLE [LiquidacionesSemanales] DROP CONSTRAINT [CK_LiquidacionesSemanales_SemanaValida];
                END

                IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_LiquidacionesSemanales_MontoTotal')
                BEGIN
                    ALTER TABLE [LiquidacionesSemanales] DROP CONSTRAINT [CK_LiquidacionesSemanales_MontoTotal];
                END
                """);

            migrationBuilder.DropTable(
                name: "LiquidacionesSemanalesDetalle");

            migrationBuilder.DropTable(
                name: "LiquidacionesSemanalesDistribucionMensual");

            migrationBuilder.DropTable(
                name: "LiquidacionesSemanales");

            migrationBuilder.DropIndex(
                name: "IX_Categorias_TenantId_Nombre",
                table: "Categorias");

            migrationBuilder.AlterColumn<string>(
                name: "Nombre",
                table: "Categorias",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(150)",
                oldMaxLength: 150);

            migrationBuilder.AlterColumn<string>(
                name: "Detalle",
                table: "Categorias",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);
        }
    }
}
