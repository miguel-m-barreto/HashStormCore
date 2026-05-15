\pset null '(null)'

SELECT
    version() AS postgresql_version,
    current_database() AS current_database,
    current_user AS current_user;

WITH required_tables(table_name) AS (
    VALUES
        ('shares'),
        ('blocks'),
        ('balances'),
        ('balance_changes'),
        ('miner_settings'),
        ('payments'),
        ('poolstats'),
        ('minerstats'),
        ('share_events'),
        ('event_pipeline_processed_events')
),
catalog_tables AS (
    SELECT
        c.oid,
        c.relname AS table_name,
        c.relkind,
        c.reltuples
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relkind IN ('r', 'p')
)
SELECT
    rt.table_name,
    ct.oid IS NOT NULL AS exists,
    CASE ct.relkind
        WHEN 'p' THEN 'partitioned'
        WHEN 'r' THEN 'ordinary'
        ELSE NULL
    END AS table_kind,
    CASE
        WHEN ct.oid IS NULL THEN NULL
        ELSE GREATEST(ct.reltuples::bigint, 0)
    END AS approximate_rows,
    CASE
        WHEN ct.oid IS NULL THEN NULL
        ELSE pg_size_pretty(pg_total_relation_size(ct.oid))
    END AS total_size
FROM required_tables rt
LEFT JOIN catalog_tables ct ON ct.table_name = rt.table_name
ORDER BY rt.table_name;

WITH shares_table AS (
    SELECT c.oid, c.relkind
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relname = 'shares'
      AND c.relkind IN ('r', 'p')
)
SELECT
    EXISTS (SELECT 1 FROM shares_table) AS shares_exists,
    EXISTS (SELECT 1 FROM shares_table WHERE relkind = 'p') AS shares_is_partitioned,
    (
        SELECT pg_get_partkeydef(oid)
        FROM shares_table
        WHERE relkind = 'p'
    ) AS partition_key,
    (
        SELECT count(*)
        FROM shares_table parent
        JOIN pg_inherits i ON i.inhparent = parent.oid
        WHERE parent.relkind = 'p'
    ) AS partition_count;

WITH shares_table AS (
    SELECT c.oid, c.relkind
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relname = 'shares'
      AND c.relkind IN ('r', 'p')
),
shares_status AS (
    SELECT
        EXISTS (SELECT 1 FROM shares_table) AS shares_exists,
        EXISTS (SELECT 1 FROM shares_table WHERE relkind = 'p') AS shares_is_partitioned,
        (
            SELECT count(*)
            FROM shares_table parent
            JOIN pg_inherits i ON i.inhparent = parent.oid
            WHERE parent.relkind = 'p'
        ) AS partition_count
)
SELECT CASE
    WHEN NOT shares_exists THEN 'shares_missing'
    WHEN NOT shares_is_partitioned THEN 'shares_not_partitioned'
    WHEN partition_count = 0 THEN 'shares_partitioned_no_child_partitions'
    ELSE 'shares_partitioned_with_child_partitions'
END AS shares_partition_health
FROM shares_status;

WITH shares_table AS (
    SELECT c.oid
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relname = 'shares'
      AND c.relkind = 'p'
),
partitions AS (
    SELECT
        'public.shares' AS parent_table,
        child_ns.nspname AS partition_schema,
        child.relname AS partition_name,
        pg_get_expr(child.relpartbound, child.oid) AS partition_bound,
        substring(pg_get_expr(child.relpartbound, child.oid) FROM $$FOR VALUES IN \('([^']*)'\)$$) AS pool_value,
        GREATEST(child.reltuples::bigint, 0) AS approximate_rows,
        pg_size_pretty(pg_total_relation_size(child.oid)) AS total_size
    FROM shares_table parent
    JOIN pg_inherits i ON i.inhparent = parent.oid
    JOIN pg_class child ON child.oid = i.inhrelid
    JOIN pg_namespace child_ns ON child_ns.oid = child.relnamespace
)
SELECT
    parent_table,
    partition_schema,
    partition_name,
    partition_bound,
    pool_value,
    approximate_rows,
    total_size
FROM partitions
ORDER BY partition_schema, partition_name;

SELECT
    to_regclass('public.share_events') IS NOT NULL AS share_events_exists,
    to_regclass('public.event_pipeline_processed_events') IS NOT NULL AS event_pipeline_processed_events_exists;

SELECT
    to_regclass('public.hashstorm_schema_migrations') IS NOT NULL AS migration_ledger_exists;

SELECT
    'SELECT migration_id, migration_type, filename, checksum_sha256, applied_at, applied_by, execution_seconds
     FROM public.hashstorm_schema_migrations
     ORDER BY applied_at DESC, migration_id DESC
     LIMIT 10;' AS last_applied_migrations_query
WHERE to_regclass('public.hashstorm_schema_migrations') IS NOT NULL
\gexec
