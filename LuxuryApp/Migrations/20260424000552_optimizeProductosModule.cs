using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class optimizeProductosModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovimientosInventario_Productos",
                table: "MovimientosInventario");

            migrationBuilder.AlterColumn<string>(
                name: "NombreProducto",
                table: "Productos",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_Productos_TenantId_Activo_NombreProducto",
                table: "Productos",
                columns: new[] { "TenantId", "Activo", "NombreProducto" });

            migrationBuilder.CreateIndex(
                name: "IX_Productos_TenantId_NombreProducto",
                table: "Productos",
                columns: new[] { "TenantId", "NombreProducto" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosInventario_TenantId_ProductoId_FechaMovimiento",
                table: "MovimientosInventario",
                columns: new[] { "TenantId", "ProductoId", "FechaMovimiento" });

            migrationBuilder.AddForeignKey(
                name: "FK_MovimientosInventario_Productos",
                table: "MovimientosInventario",
                column: "ProductoId",
                principalTable: "Productos",
                principalColumn: "IdProducto",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovimientosInventario_Productos",
                table: "MovimientosInventario");

            migrationBuilder.DropIndex(
                name: "IX_Productos_TenantId_Activo_NombreProducto",
                table: "Productos");

            migrationBuilder.DropIndex(
                name: "IX_Productos_TenantId_NombreProducto",
                table: "Productos");

            migrationBuilder.DropIndex(
                name: "IX_MovimientosInventario_TenantId_ProductoId_FechaMovimiento",
                table: "MovimientosInventario");

            migrationBuilder.AlterColumn<string>(
                name: "NombreProducto",
                table: "Productos",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(150)",
                oldMaxLength: 150);

            migrationBuilder.AddForeignKey(
                name: "FK_MovimientosInventario_Productos",
                table: "MovimientosInventario",
                column: "ProductoId",
                principalTable: "Productos",
                principalColumn: "IdProducto",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
