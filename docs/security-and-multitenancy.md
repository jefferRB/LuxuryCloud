# Security and multi-tenancy

LuxuryCloud stores financial and customer data for many independent businesses in **one shared
SQL Server database**. The main security goal is that one tenant can never read or change another
tenant's data, even when application code has a bug. Isolation is therefore enforced in three
independent layers, and each layer has its own automated tests.

This document describes the design. Deployment-specific values (hosts, keys, policies on the live
server) are intentionally left out.

## Tenant model

- A `Tenant` is a business account. Every user (`AppUsuario`) belongs to exactly one tenant.
- Tenant-owned entities implement `ITenantEntity` (a `TenantId` column). There are dozens of them:
  customers, appointments, payments, expenses, staff, payroll settlements, receipts, booking requests,
  WhatsApp logs, investor statements, and so on.
- The current tenant is resolved by `TenantProvider`, which checks, in order:
  1. an explicit **ambient tenant scope** (`ITenantExecutionContextAccessor.BeginScope(tenantId)`), used
     by workers and by webhooks once the payment is matched to a tenant;
  2. the authenticated user's `TenantId` claim, cached per request.
- The tenant is **never** taken from client input. Form or JSON values named `TenantId` are ignored.

## Layer 1: EF Core global query filters

`ApplicationDbContext.OnModelCreating` walks the model and attaches a query filter to every
`ITenantEntity` (plus an index on `TenantId`). The filter expression reads the current tenant
**at query time**, not at context construction, so a single context always follows the active
scope. Ordinary LINQ queries only ever see the current tenant's rows.

`IgnoreQueryFilters()` is reserved for platform-level and reconciliation code paths, which always
add an explicit tenant predicate or run under an explicit tenant scope.

## Layer 2: `SaveChanges` tenant guards

Every `SaveChanges`/`SaveChangesAsync` call runs guards over tracked `ITenantEntity` entries:

- **Inserts** get the current `TenantId` stamped on them, overwriting whatever the caller set.
- **Updates and deletes** mark `TenantId` as unmodifiable and compare the *persisted* tenant (read
  from the database) with the current tenant. A mismatch is logged and the operation is aborted.
- **Relationships** are validated: a tenant-scoped foreign key must point to a principal that
  belongs to the same tenant. This blocks "attach another tenant's customer to my appointment"
  attacks even when the ID is guessed correctly.

## Layer 3: SQL Server Row-Level Security

The database enforces isolation as well, so a missed filter, a raw SQL query or a future bug still
can't cross tenants.

- `TenantSessionConnectionInterceptor` (an EF `DbConnectionInterceptor`) calls
  `sp_set_session_context 'TenantId'` **each time a connection is opened**, and resets it to `NULL`
  **before the connection returns to the pool**. That way a pooled connection can never carry one
  tenant's context into another request. Tests cover both behaviors.
- A schema-bound inline predicate function compares each row's `TenantId` with
  `SESSION_CONTEXT(N'TenantId')`. A security policy applies it as a **FILTER** predicate (reads) and
  as **BLOCK** predicates `AFTER INSERT` / `AFTER UPDATE` (writes).
- Migrations that introduce tenant-owned tables also register those tables with the security
  policy. The recent ones do this **without switching the policy off**, so there is no exposure
  window during deployment. `LiquidacionesRowLevelSecurityTests` checks this guarantee by applying
  the real migration SQL to a temporary SQL Server database.

## Background jobs and webhooks

Code that runs outside an HTTP request has no user claims, so it has to establish a tenant
explicitly:

- Workers use `TenantExecutionService.RunForEachActiveTenantAsync`, which gives each tenant its own
  DI scope and ambient tenant context.
- Payment webhooks first authenticate the call, then look up the payment attempt by provider
  reference, then open a tenant scope for that payment before writing anything. All three layers
  therefore apply to webhook-driven writes too.

## Authentication

- **ASP.NET Core Identity** with cookie authentication and persisted Data Protection keys.
- Password policy: at least 8 characters with an uppercase letter; platform accounts need 12. The
  account locks after 5 failed attempts.
- The **security stamp is validated on every request** (`ValidationInterval = TimeSpan.Zero`), so
  disabling a user, changing a password or revoking access takes effect on the next request.
