# HashStormCore Event Pipeline

This document is the operating reference for the scoped event-driven runtime.
Read it as the answer to three questions:

- What runs where?
- Which data is durable, and when?
- How do I start, debug, and reason about the stack?

## Mental Model

HashStormCore is split into one mining process and three sidecars:

```text
Miners
  -> HashStormCore Pool Core
  -> in-memory ShareEvent handoff
  -> local WAL/outbox
  -> Redis Streams
  -> DbWriter / LiveAggregator
  -> PostgreSQL / Redis live read models
  -> ApiProvider
```

Pool Core is the only process that owns mining-critical work:

- Stratum listeners
- share validation
- block candidate submission
- miner responses
- local event handoff
- WAL/outbox writing and publishing

The sidecars do not validate shares and do not submit blocks:

- `HashStormCore.DbWriter` consumes Redis Streams and writes historical/accounting data to PostgreSQL.
- `HashStormCore.LiveAggregator` consumes Redis Streams and writes rolling live state into Redis.
- `HashStormCore.ApiProvider` serves frontend-facing reads from Redis and PostgreSQL.

Redis is required for the event-pipeline runtime. It is not only a cache. Redis Streams are the broker between Pool Core and the sidecars, and Redis keys are the live-state read model.

PostgreSQL is required for historical/accounting state.

## Process Ownership

### Pool Core

Executable:

```bash
HashStormCore -c configs/config.json
```

Responsibilities:

- accept miners
- validate shares
- submit block candidates
- respond to miners without waiting for Redis/PostgreSQL/API
- enqueue `ShareEvent` records after mining processing
- append events to local WAL/outbox
- publish WAL batches to Redis Streams

Pool Core is configured through `-c|--config <configfile>`. The `eventPipeline` block lives in the main Pool Core config.

Important Pool Core options:

- `eventPipeline.enabled`: enables the scoped event-driven architecture.
- `poolCore.publicApiEnabled`: enables Pool Core's embedded API, metrics, and WebSocket host.
- `poolCore.liveStateEnabled`: controls legacy/direct in-process live-state handling. It is mostly redundant when `eventPipeline.enabled=true`.

When `eventPipeline.enabled=true`, the WAL/outbox is required. There is no supported `eventPipeline.outbox.enabled=false` mode.

### DbWriter

Executable:

```bash
HashStormCore.DbWriter
```

Config sources:

- reads Pool Core config first, from `HASHSTORM_CONFIG` or `configs/config.json`
- optional flat override: `configs/db-writer.json`
- environment prefix: `HASHSTORM_DBWRITER_`

Important options:

- `redisConnectionString`
- `streamName`
- `consumerName`
- `readBatchSize`
- `pendingMinIdleMs`
- `postgresConnectionString`

`postgresConnectionString` is required. If it is omitted, DbWriter derives it from `persistence.postgres` in the Pool Core config.

DbWriter creates the Redis consumer group `db-writer` at `0-0`. This means it can process retained backlog from the beginning of the stream. It ACKs Redis messages only after the PostgreSQL transaction commits.

### LiveAggregator

Executable:

```bash
HashStormCore.LiveAggregator
```

Config sources:

- reads Pool Core config first, from `HASHSTORM_CONFIG` or `configs/config.json`
- optional flat override: `configs/live-aggregator.json`
- environment prefix: `HASHSTORM_LIVE_`

Important options:

- `redisConnectionString`
- `streamName`
- `consumerName`
- `readBatchSize`
- `pendingMinIdleMs`
- `liveWindowSeconds`
- `redisTtlSeconds`
- `bucketSeconds`
- `pools`

LiveAggregator creates the Redis consumer group `live-aggregator` at `$`. First startup starts from new group messages only, then LiveAggregator rebuilds the current rolling live window separately by reading retained Redis Stream entries for the live window.

The `pools` list is used for startup clear/warming status. It is not an event filter.

### ApiProvider

Executable:

```bash
HashStormCore.ApiProvider
```

Config sources:

