using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppConsentOptIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AceptaMensajesWhatsApp",
                table: "Clientes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppConsentCapturedByUserId",
                table: "Clientes",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppConsentSource",
                table: "Clientes",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppConsentTextVersion",
                table: "Clientes",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WhatsAppConsentUpdatedAtUtc",
                table: "Clientes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClienteId",
                table: "Citas",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppConsentAtCreation",
                table: "Citas",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "WhatsAppConsentCapturedAtUtc",
                table: "Citas",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppConsentSource",
                table: "Citas",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Citas_ClienteId",
                table: "Citas",
                column: "ClienteId");

            migrationBuilder.AddForeignKey(
                name: "FK_Citas_Clientes_ClienteId",
                table: "Citas",
                column: "ClienteId",
                principalTable: "Clientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Citas_Clientes_ClienteId",
                table: "Citas");

            migrationBuilder.DropIndex(
                name: "IX_Citas_ClienteId",
                table: "Citas");

            migrationBuilder.DropColumn(
                name: "AceptaMensajesWhatsApp",
                table: "Clientes");

            migrationBuilder.DropColumn(
                name: "WhatsAppConsentCapturedByUserId",
                table: "Clientes");

            migrationBuilder.DropColumn(
                name: "WhatsAppConsentSource",
                table: "Clientes");

            migrationBuilder.DropColumn(
                name: "WhatsAppConsentTextVersion",
                table: "Clientes");

            migrationBuilder.DropColumn(
                name: "WhatsAppConsentUpdatedAtUtc",
                table: "Clientes");

            migrationBuilder.DropColumn(
                name: "ClienteId",
                table: "Citas");

            migrationBuilder.DropColumn(
                name: "WhatsAppConsentAtCreation",
                table: "Citas");

            migrationBuilder.DropColumn(
                name: "WhatsAppConsentCapturedAtUtc",
                table: "Citas");

            migrationBuilder.DropColumn(
                name: "WhatsAppConsentSource",
                table: "Citas");
        }
    }
}
