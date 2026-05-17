-- Durable payout intent/reservation schema foundation.
-- Transactional migration: no CONCURRENTLY indexes and no runtime behavior change.

CREATE TABLE payout_batches
(
    id BIGSERIAL NOT NULL PRIMARY KEY,
    poolid TEXT NOT NULL,
    coin TEXT NOT NULL,
    coinfamily TEXT NOT NULL,
    handler TEXT NOT NULL,
    state TEXT NOT NULL,
    sendshape TEXT NOT NULL,
    recipientsethash TEXT NOT NULL,
    minimumamount decimal(28,12) NOT NULL,
    reservedamountsnapshot decimal(28,12) NOT NULL,
    intentcountsnapshot INT NOT NULL,
    externaloperationid TEXT NULL,
    transactionconfirmationdata TEXT NULL,
    errorcode TEXT NULL,
    errormessage TEXT NULL,
    created TIMESTAMPTZ NOT NULL,
    updated TIMESTAMPTZ NOT NULL,
    submitted TIMESTAMPTZ NULL,
    settled TIMESTAMPTZ NULL,
    reviewed TIMESTAMPTZ NULL,

    CONSTRAINT chk_payout_batches_state CHECK (state IN ('reserved', 'sending', 'submitted', 'settled', 'failed', 'ambiguous_requires_review', 'cancelled')),
    CONSTRAINT chk_payout_batches_sendshape CHECK (sendshape IN ('batch_multi_recipient', 'per_address', 'address_group', 'async_operation')),
    CONSTRAINT chk_payout_batches_poolid CHECK (btrim(poolid) <> ''),
    CONSTRAINT chk_payout_batches_coin CHECK (btrim(coin) <> ''),
    CONSTRAINT chk_payout_batches_coinfamily CHECK (btrim(coinfamily) <> ''),
    CONSTRAINT chk_payout_batches_handler CHECK (btrim(handler) <> ''),
    CONSTRAINT chk_payout_batches_recipientsethash CHECK (btrim(recipientsethash) <> ''),
    CONSTRAINT chk_payout_batches_minimumamount CHECK (minimumamount >= 0),
    CONSTRAINT chk_payout_batches_reservedamountsnapshot CHECK (reservedamountsnapshot > 0),
    CONSTRAINT chk_payout_batches_intentcountsnapshot CHECK (intentcountsnapshot > 0),
    CONSTRAINT uq_payout_batches_id_pool_coin UNIQUE(id, poolid, coin)
);

CREATE TABLE payout_intents
(
    id BIGSERIAL NOT NULL PRIMARY KEY,
    batchid BIGINT NOT NULL,
    poolid TEXT NOT NULL,
    coin TEXT NOT NULL,
    address TEXT NOT NULL,
    state TEXT NOT NULL,
    amount decimal(28,12) NOT NULL,
    balancesnapshotamount decimal(28,12) NOT NULL,
    balancesnapshotupdated TIMESTAMPTZ NOT NULL,
    paymentthreshold decimal(28,12) NOT NULL,
    transactionconfirmationdata TEXT NULL,
    paymentid BIGINT NULL,
    balancechangeid BIGINT NULL,
    created TIMESTAMPTZ NOT NULL,
    updated TIMESTAMPTZ NOT NULL,
    settled TIMESTAMPTZ NULL,
    errorcode TEXT NULL,
    errormessage TEXT NULL,

    CONSTRAINT uq_payout_intents_id_batch_pool_coin UNIQUE(id, batchid, poolid, coin),
    CONSTRAINT fk_payout_intents_batch FOREIGN KEY(batchid, poolid, coin) REFERENCES payout_batches(id, poolid, coin),
    CONSTRAINT fk_payout_intents_payment FOREIGN KEY(paymentid) REFERENCES payments(id),
    CONSTRAINT fk_payout_intents_balancechange FOREIGN KEY(balancechangeid) REFERENCES balance_changes(id),
    CONSTRAINT chk_payout_intents_state CHECK (state IN ('reserved', 'sending', 'submitted', 'settled', 'failed', 'ambiguous_requires_review', 'cancelled')),
    CONSTRAINT chk_payout_intents_poolid CHECK (btrim(poolid) <> ''),
    CONSTRAINT chk_payout_intents_coin CHECK (btrim(coin) <> ''),
    CONSTRAINT chk_payout_intents_address CHECK (btrim(address) <> ''),
    CONSTRAINT chk_payout_intents_amount CHECK (amount > 0),
    CONSTRAINT chk_payout_intents_balancesnapshotamount CHECK (balancesnapshotamount >= amount),
    CONSTRAINT chk_payout_intents_paymentthreshold CHECK (paymentthreshold >= 0),
    CONSTRAINT chk_payout_intents_settled_links CHECK (state <> 'settled' OR (paymentid IS NOT NULL AND balancechangeid IS NOT NULL AND transactionconfirmationdata IS NOT NULL))
);