- A **tenant session validator** re-checks on each request that the user still exists, is active,
  isn't locked out and still belongs to the tenant.
- An **absolute session lifetime** caps persistent sessions, even though sliding expiration keeps
  renewing the cookie.
- **TOTP MFA enrollment** can be enforced for platform super-admins (configuration flag).
- Registration anti-abuse: email confirmation before the tenant becomes usable, a honeypot field,
  suspicious-email heuristics, per-IP and per-email rate limits, optional Cloudflare Turnstile, and
  optional expiry of unverified sign-ups.

## Authorization

| Principal | Mechanism |
| --- | --- |
| Tenant owner (`Administrador`) | Role policy. Full access inside their own tenant. |
| Staff member (`Funcionario`) | Role + staff claim. Limited "Mi Portal" that only shows the staff member's own appointments, production and receipts, with ownership checked server-side on every action. |
| Associate (`Asociado`: investor, partner, marketing, accountant) | **Permission-based.** `[RequirePermission("Module.Action")]` policies built on the fly by a custom `IAuthorizationPolicyProvider`. Unknown permission keys produce no policy (fail-closed). Permissions are read from the database per request, not stored in claims, so revoking one applies immediately. `Manage` implies `View`. An associate's *type* never grants access. |
| Platform operator | Separate `PlatformSuperAdmin` claim policy for the cross-tenant console. |

Everything requires authentication by default (global `AuthorizeFilter`). Public endpoints such as
the business website, the booking link, public media and webhooks opt out explicitly. Hiding a menu
item is never treated as authorization: direct URLs return 403.

## Public surface

- **Public booking** (`/reservar/{slug}`) and **public website** (`/sitio/{slug}`) resolve the tenant
  from the slug and expose only data meant to be public. Availability queries and booking
  submissions have separate rate limits partitioned by IP and business. A one-time submission token
  prevents duplicate booking requests from double-submits.
- **Client IPs** used for abuse tracking are stored as SHA-256 hashes, not raw addresses.
- **Receipt links** use 256-bit random tokens (`RandomNumberGenerator`).
- **Webhooks:**
  - Meta WhatsApp: `X-Hub-Signature-256` HMAC over the raw body, compared in constant time.
  - Stripe: signature verification with the SDK.
  - Tilopay: access-token check in constant time, **plus** server-side verification of the payment
    with the provider API before any subscription is activated.
  - All providers: events are stored with a unique `(Provider, ProviderEventId)` index, which makes
    processing **idempotent**. Duplicates are acknowledged without side effects. Amount, plan or
    customer mismatches are rejected or routed to manual review; they are never auto-activated.
- **Uploads:** file-signature sniffing, size limits, and a cap on declared resolution that is read
  from the image header *before* decoding (decompression-bomb protection). Public images are
  re-encoded server-side to WebP and counted against per-tenant storage quotas.
- **CSRF:** antiforgery tokens on MVC forms, plus a header token for AJAX calls.
- **Security headers** (HSTS, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`,
  `Permissions-Policy`) outside Development.

## Auditability

- `PlatformAuditLog` is an append-only, cross-tenant audit trail for sensitive operations: manual
  billing actions, plan changes, provider cancellations, investor statement lifecycle events,
  permission changes, and conflicting schedule-rule activations.
- Financial documents (investor statements, receipts) are generated from **frozen snapshots** and
  never recalculated, so an issued document can't drift when historical data changes.
- Logs use `SensitiveDataMasker` to redact emails, phone numbers, tokens and query strings.

## Secrets and configuration

No credentials are committed. `appsettings.json` contains only non-sensitive defaults with empty
placeholders. Real values are provided per environment through environment variables
(`Section__Key`) or .NET user secrets in development. See
[local-development.md](local-development.md#configuration).

## Test coverage

`LuxuryApp.Tests/TenantIsolation` contains the tenant-isolation and security suites: cross-tenant
reads/writes through controllers and services, webhook tenant isolation, export isolation, session
security, rate-limit wiring, registration abuse and permission enforcement. The SQL Server-backed
tests (`Category=SqlServer`) exercise the real session-context interceptor and RLS migrations;
CI runs them against an ephemeral SQL Server container.
