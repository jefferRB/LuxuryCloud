using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddComprobantesCobro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComprobanteCobroSecuencias",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UltimoNumero = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComprobanteCobroSecuencias", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "ComprobantesCobro",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CobroId = table.Column<int>(type: "int", nullable: false),
                    CitaId = table.Column<int>(type: "int", nullable: true),
                    ClienteId = table.Column<int>(type: "int", nullable: true),
                    FuncionarioId = table.Column<int>(type: "int", nullable: true),
                    NumeroInterno = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    TipoComprobante = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EstadoEnvio = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TokenPublico = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EmailDestino = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EmailDestinoNormalizado = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NombreClienteSnapshot = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    TelefonoClienteSnapshot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    NombreNegocioSnapshot = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CedulaNegocioSnapshot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TelefonoNegocioSnapshot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    EmailNegocioSnapshot = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DireccionNegocioSnapshot = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FechaEmision = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Moneda = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Descuento = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Impuesto = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MetodoPago = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Observacion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ResendEmailId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ErrorEnvio = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IntentosEnvio = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HaciendaClave = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    HaciendaConsecutivo = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    HaciendaXmlPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    HaciendaEstado = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    HaciendaRespuesta = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EsFiscal = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComprobantesCobro", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComprobantesCobro_Clientes_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "Clientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComprobantesCobro_Cobros_CobroId",
                        column: x => x.CobroId,
                        principalTable: "Cobros",
                        principalColumn: "IdCobro",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComprobantesCobro_Funcionarios_FuncionarioId",
                        column: x => x.FuncionarioId,
                        principalTable: "Funcionarios",
                        principalColumn: "IdFuncionario",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComprobanteCobroLineas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComprobanteCobroId = table.Column<int>(type: "int", nullable: false),
                    Descripcion = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    TipoLinea = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Cantidad = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PrecioUnitario = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Impuesto = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ServicioId = table.Column<int>(type: "int", nullable: true),
                    ProductoId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComprobanteCobroLineas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComprobanteCobroLineas_ComprobantesCobro_ComprobanteCobroId",
                        column: x => x.ComprobanteCobroId,
                        principalTable: "ComprobantesCobro",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComprobanteCobroLineas_ComprobanteCobroId",
                table: "ComprobanteCobroLineas",
                column: "ComprobanteCobroId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprobanteCobroLineas_TenantId",
                table: "ComprobanteCobroLineas",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprobanteCobroLineas_TenantId_ComprobanteCobroId",
                table: "ComprobanteCobroLineas",
                columns: new[] { "TenantId", "ComprobanteCobroId" });

            migrationBuilder.CreateIndex(
                name: "IX_ComprobanteCobroSecuencias_TenantId",
                table: "ComprobanteCobroSecuencias",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprobantesCobro_ClienteId",
                table: "ComprobantesCobro",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprobantesCobro_CobroId",
                table: "ComprobantesCobro",
                column: "CobroId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprobantesCobro_FuncionarioId",
                table: "ComprobantesCobro",
                column: "FuncionarioId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprobantesCobro_TenantId",
                table: "ComprobantesCobro",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprobantesCobro_TenantId_ClienteId",
                table: "ComprobantesCobro",
                columns: new[] { "TenantId", "ClienteId" },
                filter: "[ClienteId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ComprobantesCobro_TenantId_EstadoEnvio",
                table: "ComprobantesCobro",
                columns: new[] { "TenantId", "EstadoEnvio" });

            migrationBuilder.CreateIndex(
                name: "UX_ComprobantesCobro_TenantId_CobroId",
                table: "ComprobantesCobro",
                columns: new[] { "TenantId", "CobroId" },
                unique: true,
                filter: "[EstadoEnvio] <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "UX_ComprobantesCobro_TenantId_NumeroInterno",
                table: "ComprobantesCobro",
                columns: new[] { "TenantId", "NumeroInterno" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ComprobantesCobro_TokenPublico",
                table: "ComprobantesCobro",
                column: "TokenPublico",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComprobanteCobroLineas");

            migrationBuilder.DropTable(
                name: "ComprobanteCobroSecuencias");

            migrationBuilder.DropTable(
                name: "ComprobantesCobro");
        }
    }
}
