using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class CrmPhase1_ClienteServicioRealizado_CobroClienteId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClienteId",
                table: "Cobros",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClienteServiciosRealizados",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClienteId = table.Column<int>(type: "int", nullable: false),
                    FuncionarioId = table.Column<int>(type: "int", nullable: true),
                    ServicioId = table.Column<int>(type: "int", nullable: true),
                    CobroId = table.Column<int>(type: "int", nullable: true),
                    CitaId = table.Column<int>(type: "int", nullable: true),
                    FechaHora = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Notas = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Origen = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreadoEn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClienteServiciosRealizados", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClienteServiciosRealizados_Citas_CitaId",
                        column: x => x.CitaId,
                        principalTable: "Citas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClienteServiciosRealizados_Clientes_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "Clientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClienteServiciosRealizados_Cobros_CobroId",
                        column: x => x.CobroId,
                        principalTable: "Cobros",
                        principalColumn: "IdCobro",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClienteServiciosRealizados_Funcionarios_FuncionarioId",
                        column: x => x.FuncionarioId,
                        principalTable: "Funcionarios",
                        principalColumn: "IdFuncionario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClienteServiciosRealizados_Servicios_ServicioId",
                        column: x => x.ServicioId,
                        principalTable: "Servicios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cobros_ClienteId",
                table: "Cobros",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_Cobros_TenantId_ClienteId",
                table: "Cobros",
                columns: new[] { "TenantId", "ClienteId" },
                filter: "[ClienteId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteServiciosRealizados_CitaId",
                table: "ClienteServiciosRealizados",
                column: "CitaId");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteServiciosRealizados_ClienteId",
                table: "ClienteServiciosRealizados",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteServiciosRealizados_FuncionarioId",
                table: "ClienteServiciosRealizados",
                column: "FuncionarioId");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteServiciosRealizados_ServicioId",
                table: "ClienteServiciosRealizados",
                column: "ServicioId");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteServiciosRealizados_TenantId",
                table: "ClienteServiciosRealizados",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteServiciosRealizados_TenantId_ClienteId_FechaHora",
                table: "ClienteServiciosRealizados",
                columns: new[] { "TenantId", "ClienteId", "FechaHora" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "UX_ClienteServiciosRealizados_CobroId",
                table: "ClienteServiciosRealizados",
                column: "CobroId",
                unique: true,
                filter: "[CobroId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Cobros_Clientes_ClienteId",
                table: "Cobros",
                column: "ClienteId",
                principalTable: "Clientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cobros_Clientes_ClienteId",
                table: "Cobros");

            migrationBuilder.DropTable(
                name: "ClienteServiciosRealizados");

            migrationBuilder.DropIndex(
                name: "IX_Cobros_ClienteId",
                table: "Cobros");

            migrationBuilder.DropIndex(
                name: "IX_Cobros_TenantId_ClienteId",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "ClienteId",
                table: "Cobros");
        }
    }
}
