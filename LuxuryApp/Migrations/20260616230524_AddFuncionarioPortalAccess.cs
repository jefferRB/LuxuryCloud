using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddFuncionarioPortalAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppUsuarioId",
                table: "Funcionarios",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FuncionarioId",
                table: "AspNetUsers",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Funcionarios_AppUsuarioId",
                table: "Funcionarios",
                column: "AppUsuarioId",
                unique: true,
                filter: "[AppUsuarioId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Funcionarios_AspNetUsers_AppUsuarioId",
                table: "Funcionarios",
                column: "AppUsuarioId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Funcionarios_AspNetUsers_AppUsuarioId",
                table: "Funcionarios");

            migrationBuilder.DropIndex(
                name: "UX_Funcionarios_AppUsuarioId",
                table: "Funcionarios");

            migrationBuilder.DropColumn(
                name: "AppUsuarioId",
                table: "Funcionarios");

            migrationBuilder.DropColumn(
                name: "FuncionarioId",
                table: "AspNetUsers");
        }
    }
}
