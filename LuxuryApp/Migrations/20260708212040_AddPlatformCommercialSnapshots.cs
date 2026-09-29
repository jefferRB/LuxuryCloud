using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformCommercialSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformCommercialSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodYear = table.Column<int>(type: "int", nullable: false),
                    PeriodMonth = table.Column<int>(type: "int", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TriggerType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TriggeredByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    MrrTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ArrTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ActiveSubscriptions = table.Column<int>(type: "int", nullable: false),
                    MonthlyCycleCount = table.Column<int>(type: "int", nullable: false),
                    AnnualCycleCount = table.Column<int>(type: "int", nullable: false),
                    TenantsTotal = table.Column<int>(type: "int", nullable: false),
                    TenantsSaludable = table.Column<int>(type: "int", nullable: false),
                    TenantsAtencion = table.Column<int>(type: "int", nullable: false),
                    TenantsRiesgo = table.Column<int>(type: "int", nullable: false),
                    TenantsSinAcceso = table.Column<int>(type: "int", nullable: false),
                    TrialsActivos = table.Column<int>(type: "int", nullable: false),
                    TrialsPorVencer7d = table.Column<int>(type: "int", nullable: false),
                    ChurnedTenants = table.Column<int>(type: "int", nullable: false),
                    ChurnedMrr = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NewTenants = table.Column<int>(type: "int", nullable: false),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformCommercialSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_PlatformCommercialSnapshots_Period",
                table: "PlatformCommercialSnapshots",
                columns: new[] { "PeriodYear", "PeriodMonth" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformCommercialSnapshots");
        }
    }
}
