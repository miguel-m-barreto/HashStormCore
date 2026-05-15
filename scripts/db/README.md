# HashStormCore Database Operations

This directory contains the safe PostgreSQL operations surface for HashStormCore.

## PostgreSQL Baseline

HashStormCore requires PostgreSQL 16 or newer. PostgreSQL 18 is recommended for new deployments. PostgreSQL 17 is acceptable where managed-provider or package maturity matters.

The default database is `hashstorm`, the default application user is `hashstorm`, and the default PostgreSQL port is `5432`. Override the database and user with `HASHSTORM_DB_NAME` and `HASHSTORM_DB_USER`. Override the port with the normal `PGPORT` environment variable.

## Setup Flow

Create the application role and database with admin PostgreSQL credentials:

```bash
HASHSTORM_DB_PASSWORD='choose-a-password' bash scripts/db/create-database.sh
```

`create-database.sh` uses normal psql connection environment variables such as `PGHOST`, `PGPORT`, `PGUSER`, and `PGPASSWORD`. It creates or updates the application role, creates the database if missing, grants the application user access, checks PostgreSQL `server_version_num >= 160000`, and does not apply schema.

Apply schema with the application user:

```bash
PGPASSWORD='the-application-password' bash scripts/db/apply-schema.sh
```

`apply-schema.sh` connects as `HASHSTORM_DB_USER` to `HASHSTORM_DB_NAME`, checks PostgreSQL `server_version_num >= 160000`, and applies:

- `src/HashStormCore/Persistence/Postgres/Scripts/createdb.sql`
- `src/HashStormCore/Persistence/Postgres/Scripts/event_pipeline.sql`

`createdb.sql` is currently DB-empty-only. `apply-schema.sh` refuses to run when core HashStormCore tables already exist unless `HASHSTORM_APPLY_SCHEMA_ALLOW_EXISTING=1` is set.

Apply schema migrations after the base schema:

```bash
PGPASSWORD='the-application-password' bash scripts/db/apply-migrations.sh
```

`apply-migrations.sh` creates the migration ledger table `public.hashstorm_schema_migrations` as internal bootstrap if missing, checks PostgreSQL `server_version_num >= 160000`, calculates SHA-256 checksums, and applies migrations from:

```text
src/HashStormCore/Persistence/Postgres/Scripts/migrations
```

Migration files are sorted lexicographically and must use this naming convention:

```text
NNN_category_description.tx.sql
NNN_category_description.ntx.sql
```

The ledger table is not tracked as a normal migration. Future schema and index migrations should start at `010_...` or later.

Use `.tx.sql` for migrations that should run inside an explicit transaction. Use `.ntx.sql` for migrations that must run outside an explicit transaction. PostgreSQL `CREATE INDEX CONCURRENTLY` must use `.ntx.sql`.

Non-transactional migrations are recorded in the ledger only after their SQL executes successfully, so every `.ntx.sql` migration must be restart-safe/idempotent. Future concurrent index migrations should use `CREATE INDEX CONCURRENTLY IF NOT EXISTS`.

Migration files are immutable once applied. If an applied migration's checksum changes, the runner fails hard instead of reapplying it.

The migration runner takes a fixed HashStormCore PostgreSQL advisory lock for the whole run. A second runner waits until the first one exits, which prevents races between migration status checks, migration DDL, and ledger recording. The lock is held by a dedicated PostgreSQL session and is released by disconnecting that session on normal exit or interruption.

Apply only index-category migrations through the operator entrypoint:

```bash
PGPASSWORD='the-application-password' bash scripts/db/apply-indexes.sh
```

`apply-indexes.sh` is a thin wrapper over `apply-migrations.sh` restricted to `category=index`. No ApiProvider historical endpoint index migrations are included yet; those are planned for the next DB batch.

## Read-Only Checks

Status:

```bash
psql -v ON_ERROR_STOP=1 -U hashstorm -d hashstorm -f scripts/db/status.sql
```

Index presence:

```bash
psql -v ON_ERROR_STOP=1 -U hashstorm -d hashstorm -f scripts/db/check-indexes.sql
```

Share partition listing:

```bash
psql -v ON_ERROR_STOP=1 -U hashstorm -d hashstorm -f scripts/db/list-share-partitions.sql
```

The partition listing handles the current non-partitioned `shares` table gracefully. Share partition creation tooling is intentionally deferred to a later batch.

## Legacy Scripts

`createdb_postgresql_11_appendix.sql` is legacy and destructive. It is not operator-safe and is not used by the scripts in this directory.

`cleandb.sql` is legacy, destructive, and incomplete. It is not used by the scripts in this directory.

PostgreSQL 10/11 setup language and assumptions are not the HashStormCore operations baseline.

## Runtime Data Ownership

Postgres remains canonical for accounting and history, including blocks, payouts/payments, balances, block confirmations, miner history, long-term stats, and historical event data.

Redis live read models are for hot dashboard state. Redis Streams and broker details are internal transport, not public API.

Long term, frontend and operator traffic should be served by ApiProvider using Redis/Postgres read models, not by Pool Core.
