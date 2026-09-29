using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingRequestSubmissionToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicSubmissionToken",
                table: "BookingRequests",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_BookingRequests_TenantId_SubmissionToken",
                table: "BookingRequests",
                columns: new[] { "TenantId", "PublicSubmissionToken" },
                unique: true,
                filter: "[PublicSubmissionToken] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_BookingRequests_TenantId_SubmissionToken",
                table: "BookingRequests");

            migrationBuilder.DropColumn(
                name: "PublicSubmissionToken",
                table: "BookingRequests");
        }
    }
}
