using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionProviderExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderExpiresAtUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderExpiryLastSyncedUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderExpiryRaw",
                table: "Suscripciones",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderExpiresAtUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "ProviderExpiryLastSyncedUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "ProviderExpiryRaw",
                table: "Suscripciones");
        }
    }
}
