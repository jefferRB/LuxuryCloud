using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class saasPaymentsTilopay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[Tenants]', N'U') IS NULL
BEGIN
    CREATE TABLE [Tenants](
        [Id] uniqueidentifier NOT NULL,
        [Nombre] nvarchar(150) NOT NULL,
        [FechaCreacion] datetime2 NOT NULL,
        [Activo] bit NOT NULL,
        CONSTRAINT [PK_Tenants] PRIMARY KEY ([Id])
    );
END
ELSE
BEGIN
    UPDATE [Tenants] SET [FechaCreacion] = SYSUTCDATETIME() WHERE [FechaCreacion] IS NULL;
    UPDATE [Tenants] SET [Activo] = 1 WHERE [Activo] IS NULL;
    ALTER TABLE [Tenants] ALTER COLUMN [Nombre] nvarchar(150) NOT NULL;
    ALTER TABLE [Tenants] ALTER COLUMN [FechaCreacion] datetime2 NOT NULL;
    ALTER TABLE [Tenants] ALTER COLUMN [Activo] bit NOT NULL;
END;
");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[Features]', N'U') IS NULL
BEGIN
    CREATE TABLE [Features](
        [Id] uniqueidentifier NOT NULL,
        [Codigo] nvarchar(max) NOT NULL,
        [Nombre] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_Features] PRIMARY KEY ([Id])
    );
END;
");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[Planes]', N'U') IS NULL
BEGIN
    CREATE TABLE [Planes](
        [Id] uniqueidentifier NOT NULL,
        [Nombre] nvarchar(50) NOT NULL,
        [ProviderProductId] nvarchar(100) NULL,
        [ProviderPriceId] nvarchar(100) NULL,
        [Moneda] nvarchar(100) NOT NULL,
        [PrecioMensual] decimal(18,2) NOT NULL,
        [Activo] bit NOT NULL,
        [MaxFuncionarios] int NULL,
        CONSTRAINT [PK_Planes] PRIMARY KEY ([Id])
    );
END
ELSE
BEGIN
    IF COL_LENGTH(N'[Planes]', 'StripeProductId') IS NOT NULL AND COL_LENGTH(N'[Planes]', 'ProviderProductId') IS NULL
        EXEC sp_rename N'[Planes].[StripeProductId]', N'ProviderProductId', N'COLUMN';

    IF COL_LENGTH(N'[Planes]', 'StripePriceId') IS NOT NULL AND COL_LENGTH(N'[Planes]', 'ProviderPriceId') IS NULL
        EXEC sp_rename N'[Planes].[StripePriceId]', N'ProviderPriceId', N'COLUMN';

    IF COL_LENGTH(N'[Planes]', 'ProviderProductId') IS NULL
        ALTER TABLE [Planes] ADD [ProviderProductId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[Planes]', 'ProviderPriceId') IS NULL
        ALTER TABLE [Planes] ADD [ProviderPriceId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[Planes]', 'MaxFuncionarios') IS NULL
        ALTER TABLE [Planes] ADD [MaxFuncionarios] int NULL;

    UPDATE [Planes] SET [Moneda] = 'CRC' WHERE [Moneda] IS NULL;
    UPDATE [Planes] SET [Activo] = 1 WHERE [Activo] IS NULL;

    ALTER TABLE [Planes] ALTER COLUMN [ProviderProductId] nvarchar(100) NULL;
    ALTER TABLE [Planes] ALTER COLUMN [ProviderPriceId] nvarchar(100) NULL;
    ALTER TABLE [Planes] ALTER COLUMN [Moneda] nvarchar(100) NOT NULL;
    ALTER TABLE [Planes] ALTER COLUMN [Activo] bit NOT NULL;
END;
");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[PlanFeatures]', N'U') IS NULL
BEGIN
    CREATE TABLE [PlanFeatures](
        [PlanId] uniqueidentifier NOT NULL,
        [FeatureId] uniqueidentifier NOT NULL,
        [Limite] int NULL,
        CONSTRAINT [PK_PlanFeatures] PRIMARY KEY ([PlanId], [FeatureId]),
        CONSTRAINT [FK_PlanFeatures_Features_FeatureId] FOREIGN KEY ([FeatureId]) REFERENCES [Features]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_PlanFeatures_Planes_PlanId] FOREIGN KEY ([PlanId]) REFERENCES [Planes]([Id]) ON DELETE CASCADE
    );
END;

IF OBJECT_ID(N'[PlanFeatures]', N'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[PlanFeatures]')
      AND name = N'IX_PlanFeatures_FeatureId')
BEGIN
    CREATE INDEX [IX_PlanFeatures_FeatureId] ON [PlanFeatures]([FeatureId]);
END;
");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'[AspNetUsers]', 'TenantId') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers]
    ADD [TenantId] uniqueidentifier NOT NULL
        CONSTRAINT [DF_AspNetUsers_TenantId] DEFAULT ('00000000-0000-0000-0000-000000000000');
