# HashStormCore SQL Migrations

Migration files in this directory are applied by `scripts/db/apply-migrations.sh`.

Use lexicographic numeric prefixes and transaction markers:

- `NNN_category_description.tx.sql` runs inside a transaction.
- `NNN_category_description.ntx.sql` runs outside an explicit transaction.

`public.hashstorm_schema_migrations` is internal runner bootstrap, not a normal migration file. Start real migrations at `010_...` or later.

Use `category=index` for future index migrations. Migrations that use `CREATE INDEX CONCURRENTLY` must use the `.ntx.sql` suffix because PostgreSQL does not allow concurrent index creation inside an explicit transaction.

Non-transactional migrations are recorded only after successful SQL execution, so every `.ntx.sql` migration must be restart-safe/idempotent. Future concurrent index migrations should use `CREATE INDEX CONCURRENTLY IF NOT EXISTS`.

Migration files are immutable once applied. The runner stores a SHA-256 checksum in `public.hashstorm_schema_migrations` and fails if an already-applied migration changes.

The runner holds a fixed HashStormCore PostgreSQL advisory lock for the whole migration run, so only one runner should apply or record migrations at a time. Transactional migrations are still wrapped in `BEGIN`/`COMMIT`; non-transactional migrations remain outside an explicit transaction.
