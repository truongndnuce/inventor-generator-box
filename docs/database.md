# Database (PostgreSQL)

Jobs, result files (the list) and the event log live in PostgreSQL. `JobStore` (`be/Cadflow.Api/JobStore.cs`) is the only class that touches the DB, using Npgsql with plain SQL.

## Where things are stored

| Data | Where |
|---|---|
| Jobs, artifact list, events | **PostgreSQL** (`jobs`, `job_artifacts`, `job_events`) |
| Worker state (heartbeat, Busy) | The backend memory, refreshed by every heartbeat, **not in the DB** |
| Result files | R2 or `data/artifacts/<jobId>/` |
| Imported `.gea` scripts, product configurations | Files in `data/scripts`, `data/config` |
| Chat drafts (`DraftStore`) | Memory, expire after 30 minutes |

## Schema

The tables are created with `CREATE TABLE IF NOT EXISTS` when the backend starts (with a `pg_advisory_lock`, so two backends starting at the same time do not race).

- **`jobs`**: `id` (PK, `AJ-yyyyMMdd-HHmmss-xxxx`), `product_code`, `product_version`, `status` (`queued|running|succeeded|failed|cancelled`), `step`, `error`, `requested_by`, `created_at`, `started_at`, `finished_at`, `last_heartbeat`, `worker`, `attempts`, `inputs`/`text_inputs`/`summary` (jsonb), `script_name`, `script_sha256`, `script_source`, `artifacts_purged_at`. Indexes: `(status, created_at)` for the queue, `requested_by`, `product_code`.
- **`job_artifacts`**: PK `(job_id, name)`, `kind`, `size`, `r2_key` (null = the file is on the server disk). Deleted together with the job (`ON DELETE CASCADE`).
- **`job_events`**: `id` (bigserial), `job_id`, `time`, `message`. Append-only. Index `(job_id, time)`.

Times are `timestamptz` (UTC). `inputs`, `text_inputs` and `summary` are always read/written as a whole block, so they are jsonb, not separate tables.

## The queue

`Claim` is one `UPDATE ... WHERE id = (SELECT ... FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING ...` statement, so several workers or several backends sharing the DB never take the same job. The worker operations (`progress`, `complete`, `fail`, artifact) lock the job row and accept only the worker that holds the job. `ReapStale` (every 10 seconds) returns Running jobs with no heartbeat for 90 seconds to the queue, or marks them `failed` when `MaxAttempts` (3) is used up.

## Configuration

`ConnectionStrings:Cadflow` in `be/Cadflow.Api/appsettings.json`, default `Host=localhost;Database=cadflow;Username=admin` (no password, fine for a local Postgres). When you need a password or another server, set it in `.env` (ignored by git): `ConnectionStrings__Cadflow=Host=...;Database=cadflow;Username=...;Password=...`.

```bash
brew services start postgresql@15   # macOS
createdb cadflow
```

If the connection string is missing or the DB cannot be reached, the backend does not start.

## Migrating from jobs.json

On the first run, if `data/jobs.json` still exists, the backend imports every job into the DB (skipping ids that already exist) and renames it to `jobs.json.migrated`. Nothing else is needed.

## Test

`be/Cadflow.Api.Tests` (xUnit) runs on a real PostgreSQL. Each test creates its own DB `cadflow_test_xxxx` and drops it when done.

```bash
dotnet test be/Cadflow.Api.Tests
# another server: CADFLOW_TEST_PG="Host=...;Username=...;Password=..." dotnet test ...
```

Covers: `JobStore` (durable storage, parallel `Claim` without duplicates, worker ownership, retry, cancel, reap, heartbeat, artifacts and retention, `jobs.json` import) and `CadTools` (validate, propose/confirm exactly once, create job, get/list/cancel).