- reads Pool Core config first, from `HASHSTORM_CONFIG` or `configs/config.json`
- optional flat override: `configs/api-provider.json`
- environment prefix: `HASHSTORM_API_`

Important options:

- `redisConnectionString`
- `postgresConnectionString`
- `listenUrl`

`postgresConnectionString` is derived from Pool Core config if omitted.

ApiProvider does not use an `/api` prefix. Current routes are:

- `GET /live/pools/{poolId}/summary`
- `GET /live/pools/{poolId}/top-miners?count=100`
- `GET /live/pools/{poolId}/miners/{miner}`
- `GET /live/pools/{poolId}/miners/{miner}/workers`
- `GET /live/pools/{poolId}/status`
- `GET /historical/pools/{poolId}/stats/latest`
- `GET /historical/pools/{poolId}/share-events?from=<utc>&to=<utc>&limit=100`

Pool Core's embedded API is separate. If enabled, it keeps the existing `/api/...` routes, `/metrics`, and WebSocket notifications endpoint. The legacy static website is not part of the event-pipeline runtime.

## Startup And Build Workflow

Run scripts are intentionally separated from build scripts.

Build sidecars only:

```bash
./scripts/build-services.sh
```

Build Pool Core and sidecars:

```bash
./scripts/build-all.sh
```

Run the full stack from already-published binaries:

```bash
./scripts/run-all.sh
```

`run-all.sh` does not run `dotnet build`, `dotnet publish`, or `dotnet run`. It expects published outputs under `build/`.

Default published layout:

```text
build/pool-core/
build/db-writer/
build/live-aggregator/
build/api-provider/
```

For compatibility, `run-all.sh` also accepts the older Pool Core layout:

```text
build/HashStormCore
build/HashStormCore.dll
```

By default, `run-all.sh` opens one terminal window per process for live logs. Use background mode when needed:

```bash
HASHSTORM_BACKGROUND=true ./scripts/run-all.sh
```

Redis startup behavior:

- default: `HASHSTORM_START_REDIS=auto`
- if Redis is reachable, the script uses it
- if Redis is not reachable and `redis-server` exists, the script starts local Redis with `redis-server --daemonize yes`
- if Redis cannot be made reachable, the script exits before starting sidecars

For production, prefer Redis managed by `systemd`, Docker, Kubernetes, or an external Redis service. The script-started Redis mode is for local development.

## Runtime Config Layout

Default runtime config directory:

```text
configs/
```

Typical files:

```text
configs/config.json
configs/db-writer.json
configs/live-aggregator.json
configs/api-provider.json
```

Examples:

```text
configs/config.example.json
configs/config.legacy.example.json
configs/db-writer.example.json
configs/live-aggregator.example.json
configs/api-provider.example.json
configs/event-pipeline.example.json
```

Pool Core config contains mining pool settings, daemon RPC settings, payment settings, PostgreSQL settings, and event-pipeline settings. Sidecar files should only contain sidecar overrides.

If sidecar Redis/Postgres settings are omitted, sidecars derive defaults from the Pool Core config.

## Event Pipeline Config Cheat Sheet

Main Pool Core config section:

```text
eventPipeline
```

Broker:

- `eventPipeline.broker.type`: currently `redis-streams`.
- `eventPipeline.broker.connectionString`: Redis connection string.
- `eventPipeline.broker.streamName`: Redis Stream name, usually `hashstorm:share-events`.
- `eventPipeline.broker.producerId`: Pool Core producer identity written into batches.
- `eventPipeline.broker.startupRequired`: if `false`, Redis startup/connectivity failure does not block Pool Core construction.
- `eventPipeline.broker.publishRetryDelayMs`: initial Redis publish retry delay.
- `eventPipeline.broker.publishMaxRetryDelayMs`: maximum Redis publish retry delay.
- `eventPipeline.broker.pendingMinIdleMs`: stale pending message threshold for consumers.

Outbox:

