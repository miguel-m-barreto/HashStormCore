# HashStormCore Database Operations

This directory contains the safe first-batch PostgreSQL operations surface for HashStormCore.

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
