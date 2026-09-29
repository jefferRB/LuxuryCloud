using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddFuncionarioRebajarImpuestosComision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RebajarImpuestosAntesDeComision",
                table: "Funcionarios",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RebajarImpuestosAntesDeComision",
                table: "Funcionarios");
        }
    }
}