CREATE TABLE payout_send_attempts
(
    id BIGSERIAL NOT NULL PRIMARY KEY,
    batchid BIGINT NOT NULL,
    poolid TEXT NOT NULL,
    coin TEXT NOT NULL,
    attemptno INT NOT NULL,
    method TEXT NOT NULL,
    state TEXT NOT NULL,
    requesthash TEXT NOT NULL,
    requestsummary TEXT NULL,
    recipientcount INT NOT NULL,
    amountsnapshot decimal(28,12) NOT NULL,
    externaloperationid TEXT NULL,
    transactionconfirmationdata TEXT NULL,
    errorcode TEXT NULL,
    errormessage TEXT NULL,
    created TIMESTAMPTZ NOT NULL,
    updated TIMESTAMPTZ NOT NULL,
    completed TIMESTAMPTZ NULL,

    CONSTRAINT uq_payout_send_attempts_id_batch_pool_coin UNIQUE(id, batchid, poolid, coin),
    CONSTRAINT fk_payout_send_attempts_batch FOREIGN KEY(batchid, poolid, coin) REFERENCES payout_batches(id, poolid, coin),
    CONSTRAINT chk_payout_send_attempts_state CHECK (state IN ('prepared', 'sending', 'accepted', 'failed_pre_accept', 'failed_no_accept', 'ambiguous_requires_review')),
    CONSTRAINT chk_payout_send_attempts_poolid CHECK (btrim(poolid) <> ''),
    CONSTRAINT chk_payout_send_attempts_coin CHECK (btrim(coin) <> ''),
    CONSTRAINT chk_payout_send_attempts_method CHECK (btrim(method) <> ''),
    CONSTRAINT chk_payout_send_attempts_requesthash CHECK (btrim(requesthash) <> ''),
    CONSTRAINT chk_payout_send_attempts_requestsummary CHECK (requestsummary IS NULL OR btrim(requestsummary) <> ''),
    CONSTRAINT chk_payout_send_attempts_attemptno CHECK (attemptno > 0),
    CONSTRAINT chk_payout_send_attempts_recipientcount CHECK (recipientcount > 0),
    CONSTRAINT chk_payout_send_attempts_amountsnapshot CHECK (amountsnapshot > 0)
);

CREATE TABLE payout_attempt_intents
(
    attemptid BIGINT NOT NULL,
    intentid BIGINT NOT NULL,
    batchid BIGINT NOT NULL,
    poolid TEXT NOT NULL,
    coin TEXT NOT NULL,
    state TEXT NOT NULL,
    amount decimal(28,12) NOT NULL,
    transactionconfirmationdata TEXT NULL,
    created TIMESTAMPTZ NOT NULL,
    updated TIMESTAMPTZ NOT NULL,

    CONSTRAINT pk_payout_attempt_intents PRIMARY KEY(attemptid, intentid),
    CONSTRAINT fk_payout_attempt_intents_attempt FOREIGN KEY(attemptid, batchid, poolid, coin) REFERENCES payout_send_attempts(id, batchid, poolid, coin),
    CONSTRAINT fk_payout_attempt_intents_intent FOREIGN KEY(intentid, batchid, poolid, coin) REFERENCES payout_intents(id, batchid, poolid, coin),
    CONSTRAINT chk_payout_attempt_intents_state CHECK (state IN ('active', 'accepted', 'failed_pre_accept', 'failed_no_accept', 'ambiguous_requires_review', 'superseded')),
    CONSTRAINT chk_payout_attempt_intents_poolid CHECK (btrim(poolid) <> ''),
    CONSTRAINT chk_payout_attempt_intents_coin CHECK (btrim(coin) <> ''),
    CONSTRAINT chk_payout_attempt_intents_amount CHECK (amount > 0)
);

