using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class clientFuncionarioManagementFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // =====================================================================================
            // 0) DESHABILITAR TEMPORALMENTE RLS PARA HACER EL BACKFILL COMPLETO
            // =====================================================================================

            migrationBuilder.Sql(
            """
            IF EXISTS (
                SELECT 1
                FROM sys.security_policies
                WHERE name = N'TenantSecurityPolicy'
                  AND schema_id = SCHEMA_ID(N'dbo')
            )
            BEGIN
                ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy] WITH (STATE = OFF);
            END
            """);

            // =====================================================================================
            // 1) SOLTAR FKs PROBLEMÁTICAS DE FORMA TOLERANTE A NOMBRES REALES EN SQL SERVER
            // =====================================================================================

            migrationBuilder.Sql(
            """
            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Egresos_Categorias_CategoriaId')
            BEGIN
                ALTER TABLE [dbo].[Egresos] DROP CONSTRAINT [FK_Egresos_Categorias_CategoriaId];
            END

            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Egresos_Categorias')
            BEGIN
                ALTER TABLE [dbo].[Egresos] DROP CONSTRAINT [FK_Egresos_Categorias];
            END
            """);

            migrationBuilder.Sql(
            """
            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Funcionarios_Puestos_IdPuesto')
            BEGIN
                ALTER TABLE [dbo].[Funcionarios] DROP CONSTRAINT [FK_Funcionarios_Puestos_IdPuesto];
            END

            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Funcionarios_Puestos')
            BEGIN
                ALTER TABLE [dbo].[Funcionarios] DROP CONSTRAINT [FK_Funcionarios_Puestos];
            END
            """);

            // =====================================================================================
            // 2) SOLTAR CUALQUIER FK QUE APUNTE A CLIENTES, PORQUE LA PK VA A CAMBIAR
            // =====================================================================================

            migrationBuilder.Sql(
                """
                DECLARE @DropSql nvarchar(max) = N'';

                SELECT @DropSql = STRING_AGG(
                    N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id)) +
                    N' DROP CONSTRAINT ' + QUOTENAME(name) + N';',
                    N' ')
                FROM sys.foreign_keys
                WHERE referenced_object_id = OBJECT_ID(N'[dbo].[Clientes]');

                IF @DropSql IS NOT NULL AND LEN(@DropSql) > 0
                BEGIN
                    EXEC sp_executesql @DropSql;
                END
                """);

            // =====================================================================================
            // 3) AGREGAR NUEVA PK SURROGATE A CLIENTES
            // =====================================================================================

            migrationBuilder.Sql(
                """
                ALTER TABLE [Clientes]
                ADD [Id] int IDENTITY(1,1) NOT NULL;
                """);

            // =====================================================================================
            // 4) AGREGAR NUEVAS COLUMNAS FK HACIA CLIENTES POR ID
            // =====================================================================================

            migrationBuilder.AddColumn<int>(
                name: "ClienteId",
                table: "ClienteVisitas",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClienteId",
                table: "ClienteImagenes",
                type: "int",
                nullable: true);

            // =====================================================================================
            // 5) NORMALIZAR DATOS
            // =====================================================================================

            migrationBuilder.Sql(
                """
                UPDATE [Clientes]
                SET [CorreoElectronico] = NULL
                WHERE LTRIM(RTRIM(ISNULL([CorreoElectronico], N''))) = N'';
                """);

            // =====================================================================================
            // 6) BACKFILL DE ClienteId DESDE (TenantId + NumeroTelefono)
            // =====================================================================================

            migrationBuilder.Sql(
            """
            UPDATE cv
            SET cv.[ClienteId] = c.[Id]
            FROM [ClienteVisitas] cv
            INNER JOIN [Clientes] c
                ON LTRIM(RTRIM(c.[NumeroTelefono])) = LTRIM(RTRIM(cv.[NumeroTelefono]))
               AND c.[TenantId] = cv.[TenantId];

            UPDATE ci
            SET ci.[ClienteId] = c.[Id]
            FROM [ClienteImagenes] ci
            INNER JOIN [Clientes] c
                ON LTRIM(RTRIM(c.[NumeroTelefono])) = LTRIM(RTRIM(ci.[NumeroTelefono]))
               AND c.[TenantId] = ci.[TenantId];
            """);

            // =====================================================================================
            // 7) CAMBIAR PK DE CLIENTES
            // =====================================================================================

            migrationBuilder.Sql(
            """
            DECLARE @PkName nvarchar(200);

            SELECT @PkName = kc.name
            FROM sys.key_constraints kc
            WHERE kc.parent_object_id = OBJECT_ID(N'[dbo].[Clientes]')
              AND kc.[type] = 'PK';

            IF @PkName IS NOT NULL
            BEGIN
                EXEC(N'ALTER TABLE [dbo].[Clientes] DROP CONSTRAINT [' + @PkName + ']');
            END
            """);

            // =====================================================================================
            // 8) AJUSTAR TIPOS / LONGITUDES
            // =====================================================================================

            migrationBuilder.AlterColumn<string>(
                name: "NumeroTelefono",
                table: "ClienteVisitas",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Nombre",
                table: "Clientes",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "CorreoElectronico",
                table: "Clientes",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "NumeroTelefono",
                table: "Clientes",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "NumeroTelefono",
                table: "ClienteImagenes",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            // =====================================================================================
            // 8.5) LIMPIAR DATOS HUÉRFANOS
            // =====================================================================================

            migrationBuilder.Sql(
            """
            DELETE cv
            FROM ClienteVisitas cv
            LEFT JOIN Clientes c
                ON LTRIM(RTRIM(c.NumeroTelefono)) = LTRIM(RTRIM(cv.NumeroTelefono))
               AND c.TenantId = cv.TenantId
            WHERE c.Id IS NULL;

            DELETE ci
            FROM ClienteImagenes ci
            LEFT JOIN Clientes c
                ON LTRIM(RTRIM(c.NumeroTelefono)) = LTRIM(RTRIM(ci.NumeroTelefono))
               AND c.TenantId = ci.TenantId
            WHERE c.Id IS NULL;
            """);

            // =====================================================================================
            // 9) VALIDAR QUE NO QUEDEN HUÉRFANOS
            // =====================================================================================

            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [ClienteVisitas] WHERE [ClienteId] IS NULL)
                BEGIN
                    THROW 50000, 'No se pudo vincular una o más visitas con su cliente durante la migración.', 1;
                END

                IF EXISTS (SELECT 1 FROM [ClienteImagenes] WHERE [ClienteId] IS NULL)
                BEGIN
                    THROW 50001, 'No se pudo vincular una o más imágenes con su cliente durante la migración.', 1;
                END
                """);

            migrationBuilder.AlterColumn<int>(
                name: "ClienteId",
                table: "ClienteVisitas",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ClienteId",
                table: "ClienteImagenes",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            // =====================================================================================
            // 10) NUEVA PK E ÍNDICES
            // =====================================================================================

            migrationBuilder.AddPrimaryKey(
                name: "PK_Clientes",
                table: "Clientes",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteVisitas_ClienteId",
                table: "ClienteVisitas",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteVisitas_TenantId_ClienteId_FechaVisita",
                table: "ClienteVisitas",
                columns: new[] { "TenantId", "ClienteId", "FechaVisita" });

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_TenantId_NumeroTelefono",
                table: "Clientes",
                columns: new[] { "TenantId", "NumeroTelefono" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClienteImagenes_ClienteId",
                table: "ClienteImagenes",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_ClienteImagenes_TenantId_ClienteId",
                table: "ClienteImagenes",
                columns: new[] { "TenantId", "ClienteId" });

            // =====================================================================================
            // 11) RECREAR FKs CON LA NUEVA SEMÁNTICA
            // =====================================================================================

            migrationBuilder.AddForeignKey(
                name: "FK_ClienteImagenes_Clientes_ClienteId",
                table: "ClienteImagenes",
                column: "ClienteId",
                principalTable: "Clientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ClienteVisitas_Clientes_ClienteId",
                table: "ClienteVisitas",
                column: "ClienteId",
                principalTable: "Clientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Egresos_Categorias_CategoriaId",
                table: "Egresos",
                column: "CategoriaId",
                principalTable: "Categorias",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Funcionarios_Puestos_IdPuesto",
                table: "Funcionarios",
                column: "IdPuesto",
                principalTable: "Puestos",
                principalColumn: "IdPuesto",
                onDelete: ReferentialAction.Restrict);

            // =====================================================================================
            // 12) REHABILITAR RLS
            // =====================================================================================

            migrationBuilder.Sql(
            """
            IF EXISTS (
                SELECT 1
                FROM sys.security_policies
                WHERE name = N'TenantSecurityPolicy'
                  AND schema_id = SCHEMA_ID(N'dbo')
            )
            BEGIN
                ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy] WITH (STATE = ON);
            END
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // =====================================================================================
            // 0) DESHABILITAR TEMPORALMENTE RLS PARA EL ROLLBACK
            // =====================================================================================

            migrationBuilder.Sql(
            """
            IF EXISTS (
                SELECT 1
                FROM sys.security_policies
                WHERE name = N'TenantSecurityPolicy'
                  AND schema_id = SCHEMA_ID(N'dbo')
            )
            BEGIN
                ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy] WITH (STATE = OFF);
            END
            """);

            // =====================================================================================
            // 1) SOLTAR FKs NUEVAS
            // =====================================================================================

            migrationBuilder.Sql(
            """
            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ClienteImagenes_Clientes_ClienteId')
            BEGIN
                ALTER TABLE [dbo].[ClienteImagenes] DROP CONSTRAINT [FK_ClienteImagenes_Clientes_ClienteId];
            END

            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ClienteVisitas_Clientes_ClienteId')
            BEGIN
                ALTER TABLE [dbo].[ClienteVisitas] DROP CONSTRAINT [FK_ClienteVisitas_Clientes_ClienteId];
            END

            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Egresos_Categorias_CategoriaId')
            BEGIN
                ALTER TABLE [dbo].[Egresos] DROP CONSTRAINT [FK_Egresos_Categorias_CategoriaId];
            END

            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Egresos_Categorias')
            BEGIN
                ALTER TABLE [dbo].[Egresos] DROP CONSTRAINT [FK_Egresos_Categorias];
            END

            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Funcionarios_Puestos_IdPuesto')
            BEGIN
                ALTER TABLE [dbo].[Funcionarios] DROP CONSTRAINT [FK_Funcionarios_Puestos_IdPuesto];
            END

            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Funcionarios_Puestos')
            BEGIN
                ALTER TABLE [dbo].[Funcionarios] DROP CONSTRAINT [FK_Funcionarios_Puestos];
            END
            """);

            // =====================================================================================
            // 2) ELIMINAR ÍNDICES / PK / COLUMNAS NUEVAS
            // =====================================================================================

            migrationBuilder.DropIndex(
                name: "IX_ClienteVisitas_ClienteId",
                table: "ClienteVisitas");

            migrationBuilder.DropIndex(
                name: "IX_ClienteVisitas_TenantId_ClienteId_FechaVisita",
                table: "ClienteVisitas");

            migrationBuilder.Sql(
            """
            DECLARE @PkName nvarchar(200);

            SELECT @PkName = kc.name
            FROM sys.key_constraints kc
            WHERE kc.parent_object_id = OBJECT_ID(N'[dbo].[Clientes]')
              AND kc.[type] = 'PK';

            IF @PkName IS NOT NULL
            BEGIN
                EXEC(N'ALTER TABLE [dbo].[Clientes] DROP CONSTRAINT [' + @PkName + ']');
            END
            """);

            migrationBuilder.DropIndex(
                name: "IX_Clientes_TenantId_NumeroTelefono",
                table: "Clientes");

            migrationBuilder.DropIndex(
                name: "IX_ClienteImagenes_ClienteId",
                table: "ClienteImagenes");

            migrationBuilder.DropIndex(
                name: "IX_ClienteImagenes_TenantId_ClienteId",
                table: "ClienteImagenes");

            migrationBuilder.DropColumn(
                name: "ClienteId",
                table: "ClienteVisitas");

            migrationBuilder.DropColumn(
                name: "ClienteId",
                table: "ClienteImagenes");

            migrationBuilder.Sql(
                """
                ALTER TABLE [Clientes] DROP COLUMN [Id];
                """);

            // =====================================================================================
            // 3) RESTAURAR TIPOS ANTERIORES
            // =====================================================================================

            migrationBuilder.AlterColumn<string>(
                name: "NumeroTelefono",
                table: "ClienteVisitas",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "NumeroTelefono",
                table: "Clientes",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "Nombre",
                table: "Clientes",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(150)",
                oldMaxLength: 150);

            migrationBuilder.AlterColumn<string>(
                name: "CorreoElectronico",
                table: "Clientes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NumeroTelefono",
                table: "ClienteImagenes",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            // =====================================================================================
            // 4) RESTAURAR PK LEGACY
            // =====================================================================================

            migrationBuilder.AddPrimaryKey(
                name: "PK_Clientes",
                table: "Clientes",
                column: "NumeroTelefono");

            // =====================================================================================
            // 5) RESTAURAR FKs LEGACY
            // =====================================================================================

            migrationBuilder.AddForeignKey(
                name: "FK_Egresos_Categorias_CategoriaId",
                table: "Egresos",
                column: "CategoriaId",
                principalTable: "Categorias",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Funcionarios_Puestos_IdPuesto",
                table: "Funcionarios",
                column: "IdPuesto",
                principalTable: "Puestos",
                principalColumn: "IdPuesto",
                onDelete: ReferentialAction.Cascade);

            // =====================================================================================
            // 6) REHABILITAR RLS
            // =====================================================================================

            migrationBuilder.Sql(
            """
            IF EXISTS (
                SELECT 1
                FROM sys.security_policies
                WHERE name = N'TenantSecurityPolicy'
                  AND schema_id = SCHEMA_ID(N'dbo')
            )
            BEGIN
                ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy] WITH (STATE = ON);
            END
            """);
        }
    }
}