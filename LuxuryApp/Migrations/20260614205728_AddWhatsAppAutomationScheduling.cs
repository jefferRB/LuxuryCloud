using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppAutomationScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfirmationBatchTarget",
                table: "TenantWhatsAppSettings",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "TomorrowAllDay");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ConfirmationBatchTime",
                table: "TenantWhatsAppSettings",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConfirmationHoursBefore",
                table: "TenantWhatsAppSettings",
                type: "int",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ConfirmationMorningEnd",
                table: "TenantWhatsAppSettings",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ConfirmationMorningStart",
                table: "TenantWhatsAppSettings",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmationScheduleMode",
                table: "TenantWhatsAppSettings",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "RelativeBeforeAppointment");

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastConfirmationBatchRunDateLocal",
                table: "TenantWhatsAppSettings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastReminderBatchRunDateLocal",
                table: "TenantWhatsAppSettings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "QuietHoursEnabled",
                table: "TenantWhatsAppSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "QuietHoursEnd",
                table: "TenantWhatsAppSettings",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "QuietHoursStart",
                table: "TenantWhatsAppSettings",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReminderBatchTarget",
                table: "TenantWhatsAppSettings",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "SameDayRemaining");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ReminderBatchTime",
                table: "TenantWhatsAppSettings",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderHoursBefore",
                table: "TenantWhatsAppSettings",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "ReminderLookAheadHours",
                table: "TenantWhatsAppSettings",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<string>(
                name: "ReminderScheduleMode",
                table: "TenantWhatsAppSettings",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "RelativeBeforeAppointment");

            migrationBuilder.AddColumn<bool>(
                name: "SendConfirmationImmediatelyIfInsideWindow",
                table: "TenantWhatsAppSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SendReminderImmediatelyIfInsideWindow",
                table: "TenantWhatsAppSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfirmationBatchTarget",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "ConfirmationBatchTime",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "ConfirmationHoursBefore",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "ConfirmationMorningEnd",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "ConfirmationMorningStart",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "ConfirmationScheduleMode",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "LastConfirmationBatchRunDateLocal",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "LastReminderBatchRunDateLocal",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "QuietHoursEnabled",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "QuietHoursEnd",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "QuietHoursStart",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "ReminderBatchTarget",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "ReminderBatchTime",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "ReminderHoursBefore",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "ReminderLookAheadHours",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "ReminderScheduleMode",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "SendConfirmationImmediatelyIfInsideWindow",
                table: "TenantWhatsAppSettings");

            migrationBuilder.DropColumn(
                name: "SendReminderImmediatelyIfInsideWindow",
                table: "TenantWhatsAppSettings");
        }
    }
}
