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

`createdb.sql` is DB-empty-only. Fresh installs create `public.shares` as a `LIST (poolid)` partitioned parent with no default partition. `apply-schema.sh` refuses to run when core HashStormCore tables already exist unless `HASHSTORM_APPLY_SCHEMA_ALLOW_EXISTING=1` is set.

After applying schema, create one shares partition for each configured pool before starting DbWriter or Pool Core writes:

```bash
PGPASSWORD='the-application-password' bash scripts/db/add-share-partition.sh btcz_solo
PGPASSWORD='the-application-password' bash scripts/db/check-missing-share-partitions.sh configs/config.json
```

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

`apply-indexes.sh` is a thin wrapper over `apply-migrations.sh` restricted to `category=index`.

The first ApiProvider historical-read index migration is `010_index_api_provider_historical_reads.ntx.sql`. It is non-destructive, uses `CREATE INDEX CONCURRENTLY IF NOT EXISTS`, and is applied through `apply-indexes.sh` or the full migration runner. The `share_events` ApiProvider indexes include `event_id` where useful for stable pagination.

## Development Reset

`scripts/db/dev-reset.sh` is destructive and intended only for local development databases. It performs a schema-level reset:

```sql
DROP SCHEMA IF EXISTS public CASCADE;
CREATE SCHEMA public AUTHORIZATION <HASHSTORM_DB_USER>;
```

The reset script refuses to run unless this explicit confirmation is present:

```bash
HASHSTORM_DEV_RESET=I_UNDERSTAND_THIS_DELETES_DATA bash scripts/db/dev-reset.sh
```

It refuses dangerous database names such as `postgres`, `template0`, `template1`, `production`, `prod`, `main`, and `default`. It also refuses remote-looking `PGHOST` or `PGHOSTADDR` values unless `HASHSTORM_DEV_RESET_ALLOW_REMOTE=YES_I_UNDERSTAND` is set. If `PGSERVICE` is set, the script also requires that override because the service file can hide the actual host. Do not use this for production.

The reset does not apply schema automatically, does not reset Redis, does not reset filesystem outbox/WAL data, does not touch Docker, and does not recreate the database or role.

After a development reset, run the normal setup sequence:

```bash
bash scripts/db/apply-schema.sh
bash scripts/db/apply-migrations.sh
bash scripts/db/apply-indexes.sh
bash scripts/db/add-share-partition.sh <poolId>
bash scripts/db/check-missing-share-partitions.sh <config.json>
```

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

The partition listing handles the current non-partitioned `shares` table gracefully.

## Share Partitions

`scripts/db/add-share-partition.sh` and `scripts/db/check-missing-share-partitions.sh` manage pool partitions for installs where `public.shares` is already partitioned by `LIST (poolid)`. Fresh installs created through `apply-schema.sh` use this shape.

These tools do not convert an existing non-partitioned `shares` table and do not migrate existing share data. Migrating an existing non-partitioned database is a separate future task.

Safe new-pool workflow:

1. Add the pool to config while it is disabled, or before starting Pool Core/DbWriter for that pool.
2. Ensure the database and schema already exist.
3. Add the partition:

```bash
PGPASSWORD='the-application-password' bash scripts/db/add-share-partition.sh btcz_solo
```

4. Check configured pools against database partitions:

```bash
PGPASSWORD='the-application-password' bash scripts/db/check-missing-share-partitions.sh configs/config.json
```

5. Start or enable DbWriter and Pool Core for the pool.

Pool IDs must match `[A-Za-z0-9][A-Za-z0-9_.-]{0,62}`. The partition stores the exact pool ID value in `FOR VALUES IN (...)`. The generated table name is deterministic: `shares_p_<slug>_<hash8>`, where the slug is lowercased and sanitized, and `hash8` is derived from the exact pool ID. The tooling expects one pool value per shares partition.

There is intentionally no default or catch-all `shares` partition. Missing pool partitions should fail writes clearly instead of silently routing shares into an ambiguous table.

The add-partition script does not create partition-local indexes manually. PostgreSQL creates matching child indexes when a new partition is added to a partitioned table that already has partitioned parent indexes.

## Legacy Scripts

`createdb_postgresql_11_appendix.sql` is legacy and destructive. It is not operator-safe and is not used by the scripts in this directory.

`cleandb.sql` is legacy, destructive, and incomplete. It is not used by the scripts in this directory.

PostgreSQL 10/11 setup language and assumptions are not the HashStormCore operations baseline.

## Runtime Data Ownership

Postgres remains canonical for accounting and history, including blocks, payouts/payments, balances, block confirmations, miner history, long-term stats, and historical event data.

Redis live read models are for hot dashboard state. Redis Streams and broker details are internal transport, not public API.

Long term, frontend and operator traffic should be served by ApiProvider using Redis/Postgres read models, not by Pool Core.
