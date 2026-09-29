using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCitaClienteIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Citas_TenantId_ClienteId_FechaHoraCita",
                table: "Citas",
                columns: new[] { "TenantId", "ClienteId", "FechaHoraCita" },
                descending: new[] { false, false, true },
                filter: "[ClienteId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Citas_TenantId_ClienteId_FechaHoraCita",
                table: "Citas");
        }
    }
}
