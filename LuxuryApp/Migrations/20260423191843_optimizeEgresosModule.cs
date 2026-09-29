using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class optimizeEgresosModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Categorias_TenantId_Nombre",
                table: "Categorias");

            migrationBuilder.CreateIndex(
                name: "IX_Egresos_TenantId_CategoriaId_FechaEgreso",
                table: "Egresos",
                columns: new[] { "TenantId", "CategoriaId", "FechaEgreso" });

            migrationBuilder.CreateIndex(
                name: "IX_Egresos_TenantId_FechaEgreso",
                table: "Egresos",
                columns: new[] { "TenantId", "FechaEgreso" });

            migrationBuilder.CreateIndex(
                name: "IX_Categorias_TenantId_Nombre",
                table: "Categorias",
                columns: new[] { "TenantId", "Nombre" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Egresos_TenantId_CategoriaId_FechaEgreso",
                table: "Egresos");

            migrationBuilder.DropIndex(
                name: "IX_Egresos_TenantId_FechaEgreso",
                table: "Egresos");

            migrationBuilder.DropIndex(
                name: "IX_Categorias_TenantId_Nombre",
                table: "Categorias");

            migrationBuilder.CreateIndex(
                name: "IX_Categorias_TenantId_Nombre",
                table: "Categorias",
                columns: new[] { "TenantId", "Nombre" });
        }
    }
}
