\set ON_ERROR_STOP on

\if :{?pool_id}
\else
\echo 'Required psql variable pool_id is missing. Use: -v pool_id=poolName'
\quit 1
\endif

SELECT set_config('hashstorm.add_share_partition.pool_id', :'pool_id', false);

DO $hashstorm$
DECLARE
    pool_id TEXT := current_setting('hashstorm.add_share_partition.pool_id');
    parent_oid OID;
    parent_relkind "char";
    is_list_poolid BOOLEAN;
    slug TEXT;
    hash8 TEXT;
    partition_name TEXT;
    existing_for_pool RECORD;
    generated_table RECORD;
BEGIN
    IF pool_id !~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$' THEN
        RAISE EXCEPTION 'Invalid pool_id "%". Expected pattern: [A-Za-z0-9][A-Za-z0-9_.-]{0,62}', pool_id;
    END IF;

    SELECT c.oid, c.relkind
    INTO parent_oid, parent_relkind
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relname = 'shares'
      AND c.relkind IN ('r', 'p');

    IF parent_oid IS NULL THEN
        RAISE EXCEPTION 'public.shares does not exist. Apply the HashStormCore schema before adding share partitions.';
    END IF;

    IF parent_relkind <> 'p' THEN
        RAISE EXCEPTION 'public.shares exists but is not partitioned. This tool does not convert or migrate existing non-partitioned shares data.';
    END IF;

    SELECT COALESCE(pt.partstrat = 'l' AND pg_get_partkeydef(parent_oid) = 'LIST (poolid)', false)
    INTO is_list_poolid
    FROM pg_partitioned_table pt
    WHERE pt.partrelid = parent_oid;

    IF is_list_poolid IS DISTINCT FROM TRUE THEN
        RAISE EXCEPTION 'public.shares must be partitioned by LIST (poolid). Current partition key: %', pg_get_partkeydef(parent_oid);
    END IF;

    slug := lower(pool_id);
    slug := regexp_replace(slug, '[^a-z0-9]+', '_', 'g');
    slug := regexp_replace(slug, '^_+|_+$', '', 'g');

    IF slug = '' THEN
        slug := 'pool';
    END IF;

    hash8 := substring(md5(pool_id) FROM 1 FOR 8);
    partition_name := format('shares_p_%s_%s', left(slug, 45), hash8);

    SELECT child_ns.nspname AS schema_name, child.relname AS table_name
    INTO existing_for_pool
    FROM pg_inherits i
    JOIN pg_class parent ON parent.oid = i.inhparent
    JOIN pg_class child ON child.oid = i.inhrelid
    JOIN pg_namespace child_ns ON child_ns.oid = child.relnamespace
    WHERE parent.oid = parent_oid
      AND pg_get_expr(child.relpartbound, child.oid) = format('FOR VALUES IN (%L)', pool_id)
    LIMIT 1;

    IF FOUND THEN
        IF existing_for_pool.schema_name = 'public' AND existing_for_pool.table_name = partition_name THEN
            RAISE NOTICE 'Share partition %.% already exists for pool "%".', existing_for_pool.schema_name, existing_for_pool.table_name, pool_id;
            RETURN;
        END IF;

        RAISE EXCEPTION 'A share partition for pool "%" already exists as %.%. Refusing to create duplicate partition %.',
            pool_id,
            existing_for_pool.schema_name,
            existing_for_pool.table_name,
            partition_name;
    END IF;

    SELECT c.relkind, EXISTS (
        SELECT 1
        FROM pg_inherits i
        WHERE i.inhparent = parent_oid
          AND i.inhrelid = c.oid
    ) AS attached_to_shares
    INTO generated_table
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relname = partition_name;

    IF FOUND THEN
        RAISE EXCEPTION 'public.% already exists but is not the expected partition for pool "%". Refusing to reuse it.', partition_name, pool_id;
    END IF;

    EXECUTE format(
        'CREATE TABLE %I.%I PARTITION OF public.shares FOR VALUES IN (%L)',
        'public',
        partition_name,
        pool_id
    );

    RAISE NOTICE 'Created share partition public.% for pool "%".', partition_name, pool_id;
END
$hashstorm$;
