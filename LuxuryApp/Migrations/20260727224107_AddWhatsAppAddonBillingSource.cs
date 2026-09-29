using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppAddonBillingSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BillingSource",
                table: "TenantSubscriptionAddons",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "GrantedAtUtc",
                table: "TenantSubscriptionAddons",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GrantedByUserId",
                table: "TenantSubscriptionAddons",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsManualGrantIndefinite",
                table: "TenantSubscriptionAddons",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ManualGrantExpiresAtUtc",
                table: "TenantSubscriptionAddons",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualGrantReason",
                table: "TenantSubscriptionAddons",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ManualGrantType",
                table: "TenantSubscriptionAddons",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevocationReason",
                table: "TenantSubscriptionAddons",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAtUtc",
                table: "TenantSubscriptionAddons",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedByUserId",
                table: "TenantSubscriptionAddons",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BillingSource",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "GrantedAtUtc",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "GrantedByUserId",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "IsManualGrantIndefinite",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "ManualGrantExpiresAtUtc",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "ManualGrantReason",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "ManualGrantType",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "RevocationReason",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "RevokedAtUtc",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "RevokedByUserId",
                table: "TenantSubscriptionAddons");
        }
    }
}
