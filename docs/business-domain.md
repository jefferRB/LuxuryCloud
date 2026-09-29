# Business domain

LuxuryCloud serves appointment-based businesses: beauty salons, barbershops, nail studios, spas and
independent professionals. The first market is Costa Rica, so the UI is in Spanish, amounts are in
colones (CRC), and the tax rules follow Costa Rican VAT (IVA, 13% by default and configurable per
tenant).

This document describes the domain rules that shape the code. The intent is to show *why* the
services look the way they do.

## Glossary

Domain identifiers in the code are in Spanish. The ones you will run into most:

| Spanish | English | Notes |
| --- | --- | --- |
| `Cita` | Appointment | Calendar entry for a staff member and a customer. |
| `Cliente` | Customer | The business's end customer. |
| `Funcionario` / colaborador | Staff member | Has a schedule, produces revenue and earns commission. |
| `Puesto` | Job role | |
| `Servicio`, `Producto` | Service, product | Sellable catalog; products have inventory. |
| `Cobro` | Charge / payment received | A completed sale (services and products) with a payment method. |
| `Egreso` | Expense | Categorized business expense. |
| `Liquidación (semanal)` | (Weekly) payroll settlement | What each staff member is owed for a period. |
| `Comprobante` | Receipt | Internal, non-fiscal receipt (PDF / email / public link). |
| `Reserva`, `SolicitudReserva` | Booking, booking request | Created from the public booking link. |
| `Asociado` | Associate | Investor, partner, marketing or accounting collaborator. |
| `Inversionista` | Investor | Profit-sharing participant. |
| `Suscripción`, `Plan` | Subscription, plan | The tenant's LuxuryCloud subscription. |
| `Bloqueo recurrente` | Recurring schedule block | E.g. lunch break Mon–Sat 1–2 pm. |
| `SINPE` | Costa Rican instant bank transfer | Reported next to cash and card. |

## Modules

| Area | What it covers |
| --- | --- |
| **Calendar & appointments** | Day/week calendar per staff member, drag-and-drop move/resize, status tracking, conflict detection, recurring schedule blocks and breaks. |
| **Online booking** | Public booking page per business with a QR code, service catalog, real-time availability and booking requests that the business accepts or rejects. |
| **Customers (CRM)** | Customer records, identity resolution by name and phone, visit history, visit-frequency metrics, service notes, birthdays. |
| **Staff** | Staff profiles and roles, commission settings, tax relationship, photos, and a staff self-service portal ("Mi Portal"). |
| **Sales & payments** | Charges for services and products, payment method breakdown (cash / card / SINPE), receipts. |
| **Expenses** | Categorized expenses with system categories that the financial formulas depend on. |
| **Inventory** | Products, stock movements, product sales inside charges. |
| **Payroll** | Weekly settlements per staff member, derived from the tax engine, with Excel export. |
| **Dashboards & reports** | Financial dashboard, analytics, and a monthly executive summary emailed to owners. |
| **WhatsApp automation** | Appointment confirmations, reminders and cancellations via Meta templates; inbound replies; inbox; opt-in and consent. |
| **Public website** | Per-tenant landing page (gallery, services, team, location, WhatsApp/Maps/Waze links) with daily traffic metrics. |
| **Associates & investors** | Granular permissions for non-staff collaborators; profit distribution, statements and payouts for investors. |
| **Subscriptions & billing** | Plans by staff count (monthly/annual), WhatsApp add-ons with message quotas, plan changes, failed-payment recovery. |
| **Platform console** | Cross-tenant operations: tenant profiles, users, billing health, payment recovery, provider reconciliation, audit log, worker heartbeats. |

## Financial engine

### One tax engine, applied per line

`TaxCalculationService` is the only place where VAT and commissions are computed. It is stateless,
registered as a singleton, and tested against concrete business examples.

- VAT is computed **per line**, never as `total / 1.13` on a monthly total. That shortcut gives
  wrong numbers as soon as a sale mixes taxed and exempt items.
- Commission can be calculated on the **total charged** or on the **base without VAT**.
- Staff tax relationships are modeled explicitly: `Empleado` (payroll employee), `Independiente`
  (independent professional who may invoice VAT on their commission) and `AlquilerSilla`
  (chair rental). Independents have three VAT modes: *no VAT*, *VAT included* and *VAT added*.
