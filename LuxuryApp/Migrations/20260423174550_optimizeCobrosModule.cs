using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class optimizeCobrosModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Nombre",
                table: "Servicios",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_Servicios_TenantId_Nombre",
                table: "Servicios",
                columns: new[] { "TenantId", "Nombre" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DetalleCobroProductos_TenantId_CobroId",
                table: "DetalleCobroProductos",
                columns: new[] { "TenantId", "CobroId" });

            migrationBuilder.CreateIndex(
                name: "IX_Cobros_TenantId_FechaCobro",
                table: "Cobros",
                columns: new[] { "TenantId", "FechaCobro" });

            migrationBuilder.CreateIndex(
                name: "IX_Cobros_TenantId_FuncionarioId_FechaCobro",
                table: "Cobros",
                columns: new[] { "TenantId", "FuncionarioId", "FechaCobro" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Servicios_TenantId_Nombre",
                table: "Servicios");

            migrationBuilder.DropIndex(
                name: "IX_DetalleCobroProductos_TenantId_CobroId",
                table: "DetalleCobroProductos");

            migrationBuilder.DropIndex(
                name: "IX_Cobros_TenantId_FechaCobro",
                table: "Cobros");

            migrationBuilder.DropIndex(
                name: "IX_Cobros_TenantId_FuncionarioId_FechaCobro",
                table: "Cobros");

            migrationBuilder.AlterColumn<string>(
                name: "Nombre",
                table: "Servicios",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");
        }
    }
}