END;

UPDATE [AspNetUsers] SET [State] = 0 WHERE [State] IS NULL;
ALTER TABLE [AspNetUsers] ALTER COLUMN [State] bit NOT NULL;

IF COL_LENGTH(N'[AspNetUsers]', 'Discriminator') IS NOT NULL
BEGIN
    DECLARE @DropDiscriminatorDefault nvarchar(max);
    SELECT @DropDiscriminatorDefault =
        N'ALTER TABLE [AspNetUsers] DROP CONSTRAINT [' + dc.name + N'];'
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON dc.parent_object_id = c.object_id
       AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'[AspNetUsers]')
      AND c.name = N'Discriminator';

    IF @DropDiscriminatorDefault IS NOT NULL
        EXEC sp_executesql @DropDiscriminatorDefault;

    ALTER TABLE [AspNetUsers] DROP COLUMN [Discriminator];
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[AspNetUsers]')
      AND name = N'IX_AspNetUsers_TenantId')
BEGIN
    CREATE INDEX [IX_AspNetUsers_TenantId] ON [AspNetUsers]([TenantId]);
END;

IF EXISTS (
    SELECT 1
    FROM [AspNetUsers] u
    LEFT JOIN [Tenants] t ON u.[TenantId] = t.[Id]
    WHERE u.[TenantId] <> '00000000-0000-0000-0000-000000000000'
      AND t.[Id] IS NULL)
BEGIN
    THROW 51000, 'No se puede crear la FK AspNetUsers->Tenants porque existen usuarios con TenantId inexistente.', 1;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_AspNetUsers_Tenants_TenantId')
BEGIN
    ALTER TABLE [AspNetUsers]
    ADD CONSTRAINT [FK_AspNetUsers_Tenants_TenantId]
        FOREIGN KEY ([TenantId]) REFERENCES [Tenants]([Id]) ON DELETE CASCADE;
END;
");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[Suscripciones]', N'U') IS NULL
BEGIN
    CREATE TABLE [Suscripciones](
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [PlanId] uniqueidentifier NOT NULL,
        [Proveedor] int NOT NULL,
        [ProviderCustomerId] nvarchar(100) NULL,
        [ProviderSubscriptionId] nvarchar(100) NULL,
        [ProviderTransactionId] nvarchar(100) NULL,
        [ProviderPaymentLinkId] nvarchar(100) NULL,
        [ProviderReference] nvarchar(100) NULL,
        [UltimoEventoProveedorId] nvarchar(100) NULL,
        [Estado] int NOT NULL,
        [FechaInicio] datetime2 NOT NULL,
        [FechaFin] datetime2 NULL,
        [FechaTrialFin] datetime2 NULL,
        [CancelAtPeriodEnd] bit NOT NULL,
        [FechaUltimoPagoUtc] datetime2 NULL,
        [FechaUltimaActualizacionUtc] datetime2 NULL,
        [MotivoEstado] nvarchar(250) NULL,
        CONSTRAINT [PK_Suscripciones] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Suscripciones_Planes_PlanId] FOREIGN KEY ([PlanId]) REFERENCES [Planes]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_Suscripciones_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants]([Id]) ON DELETE CASCADE
    );
