# Local development

## Prerequisites

| Tool | Needed for |
| --- | --- |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | Build, tests, running the app |
| SQL Server 2019+ (Developer/Express) and `sqlcmd` | Running the web app; the optional `SqlServer` test category |
| `dotnet-ef` (`dotnet tool install --global dotnet-ef`) | Working with migrations |

The main test suite **doesn't need a database server**: it runs against SQLite in-memory.

## Build

```bash
dotnet restore LuxuryApp.slnx
dotnet build LuxuryApp.slnx -c Release
```

## Tests

The suite lives in `LuxuryApp/LuxuryApp.Tests` (xUnit, about 2,300 test cases). It covers the tax
engine, payroll, profit and investor calculations, booking and availability, billing and webhook
processing, reconciliation, and tenant isolation and security. It also uses `TestHost` to drive the
real HTTP security pipeline (cookies, antiforgery, rate limits).

```bash
# Everything that runs without a database server
dotnet test LuxuryApp.slnx -c Release --filter "Category!=SqlServer"

# Full suite, including the SQL Server tests
dotnet test LuxuryApp.slnx -c Release
```

The `SqlServer` tests check the RLS session-context interceptor and the RLS migrations against a
real engine. They create a throwaway database named `LuxuryAppRls_<guid>`, drop it when they
finish, never read application configuration, and never touch another database.

By default they connect to the local default instance with Windows integrated authentication. To use
another **test** server, such as a disposable container, set `LUXURYCLOUD_TEST_SQLSERVER` to its
connection string (the catalog in it is ignored):

```bash
LUXURYCLOUD_TEST_SQLSERVER="Server=localhost,1433;User ID=sa;Password=<test-only>;TrustServerCertificate=True" \
  dotnet test LuxuryApp.slnx -c Release --filter "Category=SqlServer"
```

CI runs this category against an ephemeral SQL Server service container.

## Configuration

`LuxuryApp/appsettings.json` holds only non-secret defaults and is **inert by default**. Every
credential is an empty placeholder, and every background worker and integration that could reach
an external service or change billing state is disabled. Production values are supplied per
environment and are not part of this repository.

Provide local values in one of these ways (all are git-ignored or stored outside the repo):

1. **Copy the template:**
   ```bash
   cp LuxuryApp/appsettings.Development.example.json LuxuryApp/appsettings.Development.json
   ```
   It sets the local connection string and public base URL.
2. **.NET user secrets** (the project already has a `UserSecretsId`):
   ```bash
   dotnet user-secrets --project LuxuryApp set "ConnectionStrings:ConexionSql" "Server=localhost;Database=LuxuryCloud_Dev;Trusted_Connection=True;TrustServerCertificate=True;"
   ```
3. **Environment variables**, using the `Section__Key` convention, e.g. `ConnectionStrings__ConexionSql`.

### Configuration reference

| Key | Purpose | Default in `appsettings.json` |
| --- | --- | --- |
| `ConnectionStrings:ConexionSql` | SQL Server connection string | empty *(required to run the app)* |
| `PublicBaseUrl` | Absolute base URL used in emails and background jobs | empty |
| `Platform:SuperAdminEmail` | Existing account to promote to platform super-admin at startup | empty (nobody is promoted) |
| `DataProtection:KeysPath` | Persistent key ring for auth cookies (production) | empty (platform default store) |
| `Email:SmtpPassword` | Resend API key | empty. Senders skip sending and log a warning. |
| `Tilopay:*`, `TilopayRepeat:*`, `TilopayRepeatAdmin:*` | Payment gateway credentials, plan catalog and hosted checkout links | credentials and links empty, `Enabled: false` |
| `Payments:ValidatePublicCallbackReachability` | Probe the public webhook URL before creating a checkout | `false` |
| `MetaWhatsApp:*` | WhatsApp Cloud API credentials and template names | `Enabled: false` |
| `Stripe:*` | Stripe webhook secret and keys | empty |
| `S3Storage:*`, `PublicImages:Provider` | S3-compatible storage for public images | `Local` provider |
| `RegistrationSecurity:Turnstile:*` | Cloudflare Turnstile keys | disabled |
| `BillingReconciliation:Enabled` | Master switch for reconciliation, plan-change cancellation retries and subscription lifecycle workers | `false` |
| `BillingPaymentRecovery:Enabled` | Failed-payment recovery worker | `false` |
| `MonthlyReports:SchedulerEnabled`, `InvestorStatements:SchedulerEnabled` | Scheduled report and statement generation | `false` |
| `Platform:CommercialSnapshot:Enabled` | Monthly platform snapshot worker | `false` |

Only use **sandbox or test credentials** locally.

## Database

LuxuryCloud targets SQL Server. The schema is managed with EF Core migrations
(`LuxuryApp/Migrations`, 65 migrations), several of which contain hand-written SQL for data
backfills and Row-Level Security predicates.

The migration history begins from a database that already existed before EF migrations were
introduced. The early migrations assume tables and columns from that original schema; for example,
one migration adds a column and indexes it in the same SQL batch, which only compiles when the
column already exists. As a result, `dotnet ef database update` against an **empty** database
stops at the third migration. Migrations apply correctly to databases that already have the baseline
schema, which is how the application has been deployed.

For local development, use the **schema-only baseline** in [`database/`](../database/README.md).
It creates the current schema, including the RLS policy, and marks all 65 migrations as applied, so
future migrations apply on top of it normally. It was checked by rebuilding a database from it and
comparing that database's structure with the source database.

## Running the app

1. Create the database from the baseline and reference data, as described in
   [database/README.md](../database/README.md#steps).
2. Point the app at it (template, user secrets or environment variable; see above) and run:
   ```bash
   dotnet run --project LuxuryApp/LuxuryApp.csproj --launch-profile https
   ```
   The app listens on `https://localhost:7239` (and `http://localhost:5069`) and seeds the Identity
   roles on startup.
3. Register a business at `/Accounts/Registro`. Email confirmation is required and no email is sent
   locally, so run `database/activate-local-account.sql`. It confirms the account and gives the
   tenant `Exempt` access on the Business plan. Restart the app (tenant access is cached for two
   minutes) and sign in.

If `Platform:SuperAdminEmail` matches an existing user, that user is promoted to platform
super-admin at startup and can open the platform console.

## Working with migrations

```bash
dotnet ef migrations add <Name> --project LuxuryApp/LuxuryApp.csproj
dotnet ef migrations script <From> <To> --idempotent --project LuxuryApp/LuxuryApp.csproj
```

Conventions used in this codebase:

- Review generated migrations before applying them. Hand-written SQL is common for backfills and RLS.
- A migration that adds a tenant-owned table also registers it with the RLS policy, without
  disabling the policy.
- Prefer additive, backward-compatible changes. Destructive changes need an explicit data plan.
