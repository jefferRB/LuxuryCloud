using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaWhatsAppNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CanceladaPorWhatsAppUtc",
                table: "Citas",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmacionWhatsAppEnviadaUtc",
                table: "Citas",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmadaPorWhatsAppUtc",
                table: "Citas",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EstadoConfirmacionWhatsApp",
                table: "Citas",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Pendiente");

            migrationBuilder.AddColumn<DateTime>(
                name: "RecordatorioWhatsAppTresHorasEnviadoUtc",
                table: "Citas",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UltimaRespuestaWhatsAppUtc",
                table: "Citas",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UltimoMetaMessageId",
                table: "Citas",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WhatsAppMessageLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CitaId = table.Column<int>(type: "int", nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    NotificationType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MetaMessageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ContextMessageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RecipientPhoneE164 = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SenderPhoneE164 = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    WaId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TemplateName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProcessingStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppMessageLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WhatsAppMessageLogs_Citas_CitaId",
                        column: x => x.CitaId,
                        principalTable: "Citas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessageLogs_CitaId",
                table: "WhatsAppMessageLogs",
                column: "CitaId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessageLogs_ContextMessageId",
                table: "WhatsAppMessageLogs",
                column: "ContextMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessageLogs_TenantId",
                table: "WhatsAppMessageLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessageLogs_TenantId_CitaId",
                table: "WhatsAppMessageLogs",
                columns: new[] { "TenantId", "CitaId" });

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessageLogs_TenantId_CreatedAtUtc",
                table: "WhatsAppMessageLogs",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessageLogs_TenantId_NotificationType_Status",
                table: "WhatsAppMessageLogs",
                columns: new[] { "TenantId", "NotificationType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessageLogs_TenantId_RecipientPhone_CreatedAtUtc",
                table: "WhatsAppMessageLogs",
                columns: new[] { "TenantId", "RecipientPhoneE164", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_WhatsAppMessageLogs_MetaMessageId",
                table: "WhatsAppMessageLogs",
                column: "MetaMessageId",
                unique: true,
                filter: "[MetaMessageId] IS NOT NULL");

            migrationBuilder.Sql(
     """
    IF OBJECT_ID(N'[dbo].[fnTenantAccess]') IS NOT NULL
       AND OBJECT_ID(N'[dbo].[WhatsAppMessageLogs]', N'U') IS NOT NULL
       AND NOT EXISTS (
           SELECT 1
           FROM sys.security_predicates
           WHERE target_object_id = OBJECT_ID(N'[dbo].[WhatsAppMessageLogs]', N'U')
       )
    BEGIN
        DECLARE @policySchema sysname;
        DECLARE @policyName sysname;
        DECLARE @qualifiedPolicy nvarchar(300);
        DECLARE @sql nvarchar(max);
        DECLARE @wasEnabled bit;

        SELECT TOP (1)
            @policySchema = SCHEMA_NAME(policy.schema_id),
            @policyName = policy.name,
            @wasEnabled = policy.is_enabled
        FROM sys.security_policies AS policy
        INNER JOIN sys.security_predicates AS predicate
            ON predicate.object_id = policy.object_id
        WHERE predicate.predicate_definition LIKE N'%fnTenantAccess%'
        ORDER BY policy.name;

        IF @policyName IS NOT NULL
        BEGIN
            SET @qualifiedPolicy = QUOTENAME(@policySchema) + N'.' + QUOTENAME(@policyName);

            IF @wasEnabled = 1
            BEGIN
                SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy + N' WITH (STATE = OFF);';
                EXEC sp_executesql @sql;
            END

            SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy + N'
                ADD FILTER PREDICATE [dbo].[fnTenantAccess]([TenantId])
                ON [dbo].[WhatsAppMessageLogs];';
            EXEC sp_executesql @sql;

            SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy + N'
                ADD BLOCK PREDICATE [dbo].[fnTenantAccess]([TenantId])
                ON [dbo].[WhatsAppMessageLogs] AFTER INSERT;';
            EXEC sp_executesql @sql;

            SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy + N'
                ADD BLOCK PREDICATE [dbo].[fnTenantAccess]([TenantId])
                ON [dbo].[WhatsAppMessageLogs] AFTER UPDATE;';
            EXEC sp_executesql @sql;

            IF @wasEnabled = 1
            BEGIN
                SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy + N' WITH (STATE = ON);';
                EXEC sp_executesql @sql;
            END
        END
    END
    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'[dbo].[WhatsAppMessageLogs]') IS NOT NULL
                BEGIN
                    DECLARE @sql nvarchar(max) = N'';

                    SELECT @sql = @sql + N'ALTER SECURITY POLICY '
                        + QUOTENAME(SCHEMA_NAME(policy.schema_id)) + N'.' + QUOTENAME(policy.name)
                        + CASE
                            WHEN predicate.type_desc = N'FILTER' THEN N' DROP FILTER PREDICATE ON [dbo].[WhatsAppMessageLogs];'
                            ELSE N' DROP BLOCK PREDICATE ON [dbo].[WhatsAppMessageLogs];'
                          END + CHAR(13) + CHAR(10)
                    FROM sys.security_predicates AS predicate
                    INNER JOIN sys.security_policies AS policy
                        ON policy.object_id = predicate.object_id
                    WHERE predicate.target_object_id = OBJECT_ID(N'[dbo].[WhatsAppMessageLogs]');

                    IF LEN(@sql) > 0
                    BEGIN
                        EXEC sp_executesql @sql;
                    END
                END
                """);

            migrationBuilder.DropTable(
                name: "WhatsAppMessageLogs");

            migrationBuilder.DropColumn(
                name: "CanceladaPorWhatsAppUtc",
                table: "Citas");

            migrationBuilder.DropColumn(
                name: "ConfirmacionWhatsAppEnviadaUtc",
                table: "Citas");

            migrationBuilder.DropColumn(
                name: "ConfirmadaPorWhatsAppUtc",
                table: "Citas");

            migrationBuilder.DropColumn(
                name: "EstadoConfirmacionWhatsApp",
                table: "Citas");

            migrationBuilder.DropColumn(
                name: "RecordatorioWhatsAppTresHorasEnviadoUtc",
                table: "Citas");

            migrationBuilder.DropColumn(
                name: "UltimaRespuestaWhatsAppUtc",
                table: "Citas");

            migrationBuilder.DropColumn(
                name: "UltimoMetaMessageId",
                table: "Citas");
        }
    }
}
