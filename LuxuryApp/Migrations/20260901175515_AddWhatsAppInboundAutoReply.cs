using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppInboundAutoReply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WhatsAppInboundAutoReplies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InboundMessageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SenderPhoneE164 = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    MessageType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReplyMetaMessageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppInboundAutoReplies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppInboundAutoReplies_ReceivedAtUtc",
                table: "WhatsAppInboundAutoReplies",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "UX_WhatsAppInboundAutoReplies_InboundMessageId",
                table: "WhatsAppInboundAutoReplies",
                column: "InboundMessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WhatsAppInboundAutoReplies");
        }
    }
}
