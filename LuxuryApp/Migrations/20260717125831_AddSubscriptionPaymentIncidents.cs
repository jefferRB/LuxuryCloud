using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionPaymentIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastPaymentFailedAtUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPaymentRecoveryNotificationAtUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentRecoveryStatus",
                table: "Suscripciones",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SubscriptionPaymentIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuscripcionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TilopayRecurringPlanId = table.Column<int>(type: "int", nullable: true),
                    ProviderSubscriptionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClienteEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FailureDetectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GraceEndsAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderEventKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ProviderResultCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ProviderResultMessage = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FailureCount = table.Column<int>(type: "int", nullable: false),
                    NotificationCount = table.Column<int>(type: "int", nullable: false),
                    LastNotificationAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastReminderAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPaymentIncidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionPaymentIncidents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPaymentIncidents_TenantId",
                table: "SubscriptionPaymentIncidents",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriptionPaymentIncidents");

            migrationBuilder.DropColumn(
                name: "LastPaymentFailedAtUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "LastPaymentRecoveryNotificationAtUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "PaymentRecoveryStatus",
                table: "Suscripciones");
        }
    }
}
