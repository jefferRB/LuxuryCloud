using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class funcionarioPuestoRelationshipFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[Funcionarios]', N'U') IS NOT NULL
BEGIN
    IF EXISTS (
        SELECT 1
        FROM sys.foreign_keys
        WHERE name = N'FK_Funcionarios_Puestos_PuestoIdPuesto')
    BEGIN
        ALTER TABLE [Funcionarios] DROP CONSTRAINT [FK_Funcionarios_Puestos_PuestoIdPuesto];
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'[Funcionarios]')
          AND name = N'IX_Funcionarios_PuestoIdPuesto')
    BEGIN
        DROP INDEX [IX_Funcionarios_PuestoIdPuesto] ON [Funcionarios];
    END;

    IF COL_LENGTH(N'[Funcionarios]', 'PuestoIdPuesto') IS NOT NULL
    BEGIN
        ALTER TABLE [Funcionarios] DROP COLUMN [PuestoIdPuesto];
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'[Funcionarios]')
          AND name = N'IX_Funcionarios_IdPuesto')
    BEGIN
        CREATE INDEX [IX_Funcionarios_IdPuesto] ON [Funcionarios]([IdPuesto]);
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.foreign_keys
        WHERE name = N'FK_Funcionarios_Puestos_IdPuesto')
    BEGIN
        ALTER TABLE [Funcionarios]
        ADD CONSTRAINT [FK_Funcionarios_Puestos_IdPuesto]
            FOREIGN KEY ([IdPuesto]) REFERENCES [Puestos]([IdPuesto]) ON DELETE CASCADE;
    END;
END;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[Funcionarios]', N'U') IS NOT NULL
BEGIN
    IF EXISTS (
        SELECT 1
        FROM sys.foreign_keys
        WHERE name = N'FK_Funcionarios_Puestos_IdPuesto')
    BEGIN
        ALTER TABLE [Funcionarios] DROP CONSTRAINT [FK_Funcionarios_Puestos_IdPuesto];
    END;

    IF EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'[Funcionarios]')
          AND name = N'IX_Funcionarios_IdPuesto')
    BEGIN
        DROP INDEX [IX_Funcionarios_IdPuesto] ON [Funcionarios];
    END;

    IF COL_LENGTH(N'[Funcionarios]', 'PuestoIdPuesto') IS NULL
    BEGIN
        ALTER TABLE [Funcionarios] ADD [PuestoIdPuesto] int NULL;
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'[Funcionarios]')
          AND name = N'IX_Funcionarios_PuestoIdPuesto')
    BEGIN
        CREATE INDEX [IX_Funcionarios_PuestoIdPuesto] ON [Funcionarios]([PuestoIdPuesto]);
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.foreign_keys
        WHERE name = N'FK_Funcionarios_Puestos_PuestoIdPuesto')
    BEGIN
        ALTER TABLE [Funcionarios]
        ADD CONSTRAINT [FK_Funcionarios_Puestos_PuestoIdPuesto]
            FOREIGN KEY ([PuestoIdPuesto]) REFERENCES [Puestos]([IdPuesto]);
    END;
END;
");
        }
    }
}
