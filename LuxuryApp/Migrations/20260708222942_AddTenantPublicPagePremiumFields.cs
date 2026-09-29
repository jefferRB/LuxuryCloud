using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantPublicPagePremiumFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessHours",
                table: "TenantPublicPages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HeroEyebrow",
                table: "TenantPublicPages",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WazeUrl",
                table: "TenantPublicPages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BusinessHours",
                table: "TenantPublicPages");

            migrationBuilder.DropColumn(
                name: "HeroEyebrow",
                table: "TenantPublicPages");

            migrationBuilder.DropColumn(
                name: "WazeUrl",
                table: "TenantPublicPages");
        }
    }
}
