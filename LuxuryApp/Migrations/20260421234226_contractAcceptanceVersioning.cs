using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class contractAcceptanceVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContractDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    VersionNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ContentHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContractAcceptanceRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ContractDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AcceptedContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AcceptanceSource = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    AcceptedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractAcceptanceRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractAcceptanceRecords_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractAcceptanceRecords_ContractDocuments_ContractDocumentId",
                        column: x => x.ContractDocumentId,
                        principalTable: "ContractDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "ContractDocuments",
                columns: new[] { "Id", "ContentHash", "ContentHtml", "CreatedAtUtc", "EffectiveFromUtc", "IsActive", "Title", "UpdatedAtUtc", "VersionNumber" },
                values: new object[] { new Guid("9d0d8c0b-4e22-44d1-b7d9-7ba6e95c52b1"), "2E1530410832503E401491FB3CC70DD700E6B9B24C2549BC348C4A45F7A683AD", "<section class=\"contract-section\">\n    <h2>1. Terminos y Condiciones</h2>\n    <p>Este documento corresponde a una version inicial editable del contrato de uso de LuxuryApp. Antes de salir a produccion debes reemplazar este texto por la version final aprobada por asesoria legal.</p>\n    <p>LuxuryApp presta un servicio SaaS para negocios de belleza, barberia, salon y operaciones relacionadas. El uso del servicio implica aceptar las reglas operativas, tecnicas y comerciales definidas en este contrato.</p>\n    <p>El cliente se compromete a utilizar la plataforma conforme a la ley aplicable, a no compartir accesos de manera indebida y a custodiar sus credenciales, usuarios y configuraciones internas.</p>\n    <p>LuxuryApp puede actualizar funciones, seguridad y procesos operativos para mejorar la disponibilidad, estabilidad y cumplimiento del servicio.</p>\n</section>\n<section class=\"contract-section\">\n    <h2>2. Politica de Privacidad</h2>\n    <p>LuxuryApp trata la informacion necesaria para operar la cuenta, autenticar usuarios, administrar tenants, procesar pagos y mantener el funcionamiento del servicio.</p>\n    <p>El cliente declara que cuenta con la base legal necesaria para cargar datos de sus propios clientes, funcionarios y operaciones en la plataforma.</p>\n    <p>Debes reemplazar esta seccion por la politica de privacidad definitiva, incluyendo finalidades, base juridica, plazos de conservacion, medidas de seguridad, transferencias y canales de ejercicio de derechos.</p>\n</section>\n<section class=\"contract-section\">\n    <h2>3. Politica de pagos, cancelaciones y reembolsos</h2>\n    <p>El acceso comercial a LuxuryApp depende del plan contratado, sus condiciones de cobro, renovacion, suspension y reactivacion.</p>\n    <p>Debes completar esta seccion con las condiciones finales de facturacion, fechas de corte, reglas de cancelacion, periodos de aviso, politica de mora y escenarios de reembolso permitidos o no permitidos.</p>\n    <p>Mientras esta version placeholder siga vigente, ninguna clausula aqui incluida debe considerarse texto legal final para produccion.</p>\n</section>\n<section class=\"contract-section\">\n    <h2>4. Consentimiento de tratamiento de datos</h2>\n    <p>Al aceptar este contrato el usuario declara que ha leido el alcance del tratamiento de datos relacionado con la operacion de la cuenta, la seguridad del servicio y el soporte tecnico.</p>\n    <p>Debes sustituir este apartado por el consentimiento final aprobado, incluyendo categorias de datos, finalidad, responsables, encargados, revocatoria y demas extremos regulatorios aplicables.</p>\n    <p>La aceptacion registrada por el sistema conserva fecha, direccion IP, agente de usuario, version del documento y hash del contenido aceptado para fines de trazabilidad y cumplimiento.</p>\n</section>", new DateTime(2026, 4, 21, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 21, 0, 0, 0, 0, DateTimeKind.Utc), true, "Contrato de Uso del Servicio LuxuryApp", new DateTime(2026, 4, 21, 0, 0, 0, 0, DateTimeKind.Utc), "1.0.0" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractAcceptanceRecords_ContractDocumentId",
                table: "ContractAcceptanceRecords",
                column: "ContractDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractAcceptanceRecords_UserId_ContractDocumentId_AcceptedAtUtc",
                table: "ContractAcceptanceRecords",
                columns: new[] { "UserId", "ContractDocumentId", "AcceptedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractDocuments_IsActive",
                table: "ContractDocuments",
                column: "IsActive",
                unique: true,
                filter: "IsActive = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ContractDocuments_VersionNumber",
                table: "ContractDocuments",
                column: "VersionNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContractAcceptanceRecords");

            migrationBuilder.DropTable(
                name: "ContractDocuments");
        }
    }
}
