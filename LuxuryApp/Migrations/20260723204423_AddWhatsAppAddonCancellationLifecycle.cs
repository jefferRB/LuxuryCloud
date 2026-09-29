using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppAddonCancellationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelAtPeriodEnd",
                table: "TenantSubscriptionAddons",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancellationEffectiveAtUtc",
                table: "TenantSubscriptionAddons",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "TenantSubscriptionAddons",
                type: "nvarchar(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationRequestedByUserId",
                table: "TenantSubscriptionAddons",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingCancellationProviderSubscriptionId",
                table: "TenantSubscriptionAddons",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingCancellationTilopayRecurringPlanId",
                table: "TenantSubscriptionAddons",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProviderCancellation",
                table: "TenantSubscriptionAddons",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProviderCancellationAttemptCount",
                table: "TenantSubscriptionAddons",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderCancellationLastAttemptUtc",
                table: "TenantSubscriptionAddons",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderCancellationNextRetryUtc",
                table: "TenantSubscriptionAddons",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderCancelledAtUtc",
                table: "TenantSubscriptionAddons",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelAtPeriodEnd",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "CancellationEffectiveAtUtc",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedByUserId",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "PendingCancellationProviderSubscriptionId",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "PendingCancellationTilopayRecurringPlanId",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "ProviderCancellation",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "ProviderCancellationAttemptCount",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "ProviderCancellationLastAttemptUtc",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "ProviderCancellationNextRetryUtc",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "ProviderCancelledAtUtc",
                table: "TenantSubscriptionAddons");
        }
    }
}
