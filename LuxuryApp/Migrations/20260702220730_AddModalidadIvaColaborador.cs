using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddModalidadIvaColaborador : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ModalidadIvaColaborador",
                table: "Funcionarios",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Backfill: los colaboradores que ya facturaban IVA (ColaboradorFacturaIva = 1) pasan a la
            // modalidad B = IvaIncluido (1), que es el comportamiento correcto solicitado: el % ya incluye
            // su IVA y se descompone SIN aumentar el total (antes se sumaba por encima, modalidad C). Los
            // pagos ya registrados son snapshots inmutables (LiquidacionSemanalDetalle) y NO se recalculan;
            // solo cambian los cálculos del periodo vigente/futuro. Idempotente y no destructivo.
            migrationBuilder.Sql("""
IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Funcionarios')
      AND name = N'ModalidadIvaColaborador'
)
BEGIN
    EXEC sys.sp_executesql N'
        UPDATE dbo.Funcionarios
        SET ModalidadIvaColaborador = 1
        WHERE ColaboradorFacturaIva = 1;
    ';
END
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModalidadIvaColaborador",
                table: "Funcionarios");
        }
    }
}
