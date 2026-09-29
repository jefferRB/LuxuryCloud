using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthlyReportAutomationAndRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludeManualRecipients",
                table: "TenantMonthlyReportSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IncludeMonthOverMonth",
                table: "TenantMonthlyReportSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "LastAutomaticError",
                table: "TenantMonthlyReportSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastAutomaticPeriod",
                table: "TenantMonthlyReportSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAutomaticRunAt",
                table: "TenantMonthlyReportSettings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAutomaticSentAt",
                table: "TenantMonthlyReportSettings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireConfirmedEmail",
                table: "TenantMonthlyReportSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SendToAllAdmins",
                table: "TenantMonthlyReportSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncludeManualRecipients",
                table: "TenantMonthlyReportSettings");

            migrationBuilder.DropColumn(
                name: "IncludeMonthOverMonth",
                table: "TenantMonthlyReportSettings");

            migrationBuilder.DropColumn(
                name: "LastAutomaticError",
                table: "TenantMonthlyReportSettings");

            migrationBuilder.DropColumn(
                name: "LastAutomaticPeriod",
                table: "TenantMonthlyReportSettings");

            migrationBuilder.DropColumn(
                name: "LastAutomaticRunAt",
                table: "TenantMonthlyReportSettings");

            migrationBuilder.DropColumn(
                name: "LastAutomaticSentAt",
                table: "TenantMonthlyReportSettings");

            migrationBuilder.DropColumn(
                name: "RequireConfirmedEmail",
                table: "TenantMonthlyReportSettings");

            migrationBuilder.DropColumn(
                name: "SendToAllAdmins",
                table: "TenantMonthlyReportSettings");
        }
    }
}