END
ELSE
BEGIN
    IF COL_LENGTH(N'[Suscripciones]', 'StripeCustomerId') IS NOT NULL AND COL_LENGTH(N'[Suscripciones]', 'ProviderCustomerId') IS NULL
        EXEC sp_rename N'[Suscripciones].[StripeCustomerId]', N'ProviderCustomerId', N'COLUMN';

    IF COL_LENGTH(N'[Suscripciones]', 'StripeSubscriptionId') IS NOT NULL AND COL_LENGTH(N'[Suscripciones]', 'ProviderSubscriptionId') IS NULL
        EXEC sp_rename N'[Suscripciones].[StripeSubscriptionId]', N'ProviderSubscriptionId', N'COLUMN';

    IF COL_LENGTH(N'[Suscripciones]', 'Proveedor') IS NULL
        ALTER TABLE [Suscripciones] ADD [Proveedor] int NOT NULL CONSTRAINT [DF_Suscripciones_Proveedor] DEFAULT (1);

    IF COL_LENGTH(N'[Suscripciones]', 'ProviderCustomerId') IS NULL
        ALTER TABLE [Suscripciones] ADD [ProviderCustomerId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[Suscripciones]', 'ProviderSubscriptionId') IS NULL
        ALTER TABLE [Suscripciones] ADD [ProviderSubscriptionId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[Suscripciones]', 'ProviderTransactionId') IS NULL
        ALTER TABLE [Suscripciones] ADD [ProviderTransactionId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[Suscripciones]', 'ProviderPaymentLinkId') IS NULL
        ALTER TABLE [Suscripciones] ADD [ProviderPaymentLinkId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[Suscripciones]', 'ProviderReference') IS NULL
        ALTER TABLE [Suscripciones] ADD [ProviderReference] nvarchar(100) NULL;

    IF COL_LENGTH(N'[Suscripciones]', 'UltimoEventoProveedorId') IS NULL
        ALTER TABLE [Suscripciones] ADD [UltimoEventoProveedorId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[Suscripciones]', 'FechaUltimoPagoUtc') IS NULL
        ALTER TABLE [Suscripciones] ADD [FechaUltimoPagoUtc] datetime2 NULL;

    IF COL_LENGTH(N'[Suscripciones]', 'FechaUltimaActualizacionUtc') IS NULL
        ALTER TABLE [Suscripciones] ADD [FechaUltimaActualizacionUtc] datetime2 NULL;

    IF COL_LENGTH(N'[Suscripciones]', 'MotivoEstado') IS NULL
        ALTER TABLE [Suscripciones] ADD [MotivoEstado] nvarchar(250) NULL;

    IF COL_LENGTH(N'[Suscripciones]', 'Proveedor') IS NOT NULL
        EXEC(N'UPDATE [Suscripciones] SET [Proveedor] = 1 WHERE [Proveedor] = 0;');
    UPDATE [Suscripciones] SET [CancelAtPeriodEnd] = 0 WHERE [CancelAtPeriodEnd] IS NULL;

    ALTER TABLE [Suscripciones] ALTER COLUMN [CancelAtPeriodEnd] bit NOT NULL;
END;

IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[Suscripciones]')
      AND name = N'IX_Suscripciones_TenantId'
      AND is_unique = 0)
BEGIN
    DROP INDEX [IX_Suscripciones_TenantId] ON [Suscripciones];
END;

IF EXISTS (
    SELECT [TenantId]
    FROM [Suscripciones]
    GROUP BY [TenantId]
    HAVING COUNT(*) > 1)
BEGIN
    THROW 51001, 'No se puede crear la unicidad por tenant en Suscripciones porque existen multiples filas para el mismo tenant.', 1;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[Suscripciones]')
      AND name = N'IX_Suscripciones_TenantId')
BEGIN
    CREATE UNIQUE INDEX [IX_Suscripciones_TenantId] ON [Suscripciones]([TenantId]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[Suscripciones]')
      AND name = N'IX_Suscripciones_PlanId')
BEGIN
    CREATE INDEX [IX_Suscripciones_PlanId] ON [Suscripciones]([PlanId]);
END;
");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[HistorialSuscripciones]', N'U') IS NULL
BEGIN
    CREATE TABLE [HistorialSuscripciones](
        [Id] uniqueidentifier NOT NULL,
        [SuscripcionId] uniqueidentifier NOT NULL,
        [PlanIdAnterior] uniqueidentifier NULL,
        [PlanIdNuevo] uniqueidentifier NULL,
        [FechaCambio] datetime2 NOT NULL,
        [Proveedor] int NULL,
        [Motivo] nvarchar(250) NULL,
        CONSTRAINT [PK_HistorialSuscripciones] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_HistorialSuscripciones_Suscripciones_SuscripcionId] FOREIGN KEY ([SuscripcionId]) REFERENCES [Suscripciones]([Id]) ON DELETE CASCADE
    );
END
ELSE
BEGIN
    IF COL_LENGTH(N'[HistorialSuscripciones]', 'Proveedor') IS NULL
        ALTER TABLE [HistorialSuscripciones] ADD [Proveedor] int NULL;

    IF COL_LENGTH(N'[HistorialSuscripciones]', 'Motivo') IS NULL
        ALTER TABLE [HistorialSuscripciones] ADD [Motivo] nvarchar(250) NULL;

    UPDATE [HistorialSuscripciones] SET [FechaCambio] = SYSUTCDATETIME() WHERE [FechaCambio] IS NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[HistorialSuscripciones]')
      AND name = N'IX_HistorialSuscripciones_SuscripcionId')
BEGIN
    CREATE INDEX [IX_HistorialSuscripciones_SuscripcionId] ON [HistorialSuscripciones]([SuscripcionId]);
END;
");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[StripeEventos]', N'U') IS NOT NULL AND OBJECT_ID(N'[EventosPago]', N'U') IS NULL
BEGIN
    EXEC sp_rename N'[StripeEventos]', N'EventosPago';
END;

IF OBJECT_ID(N'[EventosPago]', N'U') IS NULL
BEGIN
    CREATE TABLE [EventosPago](
        [Id] uniqueidentifier NOT NULL,
        [Proveedor] int NOT NULL,
        [TenantId] uniqueidentifier NULL,
        [PlanId] uniqueidentifier NULL,
        [PagoSuscripcionId] uniqueidentifier NULL,
        [ProveedorEventId] nvarchar(100) NOT NULL,
        [Tipo] nvarchar(100) NOT NULL,
        [ReferenciaExterna] nvarchar(100) NULL,
        [ProviderTransactionId] nvarchar(100) NULL,
        [CorrelationId] nvarchar(100) NULL,
        [Procesado] bit NOT NULL,
        [EstadoProcesamiento] nvarchar(50) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [FechaRecepcionUtc] datetime2 NOT NULL,
        [FechaProcesamientoUtc] datetime2 NULL,
        [Error] nvarchar(500) NULL,
        CONSTRAINT [PK_EventosPago] PRIMARY KEY ([Id])
    );
END
ELSE
BEGIN
    IF COL_LENGTH(N'[EventosPago]', 'StripeEventId') IS NOT NULL AND COL_LENGTH(N'[EventosPago]', 'ProveedorEventId') IS NULL
        EXEC sp_rename N'[EventosPago].[StripeEventId]', N'ProveedorEventId', N'COLUMN';

    IF COL_LENGTH(N'[EventosPago]', 'Proveedor') IS NULL
        ALTER TABLE [EventosPago] ADD [Proveedor] int NOT NULL CONSTRAINT [DF_EventosPago_Proveedor] DEFAULT (1);

    IF COL_LENGTH(N'[EventosPago]', 'TenantId') IS NULL
        ALTER TABLE [EventosPago] ADD [TenantId] uniqueidentifier NULL;

    IF COL_LENGTH(N'[EventosPago]', 'PlanId') IS NULL
        ALTER TABLE [EventosPago] ADD [PlanId] uniqueidentifier NULL;

    IF COL_LENGTH(N'[EventosPago]', 'PagoSuscripcionId') IS NULL
        ALTER TABLE [EventosPago] ADD [PagoSuscripcionId] uniqueidentifier NULL;

    IF COL_LENGTH(N'[EventosPago]', 'ReferenciaExterna') IS NULL
        ALTER TABLE [EventosPago] ADD [ReferenciaExterna] nvarchar(100) NULL;

    IF COL_LENGTH(N'[EventosPago]', 'ProviderTransactionId') IS NULL
        ALTER TABLE [EventosPago] ADD [ProviderTransactionId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[EventosPago]', 'CorrelationId') IS NULL
        ALTER TABLE [EventosPago] ADD [CorrelationId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[EventosPago]', 'EstadoProcesamiento') IS NULL
        ALTER TABLE [EventosPago] ADD [EstadoProcesamiento] nvarchar(50) NOT NULL CONSTRAINT [DF_EventosPago_EstadoProcesamiento] DEFAULT ('Pendiente');

    IF COL_LENGTH(N'[EventosPago]', 'FechaRecepcionUtc') IS NULL
        ALTER TABLE [EventosPago] ADD [FechaRecepcionUtc] datetime2 NOT NULL CONSTRAINT [DF_EventosPago_FechaRecepcionUtc] DEFAULT (SYSUTCDATETIME());

    IF COL_LENGTH(N'[EventosPago]', 'FechaProcesamientoUtc') IS NULL
        ALTER TABLE [EventosPago] ADD [FechaProcesamientoUtc] datetime2 NULL;

    IF COL_LENGTH(N'[EventosPago]', 'Error') IS NULL
        ALTER TABLE [EventosPago] ADD [Error] nvarchar(500) NULL;

    IF COL_LENGTH(N'[EventosPago]', 'EstadoProcesamiento') IS NOT NULL
        EXEC(N'UPDATE [EventosPago]
              SET [EstadoProcesamiento] = CASE WHEN [Procesado] = 1 THEN ''Procesado'' ELSE ''Recibido'' END
              WHERE [EstadoProcesamiento] IS NULL OR [EstadoProcesamiento] = '''';');

    IF COL_LENGTH(N'[EventosPago]', 'Fecha') IS NOT NULL
    BEGIN
        EXEC(N'UPDATE [EventosPago] SET [FechaRecepcionUtc] = [Fecha] WHERE [FechaRecepcionUtc] IS NULL;');
        EXEC(N'UPDATE [EventosPago] SET [FechaProcesamientoUtc] = [Fecha] WHERE [Procesado] = 1 AND [FechaProcesamientoUtc] IS NULL;');
        DECLARE @DropFechaDefault nvarchar(max);
        SELECT @DropFechaDefault =
            N'ALTER TABLE [EventosPago] DROP CONSTRAINT [' + dc.name + N'];'
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c
            ON dc.parent_object_id = c.object_id
           AND dc.parent_column_id = c.column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'[EventosPago]')
          AND c.name = N'Fecha';

        IF @DropFechaDefault IS NOT NULL
            EXEC sp_executesql @DropFechaDefault;

        ALTER TABLE [EventosPago] DROP COLUMN [Fecha];
    END;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[EventosPago]')
      AND name = N'IX_EventosPago_Proveedor_ProveedorEventId')
BEGIN
    CREATE UNIQUE INDEX [IX_EventosPago_Proveedor_ProveedorEventId]
        ON [EventosPago]([Proveedor], [ProveedorEventId]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[EventosPago]')
      AND name = N'IX_EventosPago_Proveedor_ReferenciaExterna')
BEGIN
    CREATE INDEX [IX_EventosPago_Proveedor_ReferenciaExterna]
        ON [EventosPago]([Proveedor], [ReferenciaExterna]);
END;
");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[PagosSuscripcion]', N'U') IS NULL
BEGIN
    CREATE TABLE [PagosSuscripcion](
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [PlanId] uniqueidentifier NOT NULL,
        [Proveedor] int NOT NULL,
        [Estado] int NOT NULL,
        [ReferenciaInterna] nvarchar(100) NOT NULL,
        [ProviderCheckoutId] nvarchar(100) NULL,
        [ProviderTransactionId] nvarchar(100) NULL,
        [ProviderReference] nvarchar(100) NULL,
        [ProviderResultCode] nvarchar(50) NULL,
        [ProviderResultMessage] nvarchar(300) NULL,
        [ProviderAuthorizationCode] nvarchar(100) NULL,
        [ProviderCardBrand] nvarchar(50) NULL,
        [ProviderCardLast4] nvarchar(20) NULL,
        [CheckoutUrl] nvarchar(500) NULL,
        [ClienteNombre] nvarchar(150) NULL,
        [ClienteEmail] nvarchar(200) NULL,
        [Descripcion] nvarchar(250) NOT NULL,
        [Monto] decimal(18,2) NOT NULL,
        [Moneda] nvarchar(10) NOT NULL,
        [FechaCreacionUtc] datetime2 NOT NULL,
        [FechaActualizacionUtc] datetime2 NULL,
        [FechaConfirmacionUtc] datetime2 NULL,
        [UltimoPayloadProveedor] nvarchar(max) NULL,
        CONSTRAINT [PK_PagosSuscripcion] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PagosSuscripcion_Planes_PlanId] FOREIGN KEY ([PlanId]) REFERENCES [Planes]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_PagosSuscripcion_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants]([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[PagosSuscripcion]')
      AND name = N'IX_PagosSuscripcion_TenantId')
BEGIN
    CREATE INDEX [IX_PagosSuscripcion_TenantId] ON [PagosSuscripcion]([TenantId]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[PagosSuscripcion]')
      AND name = N'IX_PagosSuscripcion_PlanId')
BEGIN
    CREATE INDEX [IX_PagosSuscripcion_PlanId] ON [PagosSuscripcion]([PlanId]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[PagosSuscripcion]')
      AND name = N'IX_PagosSuscripcion_Proveedor_ReferenciaInterna')
BEGIN
    CREATE UNIQUE INDEX [IX_PagosSuscripcion_Proveedor_ReferenciaInterna]
        ON [PagosSuscripcion]([Proveedor], [ReferenciaInterna]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[PagosSuscripcion]')
      AND name = N'IX_PagosSuscripcion_Proveedor_ProviderReference')
BEGIN
    CREATE UNIQUE INDEX [IX_PagosSuscripcion_Proveedor_ProviderReference]
        ON [PagosSuscripcion]([Proveedor], [ProviderReference])
        WHERE [ProviderReference] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[PagosSuscripcion]')
      AND name = N'IX_PagosSuscripcion_Proveedor_ProviderTransactionId')
BEGIN
    CREATE UNIQUE INDEX [IX_PagosSuscripcion_Proveedor_ProviderTransactionId]
        ON [PagosSuscripcion]([Proveedor], [ProviderTransactionId])
        WHERE [ProviderTransactionId] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[PagosSuscripcion]')
      AND name = N'IX_PagosSuscripcion_Proveedor_ProviderCheckoutId')
BEGIN
    CREATE UNIQUE INDEX [IX_PagosSuscripcion_Proveedor_ProviderCheckoutId]
        ON [PagosSuscripcion]([Proveedor], [ProviderCheckoutId])
        WHERE [ProviderCheckoutId] IS NOT NULL;
END;
");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[Facturas]', N'U') IS NULL
BEGIN
    CREATE TABLE [Facturas](
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [SuscripcionId] uniqueidentifier NULL,
        [PagoSuscripcionId] uniqueidentifier NULL,
        [Proveedor] int NOT NULL,
        [ProviderInvoiceId] nvarchar(100) NULL,
        [ProviderTransactionId] nvarchar(100) NULL,
        [ProviderReference] nvarchar(100) NULL,
        [Monto] decimal(18,2) NULL,
        [Moneda] nvarchar(10) NOT NULL,
        [Estado] nvarchar(50) NOT NULL,
        [Fecha] datetime2 NULL,
        CONSTRAINT [PK_Facturas] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Facturas_PagosSuscripcion_PagoSuscripcionId] FOREIGN KEY ([PagoSuscripcionId]) REFERENCES [PagosSuscripcion]([Id]),
        CONSTRAINT [FK_Facturas_Suscripciones_SuscripcionId] FOREIGN KEY ([SuscripcionId]) REFERENCES [Suscripciones]([Id]),
        CONSTRAINT [FK_Facturas_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants]([Id]) ON DELETE CASCADE
    );
END
ELSE
BEGIN
    IF COL_LENGTH(N'[Facturas]', 'StripeInvoiceId') IS NOT NULL AND COL_LENGTH(N'[Facturas]', 'ProviderInvoiceId') IS NULL
        EXEC sp_rename N'[Facturas].[StripeInvoiceId]', N'ProviderInvoiceId', N'COLUMN';

    IF COL_LENGTH(N'[Facturas]', 'SuscripcionId') IS NULL
        ALTER TABLE [Facturas] ADD [SuscripcionId] uniqueidentifier NULL;

    IF COL_LENGTH(N'[Facturas]', 'PagoSuscripcionId') IS NULL
        ALTER TABLE [Facturas] ADD [PagoSuscripcionId] uniqueidentifier NULL;

    IF COL_LENGTH(N'[Facturas]', 'Proveedor') IS NULL
        ALTER TABLE [Facturas] ADD [Proveedor] int NOT NULL CONSTRAINT [DF_Facturas_Proveedor] DEFAULT (1);

    IF COL_LENGTH(N'[Facturas]', 'ProviderInvoiceId') IS NULL
        ALTER TABLE [Facturas] ADD [ProviderInvoiceId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[Facturas]', 'ProviderTransactionId') IS NULL
        ALTER TABLE [Facturas] ADD [ProviderTransactionId] nvarchar(100) NULL;

    IF COL_LENGTH(N'[Facturas]', 'ProviderReference') IS NULL
        ALTER TABLE [Facturas] ADD [ProviderReference] nvarchar(100) NULL;

    UPDATE [Facturas] SET [Moneda] = 'CRC' WHERE [Moneda] IS NULL;
    UPDATE [Facturas] SET [Estado] = 'Desconocido' WHERE [Estado] IS NULL;
    IF COL_LENGTH(N'[Facturas]', 'Proveedor') IS NOT NULL
        EXEC(N'UPDATE [Facturas] SET [Proveedor] = 1 WHERE [Proveedor] = 0;');

    ALTER TABLE [Facturas] ALTER COLUMN [Moneda] nvarchar(10) NOT NULL;
    ALTER TABLE [Facturas] ALTER COLUMN [Estado] nvarchar(50) NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[Facturas]')
      AND name = N'IX_Facturas_TenantId')
BEGIN
    CREATE INDEX [IX_Facturas_TenantId] ON [Facturas]([TenantId]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[Facturas]')
      AND name = N'IX_Facturas_SuscripcionId')
BEGIN
    EXEC(N'CREATE INDEX [IX_Facturas_SuscripcionId] ON [Facturas]([SuscripcionId]);');
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[Facturas]')
      AND name = N'IX_Facturas_PagoSuscripcionId')
BEGIN
    EXEC(N'CREATE INDEX [IX_Facturas_PagoSuscripcionId] ON [Facturas]([PagoSuscripcionId]);');
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[Facturas]')
      AND name = N'IX_Facturas_Proveedor_ProviderTransactionId')
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Facturas_Proveedor_ProviderTransactionId]
            ON [Facturas]([Proveedor], [ProviderTransactionId])
            WHERE [ProviderTransactionId] IS NOT NULL;');
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[Facturas]')
      AND name = N'IX_Facturas_Proveedor_ProviderReference_Estado')
BEGIN
    EXEC(N'CREATE INDEX [IX_Facturas_Proveedor_ProviderReference_Estado]
            ON [Facturas]([Proveedor], [ProviderReference], [Estado])
            WHERE [ProviderReference] IS NOT NULL;');
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_Facturas_Suscripciones_SuscripcionId')
BEGIN
    EXEC(N'ALTER TABLE [Facturas]
           ADD CONSTRAINT [FK_Facturas_Suscripciones_SuscripcionId]
               FOREIGN KEY ([SuscripcionId]) REFERENCES [Suscripciones]([Id]);');
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_Facturas_PagosSuscripcion_PagoSuscripcionId')
BEGIN
    EXEC(N'ALTER TABLE [Facturas]
           ADD CONSTRAINT [FK_Facturas_PagosSuscripcion_PagoSuscripcionId]
               FOREIGN KEY ([PagoSuscripcionId]) REFERENCES [PagosSuscripcion]([Id]);');
END;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_Facturas_PagosSuscripcion_PagoSuscripcionId')
BEGIN
    ALTER TABLE [Facturas] DROP CONSTRAINT [FK_Facturas_PagosSuscripcion_PagoSuscripcionId];
END;

IF EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_Facturas_Suscripciones_SuscripcionId')
BEGIN
    ALTER TABLE [Facturas] DROP CONSTRAINT [FK_Facturas_Suscripciones_SuscripcionId];
END;

IF OBJECT_ID(N'[Facturas]', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[Facturas]') AND name = N'IX_Facturas_Proveedor_ProviderReference_Estado')
        DROP INDEX [IX_Facturas_Proveedor_ProviderReference_Estado] ON [Facturas];

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[Facturas]') AND name = N'IX_Facturas_Proveedor_ProviderTransactionId')
        DROP INDEX [IX_Facturas_Proveedor_ProviderTransactionId] ON [Facturas];

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[Facturas]') AND name = N'IX_Facturas_PagoSuscripcionId')
        DROP INDEX [IX_Facturas_PagoSuscripcionId] ON [Facturas];

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[Facturas]') AND name = N'IX_Facturas_SuscripcionId')
        DROP INDEX [IX_Facturas_SuscripcionId] ON [Facturas];

    IF COL_LENGTH(N'[Facturas]', 'ProviderInvoiceId') IS NOT NULL AND COL_LENGTH(N'[Facturas]', 'StripeInvoiceId') IS NULL
        EXEC sp_rename N'[Facturas].[ProviderInvoiceId]', N'StripeInvoiceId', N'COLUMN';

    IF COL_LENGTH(N'[Facturas]', 'ProviderTransactionId') IS NOT NULL
        ALTER TABLE [Facturas] DROP COLUMN [ProviderTransactionId];

    IF COL_LENGTH(N'[Facturas]', 'ProviderReference') IS NOT NULL
        ALTER TABLE [Facturas] DROP COLUMN [ProviderReference];

    IF COL_LENGTH(N'[Facturas]', 'PagoSuscripcionId') IS NOT NULL
        ALTER TABLE [Facturas] DROP COLUMN [PagoSuscripcionId];

    IF COL_LENGTH(N'[Facturas]', 'SuscripcionId') IS NOT NULL
        ALTER TABLE [Facturas] DROP COLUMN [SuscripcionId];

    IF COL_LENGTH(N'[Facturas]', 'Proveedor') IS NOT NULL
        ALTER TABLE [Facturas] DROP COLUMN [Proveedor];
END;

IF OBJECT_ID(N'[PagosSuscripcion]', N'U') IS NOT NULL
BEGIN
    DROP TABLE [PagosSuscripcion];
END;

IF OBJECT_ID(N'[EventosPago]', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[EventosPago]') AND name = N'IX_EventosPago_Proveedor_ReferenciaExterna')
        DROP INDEX [IX_EventosPago_Proveedor_ReferenciaExterna] ON [EventosPago];

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[EventosPago]') AND name = N'IX_EventosPago_Proveedor_ProveedorEventId')
        DROP INDEX [IX_EventosPago_Proveedor_ProveedorEventId] ON [EventosPago];

    IF COL_LENGTH(N'[EventosPago]', 'Fecha') IS NULL
        ALTER TABLE [EventosPago] ADD [Fecha] datetime2 NULL;

    UPDATE [EventosPago] SET [Fecha] = ISNULL([FechaRecepcionUtc], SYSUTCDATETIME()) WHERE [Fecha] IS NULL;

    IF COL_LENGTH(N'[EventosPago]', 'Error') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [Error];

    IF COL_LENGTH(N'[EventosPago]', 'FechaProcesamientoUtc') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [FechaProcesamientoUtc];

    IF COL_LENGTH(N'[EventosPago]', 'FechaRecepcionUtc') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [FechaRecepcionUtc];

    IF COL_LENGTH(N'[EventosPago]', 'EstadoProcesamiento') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [EstadoProcesamiento];

    IF COL_LENGTH(N'[EventosPago]', 'CorrelationId') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [CorrelationId];

    IF COL_LENGTH(N'[EventosPago]', 'ProviderTransactionId') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [ProviderTransactionId];

    IF COL_LENGTH(N'[EventosPago]', 'ReferenciaExterna') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [ReferenciaExterna];

    IF COL_LENGTH(N'[EventosPago]', 'PagoSuscripcionId') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [PagoSuscripcionId];

    IF COL_LENGTH(N'[EventosPago]', 'PlanId') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [PlanId];

    IF COL_LENGTH(N'[EventosPago]', 'TenantId') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [TenantId];

    IF COL_LENGTH(N'[EventosPago]', 'Proveedor') IS NOT NULL
        ALTER TABLE [EventosPago] DROP COLUMN [Proveedor];

    IF COL_LENGTH(N'[EventosPago]', 'ProveedorEventId') IS NOT NULL AND COL_LENGTH(N'[EventosPago]', 'StripeEventId') IS NULL
        EXEC sp_rename N'[EventosPago].[ProveedorEventId]', N'StripeEventId', N'COLUMN';

    IF OBJECT_ID(N'[StripeEventos]', N'U') IS NULL
        EXEC sp_rename N'[EventosPago]', N'StripeEventos';
END;

IF OBJECT_ID(N'[HistorialSuscripciones]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'[HistorialSuscripciones]', 'Motivo') IS NOT NULL
        ALTER TABLE [HistorialSuscripciones] DROP COLUMN [Motivo];

    IF COL_LENGTH(N'[HistorialSuscripciones]', 'Proveedor') IS NOT NULL
        ALTER TABLE [HistorialSuscripciones] DROP COLUMN [Proveedor];
END;

IF OBJECT_ID(N'[Suscripciones]', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[Suscripciones]') AND name = N'IX_Suscripciones_TenantId')
        DROP INDEX [IX_Suscripciones_TenantId] ON [Suscripciones];

    IF COL_LENGTH(N'[Suscripciones]', 'MotivoEstado') IS NOT NULL
        ALTER TABLE [Suscripciones] DROP COLUMN [MotivoEstado];

    IF COL_LENGTH(N'[Suscripciones]', 'FechaUltimaActualizacionUtc') IS NOT NULL
        ALTER TABLE [Suscripciones] DROP COLUMN [FechaUltimaActualizacionUtc];

    IF COL_LENGTH(N'[Suscripciones]', 'FechaUltimoPagoUtc') IS NOT NULL
        ALTER TABLE [Suscripciones] DROP COLUMN [FechaUltimoPagoUtc];

    IF COL_LENGTH(N'[Suscripciones]', 'UltimoEventoProveedorId') IS NOT NULL
        ALTER TABLE [Suscripciones] DROP COLUMN [UltimoEventoProveedorId];

    IF COL_LENGTH(N'[Suscripciones]', 'ProviderReference') IS NOT NULL
        ALTER TABLE [Suscripciones] DROP COLUMN [ProviderReference];

    IF COL_LENGTH(N'[Suscripciones]', 'ProviderPaymentLinkId') IS NOT NULL
        ALTER TABLE [Suscripciones] DROP COLUMN [ProviderPaymentLinkId];

    IF COL_LENGTH(N'[Suscripciones]', 'ProviderTransactionId') IS NOT NULL
        ALTER TABLE [Suscripciones] DROP COLUMN [ProviderTransactionId];

    IF COL_LENGTH(N'[Suscripciones]', 'Proveedor') IS NOT NULL
        ALTER TABLE [Suscripciones] DROP COLUMN [Proveedor];

    IF COL_LENGTH(N'[Suscripciones]', 'ProviderSubscriptionId') IS NOT NULL AND COL_LENGTH(N'[Suscripciones]', 'StripeSubscriptionId') IS NULL
        EXEC sp_rename N'[Suscripciones].[ProviderSubscriptionId]', N'StripeSubscriptionId', N'COLUMN';

    IF COL_LENGTH(N'[Suscripciones]', 'ProviderCustomerId') IS NOT NULL AND COL_LENGTH(N'[Suscripciones]', 'StripeCustomerId') IS NULL
        EXEC sp_rename N'[Suscripciones].[ProviderCustomerId]', N'StripeCustomerId', N'COLUMN';
END;

IF OBJECT_ID(N'[Planes]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'[Planes]', 'ProviderPriceId') IS NOT NULL AND COL_LENGTH(N'[Planes]', 'StripePriceId') IS NULL
        EXEC sp_rename N'[Planes].[ProviderPriceId]', N'StripePriceId', N'COLUMN';

    IF COL_LENGTH(N'[Planes]', 'ProviderProductId') IS NOT NULL AND COL_LENGTH(N'[Planes]', 'StripeProductId') IS NULL
        EXEC sp_rename N'[Planes].[ProviderProductId]', N'StripeProductId', N'COLUMN';
END;

IF EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_AspNetUsers_Tenants_TenantId')
BEGIN
    ALTER TABLE [AspNetUsers] DROP CONSTRAINT [FK_AspNetUsers_Tenants_TenantId];
END;

IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[AspNetUsers]')
      AND name = N'IX_AspNetUsers_TenantId')
BEGIN
    DROP INDEX [IX_AspNetUsers_TenantId] ON [AspNetUsers];
END;

IF COL_LENGTH(N'[AspNetUsers]', 'Discriminator') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers] ADD [Discriminator] nvarchar(13) NOT NULL CONSTRAINT [DF_AspNetUsers_Discriminator] DEFAULT ('');
END;

ALTER TABLE [AspNetUsers] ALTER COLUMN [State] bit NULL;
");
        }
    }
}
