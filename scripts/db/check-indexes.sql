\pset null '(null)'

SELECT 'Current schema indexes and ApiProvider historical-read indexes.' AS note;

WITH expected_indexes(index_scope, expected_index) AS (
    VALUES
        ('base', 'IDX_SHARES_POOL_MINER'),
        ('base', 'IDX_SHARES_POOL_CREATED'),
        ('base', 'IDX_SHARES_POOL_MINER_DIFFICULTY'),
        ('base', 'IDX_BLOCKS_POOL_BLOCK_STATUS'),
        ('base', 'IDX_BLOCKS_POOL_BLOCK_TYPE'),
        ('base', 'IDX_BALANCE_CHANGES_POOL_ADDRESS_CREATED'),
        ('base', 'IDX_BALANCE_CHANGES_POOL_TAGS'),
        ('base', 'IDX_PAYMENTS_POOL_COIN_WALLET'),
        ('base', 'IDX_POOLSTATS_POOL_CREATED'),
        ('base', 'IDX_MINERSTATS_POOL_CREATED'),
        ('base', 'IDX_MINERSTATS_POOL_MINER_CREATED'),
        ('base', 'IDX_MINERSTATS_POOL_MINER_WORKER_CREATED_HASHRATE'),
        ('base', 'idx_share_events_pool_created'),
        ('base', 'idx_share_events_pool_type_created'),
        ('base', 'idx_share_events_pool_miner_created'),
        ('api_provider', 'idx_api_blocks_pool_created_desc'),
        ('api_provider', 'idx_api_blocks_pool_status_created_desc'),
        ('api_provider', 'idx_api_blocks_pool_miner_created_desc'),
        ('api_provider', 'idx_api_payments_pool_created_desc'),
        ('api_provider', 'idx_api_payments_pool_address_created_desc'),
        ('api_provider', 'idx_api_balance_changes_pool_created_desc'),
        ('api_provider', 'idx_api_share_events_pool_created_desc_event_id'),
        ('api_provider', 'idx_api_share_events_pool_type_created_desc_event_id'),
        ('api_provider', 'idx_api_share_events_pool_miner_worker_created_desc_event_id')
),
catalog_indexes AS (
    SELECT c.relname AS index_name
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relkind IN ('i', 'I')
)
SELECT
    e.index_scope,
    e.expected_index,
    ci.index_name IS NOT NULL AS present,
    ci.index_name AS actual_index_name
FROM expected_indexes e
LEFT JOIN catalog_indexes ci ON lower(ci.index_name) = lower(e.expected_index)
ORDER BY e.index_scope, e.expected_index;
