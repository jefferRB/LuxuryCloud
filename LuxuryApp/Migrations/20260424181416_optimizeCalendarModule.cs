using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class optimizeCalendarModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Citas_TenantId_FuncionarioId_FechaHoraCita",
                table: "Citas",
                columns: new[] { "TenantId", "FuncionarioId", "FechaHoraCita" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Citas_TenantId_FuncionarioId_FechaHoraCita",
                table: "Citas");
        }
    }
}