CREATE TABLE payout_external_confirmations
(
    id BIGSERIAL NOT NULL PRIMARY KEY,
    poolid TEXT NOT NULL,
    coin TEXT NOT NULL,
    batchid BIGINT NOT NULL,
    attemptid BIGINT NULL,
    intentid BIGINT NULL,
    kind TEXT NOT NULL,
    value TEXT NOT NULL,
    created TIMESTAMPTZ NOT NULL,

    CONSTRAINT fk_payout_external_confirmations_batch FOREIGN KEY(batchid, poolid, coin) REFERENCES payout_batches(id, poolid, coin),
    CONSTRAINT fk_payout_external_confirmations_attempt FOREIGN KEY(attemptid, batchid, poolid, coin) REFERENCES payout_send_attempts(id, batchid, poolid, coin) MATCH SIMPLE,
    CONSTRAINT fk_payout_external_confirmations_intent FOREIGN KEY(intentid, batchid, poolid, coin) REFERENCES payout_intents(id, batchid, poolid, coin) MATCH SIMPLE,
    CONSTRAINT chk_payout_external_confirmations_kind CHECK (kind IN ('txid', 'operationid', 'wallet_ack', 'raw_hash')),
    CONSTRAINT chk_payout_external_confirmations_poolid CHECK (btrim(poolid) <> ''),
    CONSTRAINT chk_payout_external_confirmations_coin CHECK (btrim(coin) <> ''),
    CONSTRAINT chk_payout_external_confirmations_value CHECK (btrim(value) <> '')
);

CREATE TABLE payout_admin_actions
(
    id BIGSERIAL NOT NULL PRIMARY KEY,
    poolid TEXT NULL,
    batchid BIGINT NULL,
    intentid BIGINT NULL,
    attemptid BIGINT NULL,
    action TEXT NOT NULL,
    reason TEXT NOT NULL,
    operator TEXT NULL,
    metadata TEXT NULL,
    created TIMESTAMPTZ NOT NULL,

    CONSTRAINT fk_payout_admin_actions_batch FOREIGN KEY(batchid) REFERENCES payout_batches(id),
    CONSTRAINT fk_payout_admin_actions_intent FOREIGN KEY(intentid) REFERENCES payout_intents(id),
    CONSTRAINT fk_payout_admin_actions_attempt FOREIGN KEY(attemptid) REFERENCES payout_send_attempts(id),
    CONSTRAINT chk_payout_admin_actions_target CHECK (poolid IS NOT NULL OR batchid IS NOT NULL OR intentid IS NOT NULL OR attemptid IS NOT NULL),
    CONSTRAINT chk_payout_admin_actions_poolid CHECK (poolid IS NULL OR btrim(poolid) <> ''),
    CONSTRAINT chk_payout_admin_actions_reason CHECK (btrim(reason) <> ''),
    CONSTRAINT chk_payout_admin_actions_operator CHECK (operator IS NULL OR btrim(operator) <> ''),
    CONSTRAINT chk_payout_admin_actions_action CHECK (action IN ('pause', 'resume', 'run_now', 'cancel_reserved', 'mark_failed_pre_accept', 'mark_failed_no_accept', 'attach_txid', 'attach_operationid', 'settle_ambiguous', 'release_ambiguous', 'retry_after_review')),
    CONSTRAINT chk_payout_admin_actions_pool_action CHECK (action NOT IN ('pause', 'resume', 'run_now') OR poolid IS NOT NULL)
);

CREATE UNIQUE INDEX idx_payout_batches_one_active_pool_unique
    ON payout_batches(poolid)
    WHERE state IN ('reserved', 'sending', 'submitted', 'ambiguous_requires_review');

