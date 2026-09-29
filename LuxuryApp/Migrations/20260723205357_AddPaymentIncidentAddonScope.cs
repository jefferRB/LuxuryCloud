using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentIncidentAddonScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AddonId",
                table: "SubscriptionPaymentIncidents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "SubscriptionPaymentIncidents",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddonId",
                table: "SubscriptionPaymentIncidents");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "SubscriptionPaymentIncidents");
        }
    }
}
