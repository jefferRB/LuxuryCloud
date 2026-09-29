using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddReservasOnline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServicioId = table.Column<int>(type: "int", nullable: false),
                    FuncionarioId = table.Column<int>(type: "int", nullable: true),
                    ClienteId = table.Column<int>(type: "int", nullable: true),
                    NombreCliente = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TelefonoCliente = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CorreoCliente = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    FechaHoraInicioSolicitada = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaHoraFinCalculada = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DuracionMinutos = table.Column<int>(type: "int", nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NotasCliente = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Origen = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AceptaWhatsApp = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConfirmedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RejectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RejectedReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ConvertedCitaId = table.Column<int>(type: "int", nullable: true),
                    IpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingRequests_Citas_ConvertedCitaId",
                        column: x => x.ConvertedCitaId,
                        principalTable: "Citas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BookingRequests_Clientes_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "Clientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BookingRequests_Funcionarios_FuncionarioId",
                        column: x => x.FuncionarioId,
                        principalTable: "Funcionarios",
                        principalColumn: "IdFuncionario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingRequests_Servicios_ServicioId",
                        column: x => x.ServicioId,
                        principalTable: "Servicios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantBookingSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicBookingEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PublicBookingSlug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    PublicBookingMode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PublicBookingAllowEmployeeSelection = table.Column<bool>(type: "bit", nullable: false),
                    PublicBookingAllowAnyEmployee = table.Column<bool>(type: "bit", nullable: false),
                    PublicBookingMinAdvanceMinutes = table.Column<int>(type: "int", nullable: false),
                    PublicBookingMaxDaysAhead = table.Column<int>(type: "int", nullable: false),
                    PublicBookingWelcomeMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PublicBookingConfirmationMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OpenTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    CloseTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    SlotIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    WorkingDaysMask = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBookingSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantBookingSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingRequests_ClienteId",
                table: "BookingRequests",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingRequests_ConvertedCitaId",
                table: "BookingRequests",
                column: "ConvertedCitaId",
                filter: "[ConvertedCitaId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BookingRequests_FuncionarioId",
                table: "BookingRequests",
                column: "FuncionarioId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingRequests_ServicioId",
                table: "BookingRequests",
                column: "ServicioId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingRequests_TenantId",
                table: "BookingRequests",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingRequests_TenantId_Estado_Fecha",
                table: "BookingRequests",
                columns: new[] { "TenantId", "Estado", "FechaHoraInicioSolicitada" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingRequests_TenantId_Telefono_Estado",
                table: "BookingRequests",
                columns: new[] { "TenantId", "TelefonoCliente", "Estado" });

            migrationBuilder.CreateIndex(
                name: "UX_TenantBookingSettings_Slug",
                table: "TenantBookingSettings",
                column: "PublicBookingSlug",
                unique: true,
                filter: "[PublicBookingSlug] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_TenantBookingSettings_TenantId",
                table: "TenantBookingSettings",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingRequests");

            migrationBuilder.DropTable(
                name: "TenantBookingSettings");
        }
    }
}
