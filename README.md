# LedgerMatch

[![CI](https://github.com/Saudade01/ledgermatch/actions/workflows/ci.yml/badge.svg)](https://github.com/Saudade01/ledgermatch/actions/workflows/ci.yml)

A small .NET API that compares payment records from two systems and reports differences. A ledger might record `12500` kuruş for `INV-101`, while a provider records `12400`. This service keeps both records and reports `amount_mismatch`; it does not silently accept the difference.

This is a local portfolio prototype using synthetic data. [Türkçe açıklama](README.tr.md).

## Engineering focus

The example exercises backend behavior at a data boundary: rejecting invalid imports atomically, handling concurrent retries without duplicate records, and keeping a report traceable to its source records. PostgreSQL integration tests cover these behaviors; the HTTP demo makes them reproducible.

Inputs arrive through JSON or CSV. There is no live bank, ERP, or payment-provider connector yet, so this prototype does not demonstrate an end-to-end integration with an external provider.

## Run the example

Install Docker with Compose and Python 3, then run from the repository root:

```sh
docker compose up --build -d
python3 scripts/demo.py --base-url http://localhost:5087
```

The demo waits for readiness at `http://localhost:5087/health` for up to 30 attempts. The API uses port `5087`; local PostgreSQL uses `55439`. The Compose credentials are for local demonstration only.

The script loads [ledger.csv](samples/ledger.csv) through JSON and [provider.csv](samples/provider.csv) through CSV. It checks the result against [expected.json](samples/expected.json), retries imports, tries a conflicting import and an invalid amount, then verifies the saved report and CSV export. It exits nonzero on a mismatch. Repeat the command to check that retries reuse the same dataset and reconciliation IDs. Use `--key-prefix another-demo` to create a separate set.

## What the example contains

| Reference | Expected result |
| --- | --- |
| INV-100 | Equal TRY amounts: matched |
| INV-101 | 12500 versus 12400 kuruş: amount mismatch |
| INV-102 | Only in the ledger: missing right |
| INV-103 | Only at the provider: missing left |
| INV-104 | Two ledger records: duplicate, even though their sum equals the provider amount |
| INV-105 | Equal EUR amounts: matched |
| INV-106 | TRY on the left, EUR on the right: two separate missing groups |

There are 7 ledger records, 6 provider records and 8 result groups. Totals stay separate by currency; the difference is left minus right. These are fixture checks, not measurements from a production system.

## API

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/api/datasets` | Import JSON `{source, records}` with `Idempotency-Key` |
| POST | `/api/datasets/csv` | Import CSV with `X-Source`, `Idempotency-Key` and `Content-Type: text/csv` |
| GET | `/api/datasets/{id}` | Dataset metadata |
| POST | `/api/reconciliations` | Compare `{leftDatasetId, rightDatasetId}` |
| GET | `/api/reconciliations/{id}` | Read a saved result |
| GET | `/api/reconciliations/{id}/csv` | Export one row per original record |
| GET | `/health` | Database readiness |
| GET | `/openapi/v1.json` | API schema |

CSV columns are `sourceRecordId,reference,currency,amountMinor`. Amounts are nonnegative integers in minor units: `12500` means TRY 125.00 or EUR 125.00. Only TRY and EUR are supported. References are explicit, trimmed and case-sensitive; no name similarity or fuzzy matching is used.

Repeated references within the same currency produce a `duplicate` group before other matching rules. Different source record IDs do not make an ambiguous payment reference safe to match. Invalid imports are rejected as a whole. Datasets are immutable; retrying the same key with different content returns HTTP 409. JSON and CSV share the key namespace. Reusing the same ordered pair of dataset IDs returns the saved comparison.

## Tests

Install the .NET 10 SDK. Core tests do not require PostgreSQL:

```sh
dotnet test tests/Mutabakat.Core.Tests
```

API tests require the running local PostgreSQL instance and permission to create an isolated test database/schema:

```sh
RECON_TEST_DB='Host=localhost;Port=55439;Database=mutabakat;Username=mutabakat;Password=local-demo-only' dotnet test tests/Mutabakat.Api.Tests
```

## Scope and decisions

ASP.NET Core exposes the API; PostgreSQL and EF Core store immutable imports and comparisons. Matching logic is separate from HTTP and storage. [Design decisions](docs/decisions.md) explain reference matching, retries and duplicate handling.

No authentication or authorization is implemented. Run locally, not as a public service. The first version does not handle refunds, fees, currency conversion, partial payments, split transfers, settlement timing or manual resolution. A match means only that the supplied reference, currency and amount agree; it is not proof that a payment settled. Imports are limited to 10,000 records and 2 MiB per request.

## Try a change

Start with `INV-101` in `samples/provider.csv`: change its amount from `12400` to `12500`. Import with a new key prefix. Its expected status changes from `amount_mismatch` to `matched`, so the current demo expectation should fail until you deliberately update it. This is a small way to trace input → rule → stored report → test without adding a feature.

For local development, start only the database with `docker compose up -d db`, set `ConnectionStrings__Database` to the same local connection shown above, and run `dotnet run --project src/Mutabakat.Api --urls http://localhost:5087` (stop the Compose API first if it is running). Database migrations run on startup. To add a schema change, use `dotnet tool restore` and `dotnet ef migrations add <Name> --project src/Mutabakat.Api`.
