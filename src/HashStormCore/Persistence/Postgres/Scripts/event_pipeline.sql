CREATE TABLE IF NOT EXISTS share_events
(
    event_id TEXT NOT NULL PRIMARY KEY,
    event_type TEXT NOT NULL,
    pool_id TEXT NOT NULL,
    coin_symbol TEXT NULL,
    coin_family TEXT NULL,
    miner TEXT NULL,
    worker TEXT NULL,
    source TEXT NULL,
    created TIMESTAMPTZ NOT NULL,
    block_height BIGINT NULL,
    difficulty DOUBLE PRECISION NOT NULL DEFAULT 0,
    network_difficulty DOUBLE PRECISION NOT NULL DEFAULT 0,
    share_multiplier DOUBLE PRECISION NOT NULL DEFAULT 1,
    is_block_candidate BOOLEAN NOT NULL DEFAULT FALSE,
    block_hash TEXT NULL,
    ip_address TEXT NULL,
    user_agent TEXT NULL,
    reject_reason TEXT NULL,
    error_code TEXT NULL,
    error_message TEXT NULL,
    transaction_confirmation_data TEXT NULL,
    block_reward NUMERIC NULL,
    block_type TEXT NULL,
    inserted_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_share_events_pool_created ON share_events(pool_id, created);
CREATE INDEX IF NOT EXISTS idx_share_events_pool_type_created ON share_events(pool_id, event_type, created);
CREATE INDEX IF NOT EXISTS idx_share_events_pool_miner_created ON share_events(pool_id, miner, created);

CREATE TABLE IF NOT EXISTS event_pipeline_processed_events
(
    event_id TEXT NOT NULL PRIMARY KEY,
    stream_name TEXT NOT NULL,
    stream_id TEXT NOT NULL,
    consumer_group TEXT NOT NULL,
    processed_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
