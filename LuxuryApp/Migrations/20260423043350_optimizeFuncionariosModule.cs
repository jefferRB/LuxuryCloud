using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class optimizeFuncionariosModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Puestos_TenantId_NombrePuesto",
                table: "Puestos",
                columns: new[] { "TenantId", "NombrePuesto" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PagosFuncionarios_TenantId_Semana_Funcionario",
                table: "PagosFuncionarios",
                columns: new[] { "TenantId", "InicioSemana", "FinSemana", "FuncionarioId" });

            migrationBuilder.CreateIndex(
                name: "IX_Funcionarios_TenantId_Nombre",
                table: "Funcionarios",
                columns: new[] { "TenantId", "Nombre" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Puestos_TenantId_NombrePuesto",
                table: "Puestos");

            migrationBuilder.DropIndex(
                name: "IX_PagosFuncionarios_TenantId_Semana_Funcionario",
                table: "PagosFuncionarios");

            migrationBuilder.DropIndex(
                name: "IX_Funcionarios_TenantId_Nombre",
                table: "Funcionarios");
        }
    }
}
