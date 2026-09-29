# Local database bootstrap

The EF Core migration history in `LuxuryApp/Migrations` starts from a database that already existed
before migrations were introduced, so replaying it onto an **empty** SQL Server database fails
(see [docs/local-development.md](../docs/local-development.md#database)). This folder provides a
reproducible alternative for local development.

| File | Contents |
| --- | --- |
| `LuxuryCloud.Schema.sql` | **Schema only**: 78 tables with keys, defaults, checks and indexes; foreign keys; the Row-Level Security predicate function and security policy (100 FILTER/BLOCK predicates); and the `__EFMigrationsHistory` rows for all 65 migrations, so EF treats the database as current. |
| `LuxuryCloud.ReferenceData.sql` | Local reference data: a **development-only acceptance document** (sign-up requires an active, versioned document; this is not the production legal agreement) and the plan catalog from the repository's migrations, with provider ids left empty. |
| `activate-local-account.sql` | Local-only helper: confirms a newly registered account and grants its tenant `Exempt` access on the Business plan (there's no email or payment provider locally). |

None of these files contain business, customer, user or production data.

## Provenance

`LuxuryCloud.Schema.sql` was generated with SQL Server Management Objects (the engine behind SSMS
"Generate Scripts") from a **local development database**. That database's `__EFMigrationsHistory`
matches every migration in this repository, and it was exported schema-only (no table rows except
the migration ids). It was checked like this:

1. A new, empty database was created from the script.
2. Its structure was compared with the source database: 1,049 columns (type, length, precision,
   nullability, identity), 336 indexes and constraints (key columns, uniqueness, filters), 106
   foreign keys (delete behavior), 100 RLS predicates, 109 defaults and 9 check constraints. The
   only differences were SQL Server's auto-generated constraint names (`PK__Table__<hash>`).
3. `dotnet ef migrations list` reported all 65 migrations as applied.
4. The web app booted against it. A tenant was registered, activated with the script below, and
   signed in. The main modules loaded without errors. A customer was created, and RLS hid the row
   from a session without that tenant's context.

## Steps

Requirements: SQL Server 2019+ with the `Modern_Spanish_CI_AS` collation (the collation the
application uses), plus `sqlcmd` or SSMS.

```bash
# 1. Create the database
sqlcmd -S localhost -E -C -Q "CREATE DATABASE [LuxuryCloud_Dev] COLLATE Modern_Spanish_CI_AS"

# 2. Schema, then reference data (-I = QUOTED_IDENTIFIER ON, required by filtered indexes;
#    -f 65001 = read the files as UTF-8)
sqlcmd -S localhost -E -C -I -f 65001 -d LuxuryCloud_Dev -i database/LuxuryCloud.Schema.sql
sqlcmd -S localhost -E -C -I -f 65001 -d LuxuryCloud_Dev -i database/LuxuryCloud.ReferenceData.sql
```

Point the app at it (`ConnectionStrings:ConexionSql`, for example through the development config
template or user secrets), then run it and create an account through the sign-up page:

```bash
dotnet run --project LuxuryApp/LuxuryApp.csproj --launch-profile https
# open https://localhost:7239/Accounts/Registro and register (e.g. owner@demo-salon.example)
```

Because email and payments are disabled locally, activate the account with the helper script. Edit
`@Email` first if you used a different address:

```bash
sqlcmd -S localhost -E -C -I -f 65001 -d LuxuryCloud_Dev -i database/activate-local-account.sql
```

The app caches each tenant's access status for two minutes, so restart the app (or wait) before
signing in.

## Row-Level Security in a local database

The schema includes the production-equivalent RLS policy. The application sets
`SESSION_CONTEXT('TenantId')` on every connection it opens, so it works normally. Ad-hoc queries
from SSMS or `sqlcmd` return **no rows** from tenant tables unless you set the context first:

```sql
EXEC sp_set_session_context N'TenantId', '<tenant-guid>';
```
