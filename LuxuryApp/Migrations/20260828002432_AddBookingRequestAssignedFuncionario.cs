using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingRequestAssignedFuncionario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FuncionarioAsignadoId",
                table: "BookingRequests",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingRequests_FuncionarioAsignadoId",
                table: "BookingRequests",
                column: "FuncionarioAsignadoId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingRequests_TenantId_Estado_Asignado_Fecha",
                table: "BookingRequests",
                columns: new[] { "TenantId", "Estado", "FuncionarioAsignadoId", "FechaHoraInicioSolicitada" });

            migrationBuilder.AddForeignKey(
                name: "FK_BookingRequests_Funcionarios_FuncionarioAsignadoId",
                table: "BookingRequests",
                column: "FuncionarioAsignadoId",
                principalTable: "Funcionarios",
                principalColumn: "IdFuncionario",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BookingRequests_Funcionarios_FuncionarioAsignadoId",
                table: "BookingRequests");

            migrationBuilder.DropIndex(
                name: "IX_BookingRequests_FuncionarioAsignadoId",
                table: "BookingRequests");

            migrationBuilder.DropIndex(
                name: "IX_BookingRequests_TenantId_Estado_Asignado_Fecha",
                table: "BookingRequests");

            migrationBuilder.DropColumn(
                name: "FuncionarioAsignadoId",
                table: "BookingRequests");
        }
    }
}
