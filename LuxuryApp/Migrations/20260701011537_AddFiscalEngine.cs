using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PreciosIncluyenIva",
                table: "Tenants",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TarifaIvaPorDefecto",
                table: "Tenants",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 13m);

            migrationBuilder.AddColumn<bool>(
                name: "AplicaIva",
                table: "Servicios",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PrecioIncluyeIva",
                table: "Servicios",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TarifaIva",
                table: "Servicios",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AplicaIva",
                table: "Productos",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PrecioIncluyeIva",
                table: "Productos",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TarifaIva",
                table: "Productos",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ColaboradorFacturaIva",
                table: "Funcionarios",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ComisionCalculadaSobre",
                table: "Funcionarios",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RequiereFacturaAntesDePagar",
                table: "Funcionarios",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "TarifaIvaFacturaColaborador",
                table: "Funcionarios",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 13m);

            migrationBuilder.AddColumn<int>(
                name: "TipoRelacionColaborador",
                table: "Funcionarios",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Backfill: la columna ComisionCalculadaSobre nace en 0 (TotalCobrado). Los funcionarios
            // existentes que venían con RebajarImpuestosAntesDeComision = 1 deben conservar su
            // comportamiento histórico → comisión sobre la base sin IVA (BaseSinIva = 1). Los que
            // tenían el flag en 0 permanecen en TotalCobrado. Idempotente y no destructivo.
            migrationBuilder.Sql("""
IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Funcionarios')
      AND name = N'ComisionCalculadaSobre'
)
BEGIN
    EXEC sys.sp_executesql N'
        UPDATE dbo.Funcionarios
        SET ComisionCalculadaSobre = 1
        WHERE RebajarImpuestosAntesDeComision = 1;
    ';
END
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreciosIncluyenIva",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TarifaIvaPorDefecto",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AplicaIva",
                table: "Servicios");

            migrationBuilder.DropColumn(
                name: "PrecioIncluyeIva",
                table: "Servicios");

            migrationBuilder.DropColumn(
                name: "TarifaIva",
                table: "Servicios");

            migrationBuilder.DropColumn(
                name: "AplicaIva",
                table: "Productos");

            migrationBuilder.DropColumn(
                name: "PrecioIncluyeIva",
                table: "Productos");

            migrationBuilder.DropColumn(
                name: "TarifaIva",
                table: "Productos");

            migrationBuilder.DropColumn(
                name: "ColaboradorFacturaIva",
                table: "Funcionarios");

            migrationBuilder.DropColumn(
                name: "ComisionCalculadaSobre",
                table: "Funcionarios");

            migrationBuilder.DropColumn(
                name: "RequiereFacturaAntesDePagar",
                table: "Funcionarios");

            migrationBuilder.DropColumn(
                name: "TarifaIvaFacturaColaborador",
                table: "Funcionarios");

            migrationBuilder.DropColumn(
                name: "TipoRelacionColaborador",
                table: "Funcionarios");
        }
    }
}
