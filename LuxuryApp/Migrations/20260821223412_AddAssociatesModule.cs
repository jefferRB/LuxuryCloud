using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAssociatesModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConfigureSqlServerSessionOptions(migrationBuilder);

            migrationBuilder.AddColumn<int>(
                name: "AssociateId",
                table: "TenantInvestors",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Associates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Telefono = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Puesto = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    NotasInternas = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AppUsuarioId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Associates", x => x.Id);

                    table.ForeignKey(
                        name: "FK_Associates_AspNetUsers_AppUsuarioId",
                        column: x => x.AppUsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AssociatePermissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssociateId = table.Column<int>(type: "int", nullable: false),
                    Permiso = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Permitido = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssociatePermissions", x => x.Id);

                    table.ForeignKey(
                        name: "FK_AssociatePermissions_Associates_AssociateId",
                        column: x => x.AssociateId,
                        principalTable: "Associates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssociateTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssociateId = table.Column<int>(type: "int", nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssociateTypes", x => x.Id);

                    table.ForeignKey(
                        name: "FK_AssociateTypes_Associates_AssociateId",
                        column: x => x.AssociateId,
                        principalTable: "Associates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Se crea dinámicamente porque AssociateId acaba de ser agregado.
            // Esto evita resolución anticipada de la columna al generar/ejecutar
            // scripts SQL para producción.
            migrationBuilder.Sql(
                """
                EXEC sys.sp_executesql N'
                CREATE UNIQUE INDEX [UX_TenantInvestors_AssociateId]
                ON [dbo].[TenantInvestors] ([AssociateId])
                WHERE [AssociateId] IS NOT NULL;
                ';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AssociatePermissions_AssociateId",
                table: "AssociatePermissions",
                column: "AssociateId");

            migrationBuilder.CreateIndex(
                name: "IX_AssociatePermissions_TenantId",
                table: "AssociatePermissions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_AssociatePermissions_Associate_Permiso",
                table: "AssociatePermissions",
                columns: new[] { "TenantId", "AssociateId", "Permiso" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Associates_TenantId",
                table: "Associates",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Associates_TenantId_Activo",
                table: "Associates",
                columns: new[] { "TenantId", "Activo" });

            migrationBuilder.CreateIndex(
                name: "IX_Associates_TenantId_Nombre",
                table: "Associates",
                columns: new[] { "TenantId", "Nombre" });

            migrationBuilder.CreateIndex(
                name: "UX_Associates_AppUsuarioId",
                table: "Associates",
                column: "AppUsuarioId",
                unique: true,
                filter: "[AppUsuarioId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Associates_TenantId_Email",
                table: "Associates",
                columns: new[] { "TenantId", "Email" },
                unique: true,
                filter: "[Email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AssociateTypes_AssociateId",
                table: "AssociateTypes",
                column: "AssociateId");

            migrationBuilder.CreateIndex(
                name: "IX_AssociateTypes_TenantId",
                table: "AssociateTypes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_AssociateTypes_Associate_Tipo",
                table: "AssociateTypes",
                columns: new[] { "TenantId", "AssociateId", "Tipo" },
                unique: true);

            // Igual que el índice: se difiere la compilación de AssociateId.
            migrationBuilder.Sql(
                """
                EXEC sys.sp_executesql N'
                ALTER TABLE [dbo].[TenantInvestors]
                WITH CHECK
                ADD CONSTRAINT [FK_TenantInvestors_Associates_AssociateId]
                    FOREIGN KEY ([AssociateId])
                    REFERENCES [dbo].[Associates] ([Id])
                    ON DELETE NO ACTION;

                ALTER TABLE [dbo].[TenantInvestors]
                CHECK CONSTRAINT [FK_TenantInvestors_Associates_AssociateId];
                ';
                """);

            MigrateInvestorsToAssociatesAndAttachRowLevelSecurity(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConfigureSqlServerSessionOptions(migrationBuilder);

            DetachRowLevelSecurity(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "FK_TenantInvestors_Associates_AssociateId",
                table: "TenantInvestors");

            migrationBuilder.DropTable(
                name: "AssociatePermissions");

            migrationBuilder.DropTable(
                name: "AssociateTypes");

            migrationBuilder.DropTable(
                name: "Associates");

            migrationBuilder.DropIndex(
                name: "UX_TenantInvestors_AssociateId",
                table: "TenantInvestors");

            migrationBuilder.DropColumn(
                name: "AssociateId",
                table: "TenantInvestors");
        }

        private static readonly string[] TenantTables =
        {
            "Associates",
            "AssociateTypes",
            "AssociatePermissions"
        };

        /// <summary>
        /// Opciones requeridas/recomendadas por SQL Server para índices,
        /// índices filtrados y objetos relacionados.
        /// </summary>
        private static void ConfigureSqlServerSessionOptions(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SET ANSI_NULLS ON;
                SET ANSI_PADDING ON;
                SET ANSI_WARNINGS ON;
                SET ARITHABORT ON;
                SET CONCAT_NULL_YIELDS_NULL ON;
                SET QUOTED_IDENTIFIER ON;
                SET NUMERIC_ROUNDABORT OFF;
                """);
        }

        /// <summary>
        /// Convierte los inversionistas existentes en asociados y agrega las tablas
        /// nuevas a la misma política RLS que protege TenantInvestors.
        ///
        /// Las referencias a AssociateId se ejecutan mediante sp_executesql para que
        /// SQL Server las compile después de que la columna ya exista físicamente.
        /// </summary>
        private static void MigrateInvestorsToAssociatesAndAttachRowLevelSecurity(
            MigrationBuilder migrationBuilder)
        {
            var lista = string.Join(
                "," + Environment.NewLine + "                     ",
                TenantTables.Select(table => $"(N'{table}')"));

            migrationBuilder.Sql(
                $"""
                DECLARE @tablas TABLE (Nombre sysname NOT NULL);

                INSERT INTO @tablas (Nombre)
                VALUES
                    {lista};

                DECLARE @policySchema sysname;
                DECLARE @policyName sysname;
                DECLARE @qualifiedPolicy nvarchar(300);
                DECLARE @sql nvarchar(max);
                DECLARE @wasEnabled bit;
                DECLARE @tabla sysname;
                DECLARE @targetObjectId int;

                SELECT TOP (1)
                    @policySchema = SCHEMA_NAME(policy.schema_id),
                    @policyName = policy.name,
                    @wasEnabled = policy.is_enabled
                FROM sys.security_policies AS policy
                INNER JOIN sys.security_predicates AS predicate
                    ON predicate.object_id = policy.object_id
                WHERE predicate.target_object_id =
                          OBJECT_ID(N'[dbo].[TenantInvestors]', N'U')
                  AND predicate.predicate_definition LIKE N'%fnTenantAccess%'
                ORDER BY policy.name;

                IF @policyName IS NOT NULL
                BEGIN
                    SET @qualifiedPolicy =
                        QUOTENAME(@policySchema) + N'.' + QUOTENAME(@policyName);

                    IF @wasEnabled = 1
                    BEGIN
                        SET @sql =
                            N'ALTER SECURITY POLICY '
                            + @qualifiedPolicy
                            + N' WITH (STATE = OFF);';

                        EXEC sys.sp_executesql @sql;
                    END
                END;

                BEGIN TRY

                    /*
                     * IMPORTANTE:
                     * AssociateId acaba de ser creada en esta misma migración.
                     *
                     * El bloque se ejecuta dinámicamente para impedir que SQL Server
                     * intente resolver esa columna durante la compilación anticipada
                     * del script completo.
                     */
                    SET @sql = N'
                    INSERT INTO [dbo].[Associates]
                    (
                        [TenantId],
                        [Nombre],
                        [Email],
                        [Telefono],
                        [Puesto],
                        [Activo],
                        [NotasInternas],
                        [AppUsuarioId],
                        [CreatedAtUtc],
                        [UpdatedAtUtc],
                        [CreatedByUserId],
                        [UpdatedByUserId]
                    )
                    SELECT
                        investor.[TenantId],
                        investor.[Nombre],
                        investor.[Email],
                        investor.[Telefono],
                        N''Inversionista'',
                        investor.[Activo],
                        investor.[NotasInternas],
                        NULL,
                        investor.[CreatedAtUtc],
                        investor.[UpdatedAtUtc],
                        investor.[CreatedByUserId],
                        investor.[UpdatedByUserId]
                    FROM [dbo].[TenantInvestors] AS investor
                    WHERE investor.[AssociateId] IS NULL
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM [dbo].[Associates] AS existente
                          WHERE existente.[TenantId] = investor.[TenantId]
                            AND existente.[Email] = investor.[Email]
                      );

                    UPDATE investor
                    SET investor.[AssociateId] = associate.[Id]
                    FROM [dbo].[TenantInvestors] AS investor
                    INNER JOIN [dbo].[Associates] AS associate
                        ON associate.[TenantId] = investor.[TenantId]
                       AND associate.[Email] = investor.[Email]
                    WHERE investor.[AssociateId] IS NULL;

                    INSERT INTO [dbo].[AssociateTypes]
                    (
                        [TenantId],
                        [AssociateId],
                        [Tipo],
                        [CreatedAtUtc]
                    )
                    SELECT
                        investor.[TenantId],
                        investor.[AssociateId],
                        0,
                        SYSUTCDATETIME()
                    FROM [dbo].[TenantInvestors] AS investor
                    WHERE investor.[AssociateId] IS NOT NULL
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM [dbo].[AssociateTypes] AS tipo
                          WHERE tipo.[TenantId] = investor.[TenantId]
                            AND tipo.[AssociateId] = investor.[AssociateId]
                            AND tipo.[Tipo] = 0
                      );
                    ';

                    EXEC sys.sp_executesql @sql;

                    /*
                     * Agregar las tres tablas nuevas a la política RLS.
                     */
                    IF @policyName IS NOT NULL
                       AND OBJECT_ID(N'[dbo].[fnTenantAccess]') IS NOT NULL
                    BEGIN
                        DECLARE tablas_cursor CURSOR LOCAL FAST_FORWARD FOR
                            SELECT Nombre
                            FROM @tablas;

                        OPEN tablas_cursor;

                        FETCH NEXT FROM tablas_cursor INTO @tabla;

                        WHILE @@FETCH_STATUS = 0
                        BEGIN
                            SET @targetObjectId =
                                OBJECT_ID(
                                    N'[dbo].' + QUOTENAME(@tabla),
                                    N'U');

                            IF @targetObjectId IS NOT NULL
                            BEGIN
                                /*
                                 * FILTER
                                 */
                                IF NOT EXISTS
                                (
                                    SELECT 1
                                    FROM sys.security_predicates
                                    WHERE object_id = OBJECT_ID(@qualifiedPolicy)
                                      AND target_object_id = @targetObjectId
                                      AND predicate_type = 0
                                )
                                BEGIN
                                    SET @sql =
                                        N'ALTER SECURITY POLICY '
                                        + @qualifiedPolicy
                                        + N' ADD FILTER PREDICATE '
                                        + N'[dbo].[fnTenantAccess]([TenantId]) '
                                        + N'ON [dbo].'
                                        + QUOTENAME(@tabla)
                                        + N';';

                                    EXEC sys.sp_executesql @sql;
                                END;

                                /*
                                 * BLOCK AFTER INSERT
                                 */
                                IF NOT EXISTS
                                (
                                    SELECT 1
                                    FROM sys.security_predicates
                                    WHERE object_id = OBJECT_ID(@qualifiedPolicy)
                                      AND target_object_id = @targetObjectId
                                      AND predicate_type = 1
                                      AND operation = 1
                                )
                                BEGIN
                                    SET @sql =
                                        N'ALTER SECURITY POLICY '
                                        + @qualifiedPolicy
                                        + N' ADD BLOCK PREDICATE '
                                        + N'[dbo].[fnTenantAccess]([TenantId]) '
                                        + N'ON [dbo].'
                                        + QUOTENAME(@tabla)
                                        + N' AFTER INSERT;';

                                    EXEC sys.sp_executesql @sql;
                                END;

                                /*
                                 * BLOCK AFTER UPDATE
                                 */
                                IF NOT EXISTS
                                (
                                    SELECT 1
                                    FROM sys.security_predicates
                                    WHERE object_id = OBJECT_ID(@qualifiedPolicy)
                                      AND target_object_id = @targetObjectId
                                      AND predicate_type = 1
                                      AND operation = 2
                                )
                                BEGIN
                                    SET @sql =
                                        N'ALTER SECURITY POLICY '
                                        + @qualifiedPolicy
                                        + N' ADD BLOCK PREDICATE '
                                        + N'[dbo].[fnTenantAccess]([TenantId]) '
                                        + N'ON [dbo].'
                                        + QUOTENAME(@tabla)
                                        + N' AFTER UPDATE;';

                                    EXEC sys.sp_executesql @sql;
                                END;
                            END;

                            FETCH NEXT FROM tablas_cursor INTO @tabla;
                        END;

                        CLOSE tablas_cursor;
                        DEALLOCATE tablas_cursor;
                    END;

                    /*
                     * Restaurar el estado original de la política.
                     */
                    IF @policyName IS NOT NULL
                       AND @wasEnabled = 1
                    BEGIN
                        SET @sql =
                            N'ALTER SECURITY POLICY '
                            + @qualifiedPolicy
                            + N' WITH (STATE = ON);';

                        EXEC sys.sp_executesql @sql;
                    END;

                END TRY
                BEGIN CATCH

                    /*
                     * Si falla cualquier parte después de apagar RLS,
                     * intentar dejar la política exactamente como estaba.
                     */
                    IF @policyName IS NOT NULL
                       AND @wasEnabled = 1
                    BEGIN
                        SET @sql =
                            N'ALTER SECURITY POLICY '
                            + @qualifiedPolicy
                            + N' WITH (STATE = ON);';

                        EXEC sys.sp_executesql @sql;
                    END;

                    THROW;

                END CATCH;
                """);
        }

        /// <summary>
        /// Elimina de las políticas RLS los predicados correspondientes a
        /// Associates, AssociateTypes y AssociatePermissions antes de borrar
        /// las tablas.
        /// </summary>
        private static void DetachRowLevelSecurity(MigrationBuilder migrationBuilder)
        {
            var lista = string.Join(
                "," + Environment.NewLine + "                     ",
                TenantTables.Select(table => $"(N'{table}')"));

            migrationBuilder.Sql(
                $"""
                DECLARE @tablasDrop TABLE (Nombre sysname NOT NULL);

                INSERT INTO @tablasDrop (Nombre)
                VALUES
                    {lista};

                DECLARE @predicados TABLE
                (
                    Id int IDENTITY(1,1) NOT NULL,
                    PolicySchema sysname NOT NULL,
                    PolicyName sysname NOT NULL,
                    TableName sysname NOT NULL,
                    PredicateType int NOT NULL,
                    OperationDesc nvarchar(60) NULL
                );

                INSERT INTO @predicados
                (
                    PolicySchema,
                    PolicyName,
                    TableName,
                    PredicateType,
                    OperationDesc
                )
                SELECT
                    SCHEMA_NAME(policy.schema_id),
                    policy.name,
                    OBJECT_NAME(predicate.target_object_id),
                    predicate.predicate_type,
                    predicate.operation_desc
                FROM sys.security_predicates AS predicate
                INNER JOIN sys.security_policies AS policy
                    ON policy.object_id = predicate.object_id
                WHERE OBJECT_NAME(predicate.target_object_id)
                      IN (SELECT Nombre FROM @tablasDrop);

                DECLARE @id int;
                DECLARE @policySchema sysname;
                DECLARE @policyName sysname;
                DECLARE @tableName sysname;
                DECLARE @predicateType int;
                DECLARE @operationDesc nvarchar(60);
                DECLARE @dropSql nvarchar(max);

                WHILE EXISTS (SELECT 1 FROM @predicados)
                BEGIN
                    SELECT TOP (1)
                        @id = Id,
                        @policySchema = PolicySchema,
                        @policyName = PolicyName,
                        @tableName = TableName,
                        @predicateType = PredicateType,
                        @operationDesc = OperationDesc
                    FROM @predicados
                    ORDER BY Id;

                    SET @dropSql =
                        N'ALTER SECURITY POLICY '
                        + QUOTENAME(@policySchema)
                        + N'.'
                        + QUOTENAME(@policyName)
                        + CASE
                            WHEN @predicateType = 0
                                THEN N' DROP FILTER PREDICATE ON [dbo].'
                            ELSE N' DROP BLOCK PREDICATE ON [dbo].'
                          END
                        + QUOTENAME(@tableName)
                        + CASE
                            WHEN @predicateType = 1
                                 AND @operationDesc IS NOT NULL
                                THEN N' ' + @operationDesc
                            ELSE N''
                          END
                        + N';';

                    EXEC sys.sp_executesql @dropSql;

                    DELETE FROM @predicados
                    WHERE Id = @id;
                END;
                """);
        }
    }
}