- `eventPipeline.outbox.directory`
- `eventPipeline.outbox.segmentMaxBytes`
- `eventPipeline.outbox.writerFlushEvents`
- `eventPipeline.outbox.writerFlushBytes`
- `eventPipeline.outbox.writerFlushMs`
- `eventPipeline.outbox.fsyncMode`
- `eventPipeline.outbox.fsyncIntervalMs`
- `eventPipeline.outbox.softBacklogBytes`
- `eventPipeline.outbox.criticalBacklogBytes`

Batching:

- `eventPipeline.batching.maxEvents`
- `eventPipeline.batching.maxApproxBytes`
- `eventPipeline.batching.maxDelayMs`

Live defaults consumed by LiveAggregator unless overridden:

- `eventPipeline.live.windowSeconds`
- `eventPipeline.live.redisTtlSeconds`
- `eventPipeline.live.bucketSeconds`

Retention warnings:

- `eventPipeline.retention.softStreamLengthWarning`
- `eventPipeline.retention.criticalStreamLengthWarning`

Do not add stale legacy keys such as `maxStreamLength`, `maxLocalQueueBatches`, `overflowPolicy`, `maxBufferedEvents`, `maxBufferedBytes`, or `outbox.enabled`.

## Event Types

Redis Stream entries contain one field:

```text
payload
```

The payload is `ShareEventBatch` JSON.

Serialization details:

- JSON casing is PascalCase.
- `EventType` is numeric by default.
- The enum values are:

```text
1 ShareAccepted
2 ShareRejected
3 ShareStale
4 BlockCandidate
5 BlockAccepted
6 BlockRejected
7 NewTemplate
8 PoolOnline
9 PoolOffline
```

`eventId` is generated once when the event is created. It must remain unchanged through handoff, WAL, Redis, replay, and DbWriter. Idempotency is based on this ID.

## Durability Contract

HashStormCore does not claim absolute zero-loss for every signal. The guarantee is scoped.

Critical accounting/block events:

- `ShareAccepted`
- `BlockCandidate`
- future `BlockAccepted`/`BlockRejected` events only when they mutate canonical block/accounting state or reconcile pending block state

After Pool Core accepts these critical events for processing, software policy must not intentionally drop them. If Redis, sidecars, or PostgreSQL slow down, the in-memory queue and WAL are allowed to grow while warnings escalate. Disk/RAM exhaustion is an infrastructure failure, not a reason to silently skip critical events.

Telemetry/audit events:

- `ShareRejected`
- `ShareStale`
- diagnostic rejection reasons
- malformed protocol noise
- lifecycle events unless promoted later

These are useful, but not payout-critical by default. They may be written to `share_events`, but they are not inserted into `shares`.

Pre-admission shedding is outside the `ShareEvent` guarantee. Example: a very old submit rejected before validation because `requestAge > maxShareAge`.

The legacy external `ShareReceiver`/ZMQ path is best-effort. It is not covered by the local Pool Core accounting-critical guarantee unless future relay-side durable outbox support is added.

## Miner Response Ordering

The miner submit path is latency-first:

1. Parse, validate, and process the submit.
2. Submit block candidates immediately.
3. Queue/accept the miner response on the Stratum send path.
4. Perform local in-memory event handoff for critical share/block events.
5. Background writer appends events to WAL segment files.
6. Background publisher reads WAL and publishes batches to Redis Streams.
7. DbWriter and LiveAggregator consume Redis Streams.

Redis, PostgreSQL, HTTP/API calls, broker acknowledgements, WAL writes, and WAL fsync are not allowed before the miner response.

Critical handoff does not wait for TCP flush success. If the response was accepted into the Stratum send path, a later socket write failure must not erase the accepted accounting/block event.

There is still a crash window: if Pool Core crashes after the miner response is queued but before the event reaches the WAL writer, the event can be lost. This is intentional. Mining response latency has priority. True crash-safe zero-loss would require durable pre-response append/fsync, which is not the default architecture.

## Handoff Queue

`eventPipeline.handoff` configures warnings for the in-process queue feeding the WAL writer:

- `softMaxBufferedEvents`
- `softMaxBufferedBytes`
- `criticalBufferedEvents`
- `criticalBufferedBytes`

