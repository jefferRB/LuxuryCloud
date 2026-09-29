using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLandingRefinementFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessHoursJson",
                table: "TenantPublicPages",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicBusinessName",
                table: "TenantPublicPages",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DescripcionPublica",
                table: "Funcionarios",
                type: "nvarchar(280)",
                maxLength: 280,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BusinessHoursJson",
                table: "TenantPublicPages");

            migrationBuilder.DropColumn(
                name: "PublicBusinessName",
                table: "TenantPublicPages");

            migrationBuilder.DropColumn(
                name: "DescripcionPublica",
                table: "Funcionarios");
        }
    }
}
