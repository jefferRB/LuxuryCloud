using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanChangeOldCancellationRetryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OldCancellationAttemptCount",
                table: "PlanChangeIntents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "OldCancellationAttemptsResetAtUtc",
                table: "PlanChangeIntents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OldCancellationLastAttemptUtc",
                table: "PlanChangeIntents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OldCancellationNextRetryUtc",
                table: "PlanChangeIntents",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OldCancellationAttemptCount",
                table: "PlanChangeIntents");

            migrationBuilder.DropColumn(
                name: "OldCancellationAttemptsResetAtUtc",
                table: "PlanChangeIntents");

            migrationBuilder.DropColumn(
                name: "OldCancellationLastAttemptUtc",
                table: "PlanChangeIntents");

            migrationBuilder.DropColumn(
                name: "OldCancellationNextRetryUtc",
                table: "PlanChangeIntents");
        }
    }
}