CREATE UNIQUE INDEX idx_payout_batches_active_recipientset_unique
    ON payout_batches(poolid, recipientsethash)
    WHERE state IN ('reserved', 'sending', 'submitted', 'ambiguous_requires_review');

CREATE INDEX idx_payout_batches_pool_state_created_desc
    ON payout_batches(poolid, state, created DESC);

CREATE INDEX idx_payout_batches_state_updated
    ON payout_batches(state, updated);

CREATE INDEX idx_payout_batches_pool_created_desc
    ON payout_batches(poolid, created DESC);

CREATE UNIQUE INDEX idx_payout_intents_batch_address_unique
    ON payout_intents(batchid, address);

CREATE UNIQUE INDEX idx_payout_intents_paymentid_unique
    ON payout_intents(paymentid)
    WHERE paymentid IS NOT NULL;

CREATE UNIQUE INDEX idx_payout_intents_balancechangeid_unique
    ON payout_intents(balancechangeid)
    WHERE balancechangeid IS NOT NULL;

CREATE UNIQUE INDEX idx_payout_intents_one_active_address_unique
    ON payout_intents(poolid, address)
    WHERE state IN ('reserved', 'sending', 'submitted', 'ambiguous_requires_review');

CREATE INDEX idx_payout_intents_batch
    ON payout_intents(batchid);

CREATE INDEX idx_payout_intents_pool_address_state
    ON payout_intents(poolid, address, state);

CREATE INDEX idx_payout_intents_state_updated
    ON payout_intents(state, updated);

CREATE INDEX idx_payout_intents_pool_created_desc
    ON payout_intents(poolid, created DESC);

CREATE UNIQUE INDEX idx_payout_send_attempts_batch_attemptno_unique
    ON payout_send_attempts(batchid, attemptno);

CREATE UNIQUE INDEX idx_payout_send_attempts_batch_requesthash_unique
    ON payout_send_attempts(batchid, requesthash);

CREATE INDEX idx_payout_send_attempts_batch_state
    ON payout_send_attempts(batchid, state);

CREATE INDEX idx_payout_send_attempts_pool_state_updated
    ON payout_send_attempts(poolid, state, updated);

CREATE INDEX idx_payout_send_attempts_state_updated
    ON payout_send_attempts(state, updated);

CREATE UNIQUE INDEX idx_payout_attempt_intents_one_nonfinal_unique
    ON payout_attempt_intents(intentid)
    WHERE state IN ('active', 'accepted', 'ambiguous_requires_review');

CREATE INDEX idx_payout_attempt_intents_attempt
    ON payout_attempt_intents(attemptid);

CREATE INDEX idx_payout_attempt_intents_intent
    ON payout_attempt_intents(intentid);

CREATE INDEX idx_payout_attempt_intents_state_updated
    ON payout_attempt_intents(state, updated);

CREATE UNIQUE INDEX idx_payout_external_confirmations_evidence_unique
    ON payout_external_confirmations(poolid, coin, kind, value);

CREATE INDEX idx_payout_external_confirmations_batch
    ON payout_external_confirmations(batchid);

CREATE INDEX idx_payout_external_confirmations_attempt
    ON payout_external_confirmations(attemptid);

CREATE INDEX idx_payout_external_confirmations_intent
    ON payout_external_confirmations(intentid);

CREATE INDEX idx_payout_external_confirmations_pool_coin_kind
    ON payout_external_confirmations(poolid, coin, kind);

CREATE INDEX idx_payout_admin_actions_batch_created_desc
    ON payout_admin_actions(batchid, created DESC);

CREATE INDEX idx_payout_admin_actions_pool_created_desc
    ON payout_admin_actions(poolid, created DESC);

CREATE INDEX idx_payout_admin_actions_pool_action_created_desc
    ON payout_admin_actions(poolid, action, created DESC);

CREATE INDEX idx_payout_admin_actions_intent_created_desc
    ON payout_admin_actions(intentid, created DESC);

CREATE INDEX idx_payout_admin_actions_attempt_created_desc
    ON payout_admin_actions(attemptid, created DESC);

CREATE INDEX idx_payout_admin_actions_created_desc
    ON payout_admin_actions(created DESC);