These are warning/drain thresholds, not hard capacity limits. There is no production overflow policy, drop mode, or stop-pool mode for the critical handoff path.

Do not reintroduce stale keys:

- `overflowPolicy`
- `maxBufferedEvents`
- `maxBufferedBytes`
- `maxLocalQueueBatches`

## WAL / Outbox

When `eventPipeline.enabled=true`, the WAL/outbox is mandatory.

Config section:

```text
eventPipeline.outbox
```

Important options:

- `directory`
- `segmentMaxBytes`
- `writerFlushEvents`
- `writerFlushBytes`
- `writerFlushMs`
- `fsyncMode`
- `fsyncIntervalMs`
- `softBacklogBytes`
- `criticalBacklogBytes`

Record format is self-delimiting:

- magic/version
- payload length
- JSON `ShareEvent` payload
- checksum

Recovery scans each segment from the start, reads complete valid records, stops at the first partial/corrupt record, and truncates the partial tail when safe. Recovery does not invent events or skip earlier valid records.

The production publish path is:

```text
IShareEventQueue
  -> ShareEventOutboxWriter
  -> file WAL
  -> ShareEventOutboxPublisher
  -> RedisStreamsShareEventBatchTransport
```

If Redis is down, WAL grows on disk and the publisher retries with backoff. If publish succeeds but Pool Core crashes before checkpoint advance, events may be republished. Consumers must be idempotent.

`fsyncMode=periodic` keeps mining fast. `fsyncMode=always` is slower and explicit. Neither mode performs WAL work before miner response.

## Redis Streams

Supported broker:

```json
"type": "redis-streams"
```

Pool Core does not use Redis `MAXLEN` trimming for streams that contain critical/mixed events. Do not configure stream trimming for `hashstorm:share-events` unless critical and telemetry events have been split into separate streams.

Consumer behavior:

- own pending messages are read with group ID `0`
- stale pending messages are reclaimed with `XAUTOCLAIM`
- new messages are read with `>`
- `pendingMinIdleMs` controls stale pending recovery

DbWriter ACKs only after PostgreSQL commit. LiveAggregator ACKs only after Redis live-state writes succeed.

`eventPipeline.retention` is warning-only while critical events share the stream:

- `softStreamLengthWarning`
- `criticalStreamLengthWarning`

It must not become a hard drop/trim policy for critical streams.

## PostgreSQL

Tables involved:

- `shares`: existing accounting/payout share table.
- `blocks`: existing block tracking table.
- `share_events`: event-pipeline event history.
- `event_pipeline_processed_events`: persistent idempotency table.

`share_events` can store accepted, rejected, stale, block, and lifecycle events.

`shares` receives:

- `ShareAccepted`
- `BlockCandidate`
- `BlockAccepted` only if represented as an accepted accounting share by existing semantics

`shares` does not receive:

- `ShareRejected`
- `ShareStale`
- `BlockRejected`
- `NewTemplate`
- `PoolOnline`
- `PoolOffline`

DbWriter commits the idempotency row and side effects in the same transaction.

`BlockCandidate` inserts the accepted share row and a pending `blocks` row using the fields carried in the `ShareEvent`. The job manager still submits block candidates during share processing. Only daemon-accepted block candidates should become `BlockCandidate` events for pending block persistence.

Required SQL:

```text
src/HashStormCore/Persistence/Postgres/Scripts/createdb.sql
src/HashStormCore/Persistence/Postgres/Scripts/event_pipeline.sql
```

`createdb.sql` may contain:

```sql
SET ROLE HashStormCore;
```

For local setups using another role, create the expected role or use a temporary copy without that `SET ROLE`. The helper script `scripts/create-db.sh` handles this by stripping `SET ROLE` and applying the scripts under the configured database user.

The default `createdb.sql` creates a non-partitioned `shares` table. Partition creation is only needed if you intentionally apply the PostgreSQL 11 partition appendix and run a partitioned `shares` table.

## Live State

