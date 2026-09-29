-- =====================================================================================
-- LOCAL DEVELOPMENT ONLY. Activates an account created through the normal sign-up page
-- (/Accounts/Registro) without email delivery or a payment provider:
--   * marks the email as confirmed (email sending is disabled locally);
--   * sets the tenant's commercial access to Exempt (1) with the Business plan as its forced
--     plan, so no subscription is required (run LuxuryCloud.ReferenceData.sql first).
-- Never run this against a shared or production database.
-- =====================================================================================
SET NOCOUNT ON;

DECLARE @Email nvarchar(256) = N'owner@demo-salon.example';   -- the email you registered with

UPDATE [dbo].[AspNetUsers]
SET [EmailConfirmed] = 1
WHERE [NormalizedEmail] = UPPER(@Email);

UPDATE t
SET t.[CommercialAccessMode] = 1,  -- TenantCommercialAccessMode.Exempt
    t.[ForcedPlanId] = (SELECT [Id] FROM [dbo].[Planes] WHERE [Codigo] = N'BUSINESS')
FROM [dbo].[Tenants] AS t
JOIN [dbo].[AspNetUsers] AS u ON u.[TenantId] = t.[Id]
WHERE u.[NormalizedEmail] = UPPER(@Email);

SELECT u.[Email], u.[EmailConfirmed], t.[Nombre] AS [Tenant], t.[CommercialAccessMode], p.[Codigo] AS [ForcedPlan]
FROM [dbo].[AspNetUsers] AS u
JOIN [dbo].[Tenants] AS t ON t.[Id] = u.[TenantId]
LEFT JOIN [dbo].[Planes] AS p ON p.[Id] = t.[ForcedPlanId]
WHERE u.[NormalizedEmail] = UPPER(@Email);
