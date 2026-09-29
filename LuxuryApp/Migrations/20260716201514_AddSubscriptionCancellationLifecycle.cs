using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionCancellationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CancellationEffectiveAtUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "Suscripciones",
                type: "nvarchar(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancellationRequestedAtUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationRequestedByUserId",
                table: "Suscripciones",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderCancelledAtUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderPausedAtUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderStatusLastSyncedUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderStatusRaw",
                table: "Suscripciones",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationEffectiveAtUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAtUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedByUserId",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "ProviderCancelledAtUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "ProviderPausedAtUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "ProviderStatusLastSyncedUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "ProviderStatusRaw",
                table: "Suscripciones");
        }
    }
}
