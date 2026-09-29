using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthlyBusinessReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantMonthlyReportEmailLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportYear = table.Column<int>(type: "int", nullable: false),
                    ReportMonth = table.Column<int>(type: "int", nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsTest = table.Column<bool>(type: "bit", nullable: false),
                    TriggeredByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantMonthlyReportEmailLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantMonthlyReportSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SendToOwnerEmail = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    AdditionalRecipients = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IncludeFinancialData = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IncludeOperationalData = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IncludeRecommendations = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SendDayOfMonth = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    SendHour = table.Column<int>(type: "int", nullable: false, defaultValue: 8),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantMonthlyReportSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantMonthlyReportEmailLogs_CreatedAt",
                table: "TenantMonthlyReportEmailLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TenantMonthlyReportEmailLogs_Tenant_Anio_Mes",
                table: "TenantMonthlyReportEmailLogs",
                columns: new[] { "TenantId", "ReportYear", "ReportMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantMonthlyReportEmailLogs_Tenant_Periodo_Correo_Test",
                table: "TenantMonthlyReportEmailLogs",
                columns: new[] { "TenantId", "ReportYear", "ReportMonth", "RecipientEmail", "IsTest" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantMonthlyReportEmailLogs_Tenant_Status",
                table: "TenantMonthlyReportEmailLogs",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantMonthlyReportEmailLogs_TenantId",
                table: "TenantMonthlyReportEmailLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_TenantMonthlyReportEmailLogs_RealSent",
                table: "TenantMonthlyReportEmailLogs",
                columns: new[] { "TenantId", "ReportYear", "ReportMonth", "RecipientEmail" },
                unique: true,
                filter: "[IsTest] = 0 AND [Status] = 'Sent'");

            migrationBuilder.CreateIndex(
                name: "IX_TenantMonthlyReportSettings_TenantId_IsEnabled",
                table: "TenantMonthlyReportSettings",
                columns: new[] { "TenantId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "UX_TenantMonthlyReportSettings_TenantId",
                table: "TenantMonthlyReportSettings",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantMonthlyReportEmailLogs");

            migrationBuilder.DropTable(
                name: "TenantMonthlyReportSettings");
        }
    }
}
