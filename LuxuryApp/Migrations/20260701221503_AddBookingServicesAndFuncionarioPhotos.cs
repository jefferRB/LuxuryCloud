using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingServicesAndFuncionarioPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PublicBookingShowEmployeePhotos",
                table: "TenantBookingSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FotoActualizadaUtc",
                table: "Funcionarios",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FotoStoragePath",
                table: "Funcionarios",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FotoUrl",
                table: "Funcionarios",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MostrarFotoEnReservas",
                table: "Funcionarios",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "TenantBookingFuncionarioServices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FuncionarioId = table.Column<int>(type: "int", nullable: false),
                    ServicioId = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBookingFuncionarioServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantBookingFuncionarioServices_Funcionarios_FuncionarioId",
                        column: x => x.FuncionarioId,
                        principalTable: "Funcionarios",
                        principalColumn: "IdFuncionario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantBookingFuncionarioServices_Servicios_ServicioId",
                        column: x => x.ServicioId,
                        principalTable: "Servicios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantBookingServiceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServicioId = table.Column<int>(type: "int", nullable: false),
                    IsVisibleOnline = table.Column<bool>(type: "bit", nullable: false),
                    PublicName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    PublicDescription = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    ShowPrice = table.Column<bool>(type: "bit", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBookingServiceSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantBookingServiceSettings_Servicios_ServicioId",
                        column: x => x.ServicioId,
                        principalTable: "Servicios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantBookingFuncionarioServices_FuncionarioId",
                table: "TenantBookingFuncionarioServices",
                column: "FuncionarioId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantBookingFuncionarioServices_ServicioId",
                table: "TenantBookingFuncionarioServices",
                column: "ServicioId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantBookingFuncionarioServices_Tenant_Servicio",
                table: "TenantBookingFuncionarioServices",
                columns: new[] { "TenantId", "ServicioId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantBookingFuncionarioServices_TenantId",
                table: "TenantBookingFuncionarioServices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_TenantBookingFuncionarioServices_Tenant_Func_Servicio",
                table: "TenantBookingFuncionarioServices",
                columns: new[] { "TenantId", "FuncionarioId", "ServicioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantBookingServiceSettings_ServicioId",
                table: "TenantBookingServiceSettings",
                column: "ServicioId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantBookingServiceSettings_TenantId",
                table: "TenantBookingServiceSettings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_TenantBookingServiceSettings_TenantId_ServicioId",
                table: "TenantBookingServiceSettings",
                columns: new[] { "TenantId", "ServicioId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantBookingFuncionarioServices");

            migrationBuilder.DropTable(
                name: "TenantBookingServiceSettings");

            migrationBuilder.DropColumn(
                name: "PublicBookingShowEmployeePhotos",
                table: "TenantBookingSettings");

            migrationBuilder.DropColumn(
                name: "FotoActualizadaUtc",
                table: "Funcionarios");

            migrationBuilder.DropColumn(
                name: "FotoStoragePath",
                table: "Funcionarios");

            migrationBuilder.DropColumn(
                name: "FotoUrl",
                table: "Funcionarios");

            migrationBuilder.DropColumn(
                name: "MostrarFotoEnReservas",
                table: "Funcionarios");
        }
    }
}
