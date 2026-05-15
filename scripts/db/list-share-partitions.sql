\pset null '(null)'

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
        EXISTS (SELECT 1 FROM shares_table WHERE relkind = 'p') AS shares_is_partitioned
)
SELECT
    shares_exists,
    shares_is_partitioned
FROM shares_status;

WITH shares_table AS (
    SELECT c.oid, c.relkind
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relname = 'shares'
      AND c.relkind IN ('r', 'p')
),
partitions AS (
    SELECT
        child_ns.nspname AS partition_schema,
        child.relname AS partition_name,
        pg_get_expr(child.relpartbound, child.oid) AS partition_bound,
        GREATEST(child.reltuples::bigint, 0) AS approximate_rows,
        pg_size_pretty(pg_total_relation_size(child.oid)) AS total_size
    FROM shares_table parent
    JOIN pg_inherits i ON i.inhparent = parent.oid
    JOIN pg_class child ON child.oid = i.inhrelid
    JOIN pg_namespace child_ns ON child_ns.oid = child.relnamespace
    WHERE parent.relkind = 'p'
)
SELECT
    partition_schema,
    partition_name,
    partition_bound,
    approximate_rows,
    total_size
FROM partitions
ORDER BY partition_schema, partition_name;