LiveAggregator writes rolling Redis buckets based on `ShareEvent.Created`, not receive time.

Important behavior:

- bucket size is `bucketSeconds`
- live window is `liveWindowSeconds`
- Redis TTL is `redisTtlSeconds`
- duplicate `eventId` values are deduped inside each live bucket with Redis `SADD`
- rejected/stale events increment live counters but do not add accepted hashrate difficulty
- startup status is `warming_up` until enough window data exists
- `LastSeen` comes from latest event time observed for miner/worker

On startup, LiveAggregator:

1. creates its consumer group at `$`
2. clears configured pools' live Redis namespace
3. marks configured pools as warming
4. rebuilds the current window from retained Redis Stream entries
5. consumes new stream messages

If an old `live-aggregator` consumer group already exists from a previous version and was created at `0-0`, Redis keeps that old start point. Destroy the old group or use a new consumer group name if you need the `$` semantics.

## Legacy And Static Website

The scoped event pipeline does not ship or serve a bundled legacy static pool website.

Current UI-facing surfaces:

- ApiProvider routes under `/live` and `/historical`
- optional Pool Core embedded API under `/api`
- optional Pool Core `/metrics`
- optional Pool Core WebSocket notifications endpoint

A future Next.js frontend should consume ApiProvider first, and Pool Core endpoints only where still needed operationally.

## Local Bring-Up Checklist

1. Install PostgreSQL and create/apply schema:

```bash
./scripts/create-db.sh
```

Use the same database username/password in `configs/config.json`.

2. Install/start Redis:

```bash
sudo apt install redis-server
sudo systemctl enable --now redis-server
redis-cli ping
```

Expected:

```text
PONG
```

3. Prepare configs:

```bash
cp configs/config.example.json configs/config.json
cp configs/db-writer.example.json configs/db-writer.json
cp configs/live-aggregator.example.json configs/live-aggregator.json
cp configs/api-provider.example.json configs/api-provider.json
```

4. Build:

```bash
./scripts/build-all.sh
```

Or, if Pool Core is already built and only sidecars changed:

```bash
./scripts/build-services.sh
```

5. Run:

```bash
./scripts/run-all.sh
```

6. Stop:

```bash
./scripts/stop-all.sh
```

## Common Failures

### PostgreSQL password authentication failed

Symptom:

```text
28P01: password authentication failed for user "hashstorm"
```

Meaning: the password in `configs/config.json` does not match the PostgreSQL role password.

Fix: rerun `scripts/create-db.sh` with the same username/password, or edit `configs/config.json` to match the real database credentials.

### Redis connection timeout

Symptom:

```text
UnableToConnect on localhost:6379
command=XGROUP
```

Meaning: Redis is not running or the config points to the wrong host/port.

Fix:

```bash
sudo systemctl status redis-server
redis-cli ping
```

Then rerun:

```bash
./scripts/stop-all.sh
./scripts/run-all.sh
```

### PDB file locked during build

Symptom:

```text
Cannot open ... HashStormCore.Contracts.pdb for writing
```

Meaning: multiple `dotnet run`/builds were compiling shared projects at the same time.

Fix: do not use run scripts to build. Build once with `scripts/build-all.sh` or `scripts/build-services.sh`, then run with `scripts/run-all.sh`.

### Sidecar starts but has no data

Likely causes:

- Redis is empty because Pool Core is not publishing yet.
- Pool Core is down because PostgreSQL preflight failed.
- Redis stream name differs between Pool Core and sidecars.
- LiveAggregator started with a new group at `$`, so it only consumes new messages and rebuilds only the current live window.

## Important Non-Goals

Do not change these without deliberately redesigning the architecture:

- Do not make Redis/PostgreSQL part of the pre-response miner path.
- Do not trim critical/mixed Redis Streams with `MAXLEN`.
- Do not insert rejected/stale telemetry into `shares`.
- Do not bypass WAL/outbox for production publishing.
- Do not treat lifecycle events as accounting-critical unless a future design promotes them.
- Do not make Pool Core responsible for supervising sidecars in-process.
