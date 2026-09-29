using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddFuncionarioPortalPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CitaId",
                table: "Cobros",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FuncionarioPortalPermisos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FuncionarioId = table.Column<int>(type: "int", nullable: false),
                    Permiso = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Permitido = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FuncionarioPortalPermisos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FuncionarioPortalPermisos_Funcionarios_FuncionarioId",
                        column: x => x.FuncionarioId,
                        principalTable: "Funcionarios",
                        principalColumn: "IdFuncionario",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cobros_CitaId",
                table: "Cobros",
                column: "CitaId");

            migrationBuilder.CreateIndex(
                name: "UX_Cobros_TenantId_CitaId",
                table: "Cobros",
                columns: new[] { "TenantId", "CitaId" },
                unique: true,
                filter: "[CitaId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FuncionarioPortalPermisos_FuncionarioId",
                table: "FuncionarioPortalPermisos",
                column: "FuncionarioId");

            migrationBuilder.CreateIndex(
                name: "IX_FuncionarioPortalPermisos_TenantId",
                table: "FuncionarioPortalPermisos",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_FuncionarioPortalPermisos_Tenant_Funcionario_Permiso",
                table: "FuncionarioPortalPermisos",
                columns: new[] { "TenantId", "FuncionarioId", "Permiso" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Cobros_Citas_CitaId",
                table: "Cobros",
                column: "CitaId",
                principalTable: "Citas",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cobros_Citas_CitaId",
                table: "Cobros");

            migrationBuilder.DropTable(
                name: "FuncionarioPortalPermisos");

            migrationBuilder.DropIndex(
                name: "IX_Cobros_CitaId",
                table: "Cobros");

            migrationBuilder.DropIndex(
                name: "UX_Cobros_TenantId_CitaId",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "CitaId",
                table: "Cobros");
        }
    }
}
