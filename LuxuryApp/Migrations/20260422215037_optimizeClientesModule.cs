using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    public partial class optimizeClientesModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Quitar TODOS los predicados asociados a ClienteImagenes

            migrationBuilder.Sql(@"
BEGIN TRY
    ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy]
    DROP FILTER PREDICATE ON [dbo].[ClienteImagenes];
END TRY
BEGIN CATCH
END CATCH
");

            migrationBuilder.Sql(@"
BEGIN TRY
    ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy]
    DROP BLOCK PREDICATE ON [dbo].[ClienteImagenes] AFTER INSERT;
END TRY
BEGIN CATCH
END CATCH
");

            migrationBuilder.Sql(@"
BEGIN TRY
    ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy]
    DROP BLOCK PREDICATE ON [dbo].[ClienteImagenes] AFTER UPDATE;
END TRY
BEGIN CATCH
END CATCH
");

            // Ahora sí, ya no hay dependencia
            migrationBuilder.DropTable(
                name: "ClienteImagenes");

            migrationBuilder.DropIndex(
                name: "IX_ClienteVisitas_TenantId_ClienteId_FechaVisita",
                table: "ClienteVisitas");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteVisitas_TenantId_ClienteId_FechaVisita",
                table: "ClienteVisitas",
                columns: new[] { "TenantId", "ClienteId", "FechaVisita" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_TenantId_Nombre",
                table: "Clientes",
                columns: new[] { "TenantId", "Nombre" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClienteVisitas_TenantId_ClienteId_FechaVisita",
                table: "ClienteVisitas");

            migrationBuilder.DropIndex(
                name: "IX_Clientes_TenantId_Nombre",
                table: "Clientes");

            migrationBuilder.CreateTable(
                name: "ClienteImagenes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClienteId = table.Column<int>(type: "int", nullable: false),
                    Descripcion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Fecha = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Imagen = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    NumeroTelefono = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClienteImagenes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClienteImagenes_Clientes_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "Clientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClienteVisitas_TenantId_ClienteId_FechaVisita",
                table: "ClienteVisitas",
                columns: new[] { "TenantId", "ClienteId", "FechaVisita" });

            migrationBuilder.CreateIndex(
                name: "IX_ClienteImagenes_ClienteId",
                table: "ClienteImagenes",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteImagenes_TenantId",
                table: "ClienteImagenes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteImagenes_TenantId_ClienteId",
                table: "ClienteImagenes",
                columns: new[] { "TenantId", "ClienteId" });

            // Restaurar exactamente los predicados originales

            migrationBuilder.Sql(@"
ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy]
ADD FILTER PREDICATE [dbo].[fnTenantAccess]([TenantId]) ON [dbo].[ClienteImagenes];
");

            migrationBuilder.Sql(@"
ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy]
ADD BLOCK PREDICATE [dbo].[fnTenantAccess]([TenantId]) ON [dbo].[ClienteImagenes] AFTER INSERT;
");

            migrationBuilder.Sql(@"
ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy]
ADD BLOCK PREDICATE [dbo].[fnTenantAccess]([TenantId]) ON [dbo].[ClienteImagenes] AFTER UPDATE;
");
        }
    }
}