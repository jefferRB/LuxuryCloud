using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantPublicPageDailyMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantPublicPageDailyMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    MetricType = table.Column<int>(type: "int", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ServicioId = table.Column<int>(type: "int", nullable: true),
                    Count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantPublicPageDailyMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantPublicPageDailyMetrics_Servicios_ServicioId",
                        column: x => x.ServicioId,
                        principalTable: "Servicios",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantPublicPageDailyMetrics_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPublicPageDailyMetrics_ServicioId",
                table: "TenantPublicPageDailyMetrics",
                column: "ServicioId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPublicPageDailyMetrics_TenantId",
                table: "TenantPublicPageDailyMetrics",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPublicPageDailyMetrics_TenantId_Date",
                table: "TenantPublicPageDailyMetrics",
                columns: new[] { "TenantId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPublicPageDailyMetrics_TenantId_Date_MetricType",
                table: "TenantPublicPageDailyMetrics",
                columns: new[] { "TenantId", "Date", "MetricType" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPublicPageDailyMetrics_TenantId_Date_MetricType_ServicioId",
                table: "TenantPublicPageDailyMetrics",
                columns: new[] { "TenantId", "Date", "MetricType", "ServicioId" });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantPublicPageDailyMetrics");
        }
    }
}
