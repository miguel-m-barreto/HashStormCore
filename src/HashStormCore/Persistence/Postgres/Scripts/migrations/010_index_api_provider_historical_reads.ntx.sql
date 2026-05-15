-- ApiProvider historical-read indexes.
-- Nontransactional by design: CREATE INDEX CONCURRENTLY cannot run inside BEGIN/COMMIT.
-- Restart-safe by design: every index uses IF NOT EXISTS.

-- Blocks: pool block history, status-filtered block history, and miner block history.
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_blocks_pool_created_desc
    ON blocks(poolid, created DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_blocks_pool_status_created_desc
    ON blocks(poolid, status, created DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_blocks_pool_miner_created_desc
    ON blocks(poolid, miner, created DESC);

-- Payments: pool payout history and miner/address payout history.
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_payments_pool_created_desc
    ON payments(poolid, created DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_payments_pool_address_created_desc
    ON payments(poolid, address, created DESC);

-- Balance changes: pool-wide accounting history.
-- Existing IDX_BALANCE_CHANGES_POOL_ADDRESS_CREATED already covers address history.
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_balance_changes_pool_created_desc
    ON balance_changes(poolid, created DESC);

-- Share events: stable chronological pagination, type history, and miner/worker event history.
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_share_events_pool_created_desc_event_id
    ON share_events(pool_id, created DESC, event_id);

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_share_events_pool_type_created_desc_event_id
    ON share_events(pool_id, event_type, created DESC, event_id);

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_api_share_events_pool_miner_worker_created_desc_event_id
    ON share_events(pool_id, miner, worker, created DESC, event_id);
