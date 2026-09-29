-- =====================================================================================
-- LuxuryCloud reference data for a LOCAL development database.
-- Run once, after LuxuryCloud.Schema.sql. Safe to run again (idempotent).
--
-- 1. A development-only acceptance document. Sign-up requires an active, versioned
--    document; this one is NOT the production legal agreement and has no legal value.
-- 2. The plan catalog, with the same ids and values the AddTilopayRecurringSubscriptions
--    migration seeds. Provider ids are left NULL.
--
-- No business, customer or user data.
-- =====================================================================================
SET NOCOUNT ON;
GO

-- Development-only acceptance document. ContentHash = uppercase hex SHA-256 of the UTF-8
-- ContentHtml, as computed by ContractHashing; the app refuses a document whose hash
-- does not match its content.
IF NOT EXISTS (SELECT 1 FROM [ContractDocuments] WHERE [IsActive] = 1)
    INSERT INTO [ContractDocuments] ([Id], [Title], [VersionNumber], [ContentHtml], [ContentHash], [IsActive], [EffectiveFromUtc], [CreatedAtUtc], [UpdatedAtUtc])
    VALUES ('D0C0DE00-0000-4000-8000-00000000000A', N'LuxuryCloud - Documento de aceptacion (desarrollo local)', N'local-dev-1',
            N'<section class="contract-section">
    <h2>Documento de aceptacion para desarrollo local</h2>
    <p>Este documento existe solo para ejecutar LuxuryCloud en una base de datos local de desarrollo o demostracion. Permite probar el flujo de registro y aceptacion versionada de condiciones.</p>
    <p>No es el contrato de uso de LuxuryCloud en produccion y no tiene valor legal. Las condiciones del servicio en produccion se publican y versionan por separado.</p>
</section>
<section class="contract-section">
    <h2>Uso de datos en este entorno</h2>
    <p>Utiliza unicamente datos ficticios. Este entorno no envia correos, mensajes de WhatsApp ni cobros, porque las integraciones externas estan deshabilitadas por defecto.</p>
</section>',
            N'611404488EE2F132CE0F09F57A1C2742601FD78FC9904B56819353EED64B207A', CAST(1 AS bit), '2026-01-01T00:00:00', '2026-01-01T00:00:00', '2026-01-01T00:00:00');
GO

-- Plan catalog (same ids and values as the AddTilopayRecurringSubscriptions migration).
IF NOT EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = 'A1A61940-1080-46A7-AF9B-B74C767844DE' OR [Codigo] = N'BASIC')
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES ('A1A61940-1080-46A7-AF9B-B74C767844DE', N'BASIC', N'Basico', NULL, NULL, N'CRC', CAST(8000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), 1, NULL);
IF NOT EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = '4E36E22E-6F9F-41CA-9549-9BC548B7EC3A' OR [Codigo] = N'PRO')
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES ('4E36E22E-6F9F-41CA-9549-9BC548B7EC3A', N'PRO', N'Pro', NULL, NULL, N'CRC', CAST(20000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), 3, NULL);
IF NOT EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = '1087BDE5-404E-40DA-962E-D1E06D482361' OR [Codigo] = N'BUSINESS')
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES ('1087BDE5-404E-40DA-962E-D1E06D482361', N'BUSINESS', N'Business', NULL, NULL, N'CRC', CAST(35000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), 7, NULL);
IF NOT EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = '5A4D17FE-818A-4D8C-84C3-31BF77AE0A40' OR [Codigo] = N'WA400')
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES ('5A4D17FE-818A-4D8C-84C3-31BF77AE0A40', N'WA400', N'WhatsApp 400', NULL, NULL, N'CRC', CAST(6000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), NULL, 400);
IF NOT EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = '8758A61A-54EF-4C31-97D1-0D85E02A2F80' OR [Codigo] = N'WA800')
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES ('8758A61A-54EF-4C31-97D1-0D85E02A2F80', N'WA800', N'WhatsApp 800', NULL, NULL, N'CRC', CAST(12000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), NULL, 800);
IF NOT EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = 'AC17EA76-40A4-49F4-9750-C93DFEC3C0E0' OR [Codigo] = N'WA1200')
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES ('AC17EA76-40A4-49F4-9750-C93DFEC3C0E0', N'WA1200', N'WhatsApp 1200', NULL, NULL, N'CRC', CAST(18000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), NULL, 1200);
GO
