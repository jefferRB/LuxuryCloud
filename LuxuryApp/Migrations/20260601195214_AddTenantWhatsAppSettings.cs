using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantWhatsAppSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantWhatsAppSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    SendConfirmationOnCreate = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SendReminderThreeHoursBefore = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    DailyMessageLimit = table.Column<int>(type: "int", nullable: false, defaultValue: 30),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "America/Costa_Rica"),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantWhatsAppSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantWhatsAppSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_WhatsAppMessageLogs_ActiveOutboundNotification",
                table: "WhatsAppMessageLogs",
                columns: new[] { "TenantId", "CitaId", "NotificationType", "Direction" },
                unique: true,
                filter: "[Direction] = 'Outbound' AND [CitaId] IS NOT NULL AND [Status] IN ('Pending', 'Processing', 'Sent')");

            migrationBuilder.CreateIndex(
                name: "IX_TenantWhatsAppSettings_TenantId_IsEnabled",
                table: "TenantWhatsAppSettings",
                columns: new[] { "TenantId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "UX_TenantWhatsAppSettings_TenantId",
                table: "TenantWhatsAppSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'[dbo].[fnTenantAccess]') IS NOT NULL
                   AND OBJECT_ID(N'[dbo].[TenantWhatsAppSettings]', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1
                       FROM sys.security_predicates
                       WHERE target_object_id = OBJECT_ID(N'[dbo].[TenantWhatsAppSettings]', N'U')
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
                            ON [dbo].[TenantWhatsAppSettings];';
                        EXEC sp_executesql @sql;

                        SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy + N'
                            ADD BLOCK PREDICATE [dbo].[fnTenantAccess]([TenantId])
                            ON [dbo].[TenantWhatsAppSettings] AFTER INSERT;';
                        EXEC sp_executesql @sql;

                        SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy + N'
                            ADD BLOCK PREDICATE [dbo].[fnTenantAccess]([TenantId])
                            ON [dbo].[TenantWhatsAppSettings] AFTER UPDATE;';
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
                IF OBJECT_ID(N'[dbo].[TenantWhatsAppSettings]') IS NOT NULL
                BEGIN
                    DECLARE @sql nvarchar(max) = N'';

                    SELECT @sql = @sql + N'ALTER SECURITY POLICY '
                        + QUOTENAME(SCHEMA_NAME(policy.schema_id)) + N'.' + QUOTENAME(policy.name)
                        + CASE
                            WHEN predicate.type_desc = N'FILTER' THEN N' DROP FILTER PREDICATE ON [dbo].[TenantWhatsAppSettings];'
                            ELSE N' DROP BLOCK PREDICATE ON [dbo].[TenantWhatsAppSettings];'
                          END + CHAR(13) + CHAR(10)
                    FROM sys.security_predicates AS predicate
                    INNER JOIN sys.security_policies AS policy
                        ON policy.object_id = predicate.object_id
                    WHERE predicate.target_object_id = OBJECT_ID(N'[dbo].[TenantWhatsAppSettings]');

                    IF LEN(@sql) > 0
                    BEGIN
                        EXEC sp_executesql @sql;
                    END
                END
                """);

            migrationBuilder.DropTable(
                name: "TenantWhatsAppSettings");

            migrationBuilder.DropIndex(
                name: "UX_WhatsAppMessageLogs_ActiveOutboundNotification",
                table: "WhatsAppMessageLogs");
        }
    }
}
