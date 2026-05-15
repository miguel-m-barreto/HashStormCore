\pset null '(null)'

SELECT 'Current schema indexes only; future ApiProvider endpoint indexes are not part of this batch.' AS note;

WITH expected_indexes(expected_index) AS (
    VALUES
        ('IDX_SHARES_POOL_MINER'),
        ('IDX_SHARES_POOL_CREATED'),
        ('IDX_SHARES_POOL_MINER_DIFFICULTY'),
        ('IDX_BLOCKS_POOL_BLOCK_STATUS'),
        ('IDX_BLOCKS_POOL_BLOCK_TYPE'),
        ('IDX_BALANCE_CHANGES_POOL_ADDRESS_CREATED'),
        ('IDX_BALANCE_CHANGES_POOL_TAGS'),
        ('IDX_PAYMENTS_POOL_COIN_WALLET'),
        ('IDX_POOLSTATS_POOL_CREATED'),
        ('IDX_MINERSTATS_POOL_CREATED'),
        ('IDX_MINERSTATS_POOL_MINER_CREATED'),
        ('IDX_MINERSTATS_POOL_MINER_WORKER_CREATED_HASHRATE'),
        ('idx_share_events_pool_created'),
        ('idx_share_events_pool_type_created'),
        ('idx_share_events_pool_miner_created')
),
catalog_indexes AS (
    SELECT c.relname AS index_name
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relkind IN ('i', 'I')
)
SELECT
    e.expected_index,
    ci.index_name IS NOT NULL AS present,
    ci.index_name AS actual_index_name
FROM expected_indexes e
LEFT JOIN catalog_indexes ci ON lower(ci.index_name) = lower(e.expected_index)
ORDER BY e.expected_index;