- Rounding is centralized in `FiscalMath`: 2 decimals, **half-even (banker's rounding)**. This is
  deliberate: it reproduces the clean totals the business expects. Half-up leaves a one-colón
  difference in split-commission scenarios.

### Snapshots over recalculation

A charge (`Cobro`) stores a **snapshot** of the commission rates and tax configuration that were in
force when it happened. If a staff member's commission changes from 50% to 55% tomorrow, past
charges are still settled at 50%. The catalog describes the future; the snapshot describes history.

The same idea applies to investor statements and receipts: once finalized, documents are rendered
from their frozen snapshot and never recalculated.

### One profit formula

`PeriodProfitCalculationService` is the **single source of truth** for profit over a date range:

```
TotalCharged − VATCharged              = NetRevenue
NetRevenue − OperatingExpenses
           − TeamSettlements
           + AuthorizedAdjustments     = DistributableProfit
```

Same tenant, same range and same data always give the same result, whoever asks: the financial
dashboard (calendar month), investor statements (contractual period), the associate profit KPI and
the monthly executive report all call it. A shared test suite checks this invariant.

- Revenue, VAT and settlements come from the weekly settlement service, which uses the tax engine.
- Staff payroll payments and investor distributions are **structurally excluded** from operating
  expenses. They are identified by system category codes, not by display name. Otherwise payroll
  would be counted twice, and paying an investor would reduce their own share.
- The dashboard's *cash* view ("money in / money out") is kept separate from the *profit* view on
  purpose.

### Payroll settlements

Weekly settlements are generated per staff member from the tax engine's per-line results. A unique
`(TenantId, IdempotencyKey)` index ensures that a retried or double-clicked generation creates
exactly one settlement.

## Investors and associates

An **associate** is someone related to the business who is not operational staff. Five concerns are
modeled separately and never mixed:

1. who the person is → `Associate`;
2. what their relationship is → `AssociateTypeAssignment` (a person can have several types);
3. whether they can sign in → optional Identity account with the `Asociado` role;
4. what they can do → `AssociatePermission` (granular `Module.Action` permissions);
5. whether they share profit → `TenantInvestor` + versioned `InvestorAgreement`.

**The type never authorizes.** There is no `if (type == Marketing) allowWebsite = true`; access
comes only from explicit permissions.

Investor rules:

- Share = `DistributableProfit × agreed %`, with carry-forward of previous losses when configured.
- Overlapping active agreements can't add up to more than 100%. A percentage change takes effect at
  the **start** of a period, and the old agreement is closed and versioned, never edited.
- Agreements can settle on the calendar month or on a **cut-off day** (e.g. the 20th → periods run
  from the 21st to the 20th). All period arithmetic lives in one pure resolver
  (`InvestorSettlementPeriodResolver`), which handles short months (a cut-off on the 31st closes on
  the last day of February).
- Statement lifecycle: `Draft → Finalized → Sent → PartiallyPaid → Paid` (plus `Voided`). Only drafts
  are recalculated. Finalization runs in a `Serializable` transaction with a re-read to prevent
  double finalization. Generation is idempotent per *(tenant, investor, period)* through a filtered
  unique index.
- Payments can't exceed the outstanding balance. Corrections are compensating entries with a
  reason, never deletions.
- Every step is written to the audit log.

## Scheduling and availability

- **One availability service.** `IFuncionarioAvailabilityService` combines appointments, breaks and
  recurring blocks. Both the internal calendar and the public booking flow use it, so there is no
  second overlap check anywhere else.
- **Recurring blocks are rules, not rows.** A block such as "lunch, Mon–Sat, 1–2 pm" is stored once
  and expanded on the fly by a pure occurrence calculator. "All staff" scope is dynamic, so new staff
  are covered automatically. Editing an active rule closes the old version and creates a new linked
  one. Per-date exceptions never modify the rule itself.
- Creating a rule that conflicts with existing appointments **never moves or cancels** those
  appointments. It only blocks new bookings, and activating it with conflicts requires
  confirmation and is audited.
- Times are stored as **business-local time** (time zone configurable, default `America/Costa_Rica`).

## Online booking

- Each business gets a public booking link and QR code. Customers pick a service, a staff member
  (or "any") and a time slot computed by the availability service. Weekly business hours are
  configurable.
- A submitted request **holds capacity while it is pending**, which prevents two customers from
  requesting the same slot. For that reason, submissions have their own stricter rate limit.
- Accepting a request resolves the customer through the same identity service the calendar uses
  (name + normalized phone), so a returning customer isn't duplicated.

## WhatsApp automation

WhatsApp is a paid **add-on**. Tenants without it see no WhatsApp UI at all; the regular calendar
works the same either way.

- **Confirmation timing:** confirmations are scheduled for 24 hours before the appointment, or sent
  immediately when the appointment is less than 24 hours away. That way, creating many future
  recurring appointments doesn't flood the customer with messages.
- **Reminders** go out a configurable lead time before the appointment (default 3 hours).
- **Cancellations** and booking rejections send their own templates.
- Customer **consent/opt-in** is tracked, and each add-on tier has monthly and daily message quotas.
- Inbound replies update appointment state and can trigger an auto-reply. A shared inbox shows
  conversations.
- Sending is idempotent: a confirmation that was already sent is never sent again, even when it is
  triggered manually.

## Subscriptions and billing

- Plans are priced by the number of active staff members, monthly or annually, with WhatsApp
  add-ons on top. Plan limits (e.g. maximum staff) are enforced server-side.
- Payments use **Tilopay hosted recurring links**. Activation happens **only** from a verified
  provider webhook (or an audited manual approval), never from the browser return URL.
- Plan changes are modeled as a `PlanChangeIntent`. A filtered unique index allows only one pending
  change per tenant. After an upgrade, the old provider subscription is cancelled by a retrying
  worker, so the tenant is never left with two active recurring charges.
- A failed payment opens a **payment incident**, followed by a grace period, notifications and an
  optional suspension. Suspension ships in dry-run mode until it is explicitly enabled.
- A daily **reconciliation** compares local state with the provider's subscriber records and fixes
  known kinds of drift (late subscriber IDs, expiry dates that moved, duplicate add-ons). Anything
  it can't prove, it raises as an alert instead of changing.
