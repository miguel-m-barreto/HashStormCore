# HashStormCore

<img src="https://raw.githubusercontent.com/miguel-m-barreto/HashStormCore/674eaa74a2c9486b5c4fc65a7694b41a1186f383/banner.png" width="2048">

HashStormCore is a modern technical successor to the original [Miningcore](https://github.com/coinfoundry/miningcore).

It keeps Miningcore's proven mining foundation while modernizing the runtime around a scoped event-driven architecture:

```text
Pool Core
  -> in-memory ShareEvent handoff
  -> WAL/outbox
  -> Redis Streams
  -> DbWriter / LiveAggregator / ApiProvider
```

The goal is simple: keep Pool Core focused on mining-critical work, protect miner response latency, and move live state, historical persistence, and frontend-facing reads into sidecar processes that are easier to scale and monitor.

Status: early 1.x line - experimental, under active development.

## Why HashStormCore Exists

Miningcore remains one of the most widely used pool engines, but many parts of the original architecture were designed around older deployment assumptions:

- live dashboards depended too heavily on database reads
- per-worker live state was limited
- scaling live APIs and persistence together with the mining hot path was awkward
- native hashing and several coin/job-manager paths needed modernization
- the old static website model does not fit a modern frontend stack

HashStormCore addresses these problems by splitting the runtime into specialized processes while preserving the core mining behavior.

## Runtime Components

A full event-pipeline deployment can run as four processes:

| Process | Responsibility |
| --- | --- |
| `HashStormCore` | Pool Core: Stratum, share validation, block candidate submission, miner responses, local ShareEvent handoff, WAL/outbox publishing |
| `HashStormCore.DbWriter` | Consumes Redis Streams and writes historical/accounting state to PostgreSQL |
| `HashStormCore.LiveAggregator` | Consumes Redis Streams and writes rolling live state into Redis |
| `HashStormCore.ApiProvider` | Serves frontend-facing live and historical API reads from Redis/PostgreSQL |

Event flow:

```text
[Miners]
   |
   v
[HashStormCore Pool Core]
   |
   v
[In-memory ShareEvent queue]
   |
   v
[Local WAL/outbox]
   |
   v
[Redis Streams]
   |-----------------------------|
   v                             v
[DbWriter -> PostgreSQL]   [LiveAggregator -> Redis live state]
   |                             |
   |-----------------------------|
                 v
            [ApiProvider]
                 |
                 v
       [Next.js frontend / reverse proxy]
```

Pool Core still owns mining, share validation, and block submission. Sidecars do not validate shares and do not submit blocks.

## Scoped Event Durability

HashStormCore does not claim absolute zero-loss for every possible signal. The durability model is scoped.

Critical accounting/block events receive strong no-intentional-software-drop semantics after Pool Core accepts them for processing:

- `ShareAccepted`
- `BlockCandidate`
- `BlockAccepted`, when used to mutate canonical block/accounting state
- `BlockRejected`, when used to reconcile canonical block/accounting state

These events are handed off after the miner response is accepted into the local Stratum send path. Pool Core does not wait for TCP flush success, Redis, PostgreSQL, API calls, broker acknowledgements, or WAL fsync before responding to the miner.

Telemetry and audit events are useful for diagnostics and may be persisted, but are not payout-critical by default:

- `ShareRejected`
- `ShareStale`
- diagnostic rejection reasons
- lifecycle/live signals such as `NewTemplate`, `PoolOnline`, and `PoolOffline`

Rejected and stale share events must not be inserted into the existing `shares` accounting table.

Very old submit requests rejected before full share validation, for example `requestAge > maxShareAge`, are not accepted shares and are not accounting-critical records. They still receive a normal miner response and, when the event pipeline is enabled, emit non-penalizing stale/rejection telemetry after the response.

The legacy external ShareReceiver/ZMQ path is best-effort. It is not covered by the local Pool Core accounting-critical guarantee unless future relay-side durable outbox support is added.

## Features

HashStormCore currently includes:

- Miningcore-derived multi-pool Stratum engine
- adaptive difficulty
- native hashing implementations
- automated payment processing
- PostgreSQL persistence
- Redis Streams event pipeline
- WAL/outbox-backed event publishing
- DbWriter sidecar for historical/accounting writes
- LiveAggregator sidecar for rolling live state
- ApiProvider sidecar for frontend-facing reads
- worker-aware live tracking
- event idempotency and replay handling
- native hashing fixes and modernization work
- BTCZ/Equihash compatibility work
- scoped cleanup of legacy live-state/API responsibilities

## Documentation

- `docs/event-pipeline.md` - event-driven pipeline, config, durability, Redis, DbWriter, LiveAggregator, ApiProvider.
- `live-api.md` - legacy Pool Core embedded live API compatibility reference, where applicable.
- `configs/*.example.json` - sidecar and event-pipeline config examples.
- `configs/config.example.json` - full Pool Core config example.
- `ROADMAP.md` - planned improvements.
- `CONTRIBUTING.md` - contribution rules.

## Installation and Build

HashStormCore targets .NET 10. The repo root `global.json` pins SDK selection for local CLI builds.

Clone the repository:

```bash
git clone https://github.com/miguel-m-barreto/HashStormCore.git
cd HashStormCore
```

Build/publish Pool Core manually:

```bash
dotnet publish src/HashStormCore/HashStormCore.csproj -c Release --framework net10.0 -o build/pool-core
```

Build helpers are also available:

- `build-ubuntu-22.04.sh`
- `build-ubuntu-24.04.sh`
- `build-windows.bat` for Windows development/debugging

Do not treat build scripts as production deployment automation. Production requires explicit config, database setup, Redis, process supervision, firewalling, monitoring, and backups.

## Configuration

### Pool Core

Pool Core can be started explicitly with:

```bash
dotnet run --project src/HashStormCore/HashStormCore.csproj -- -c ./configs/config.json
```

From a published build:

```bash
./HashStormCore -c ./configs/config.json
```

If `-c|--config` is omitted, Pool Core searches for a default config in this order:

- `HASHSTORM_CONFIG`
- `./configs/config.json` from the current working directory
- `configs/config.json` beside the running binary
- legacy fallback `./config.json`, when present
- `src/HashStormCore/configs/config.json` during local development

Start from the full Pool Core example:

```bash
mkdir -p configs
cp configs/config.example.json configs/config.json
```

For the local-development default without `-c`, place the runtime config under the Pool Core project directory:

```bash
mkdir -p src/HashStormCore/configs
cp configs/config.example.json src/HashStormCore/configs/config.json
```

Important Pool Core config sections:

- `pools` - mining pool definitions.
- `persistence.postgres` - PostgreSQL connection settings used by legacy Pool Core persistence paths.
- `paymentProcessing` - payment processor settings.
- `shareRecoveryFile` - top-level share recovery fallback file.
- `eventPipeline` - event-driven handoff/WAL/Redis publisher settings.
- `poolCore.publicApiEnabled` - enables/disables the Pool Core embedded legacy API, metrics, and WebSocket host.
- `poolCore.liveStateEnabled` - controls legacy/direct in-process live-state handling. Prefer the event pipeline with LiveAggregator and ApiProvider for new frontend reads.

`configs/event-pipeline.example.json` is a focused event-pipeline example/snippet. It is not a complete mining pool config by itself.

### Sidecars

Sidecars first read the Pool Core config from `HASHSTORM_CONFIG` or `configs/config.json` and derive common defaults from it:

- Redis connection and stream name from `eventPipeline.broker`.
- live window settings from `eventPipeline.live`.
- PostgreSQL connection from `persistence.postgres`.
- LiveAggregator startup pool list from `pools[*].id`.

Flat sidecar JSON files are optional overrides under `configs/`. Environment variables override both Pool Core config-derived defaults and sidecar JSON files:

| Process | Optional config file | Environment prefix |
| --- | --- | --- |
| `HashStormCore.DbWriter` | `configs/db-writer.json` | `HASHSTORM_DBWRITER_` |
| `HashStormCore.LiveAggregator` | `configs/live-aggregator.json` | `HASHSTORM_LIVE_` |
| `HashStormCore.ApiProvider` | `configs/api-provider.json` | `HASHSTORM_API_` |

Optional override setup:

```bash
cp configs/db-writer.example.json configs/db-writer.json
cp configs/live-aggregator.example.json configs/live-aggregator.json
cp configs/api-provider.example.json configs/api-provider.json
```

DbWriter and ApiProvider require a PostgreSQL connection. If no sidecar override file is present, they derive it from `persistence.postgres` in `configs/config.json`.

## Database and Redis

A full event-pipeline deployment requires:

- PostgreSQL for durable historical/accounting data.
- Redis for Redis Streams and live read models.

For local setup, use the interactive helper:

```bash
scripts/create-db.sh
```

It prompts for PostgreSQL host, admin user, database name, database username, and passwords. Blank database name and username default to `hashstorm`. The helper creates/updates the role and database, strips the legacy `SET ROLE HashStormCore` from `createdb.sql`, and can apply both the base schema and the event-pipeline schema.

Manual base schema setup:

```bash
psql -d <database> -f src/HashStormCore/Persistence/Postgres/Scripts/createdb.sql
```

`createdb.sql` may contain:

```sql
SET ROLE HashStormCore;
```

If your local database role is different, either create the expected role or use a local temporary copy of the script without that line.

Before enabling DbWriter against an existing database, apply:

```bash
psql -d <database> -f src/HashStormCore/Persistence/Postgres/Scripts/event_pipeline.sql
```

The default `createdb.sql` creates a non-partitioned `shares` table. The PostgreSQL 11 partition appendix is only needed if you intentionally use the partitioned `shares` table.

## Local Development Startup

Prepare configs:

```bash
mkdir -p configs
cp configs/config.example.json configs/config.json
```

Edit:

- `configs/config.json` for pools, daemon RPC, wallet addresses, PostgreSQL, and event-pipeline settings.
- optional `configs/db-writer.json` for DbWriter-specific overrides.
- optional `configs/live-aggregator.json` for LiveAggregator-specific overrides.
- optional `configs/api-provider.json` for ApiProvider-specific overrides.

Run all local processes with the explicit helper:

```bash
HASHSTORM_CONFIG="$PWD/configs/config.json" scripts/run-all.sh
```

Or use the interactive startup helper:

```bash
scripts/startup.sh
```

It prompts for the Pool Core config path, uses sidecar override files only when present, optionally starts local Redis, then launches DbWriter, LiveAggregator, ApiProvider, and finally Pool Core.

For local-only experiments, the helper can start Redis:

```bash
HASHSTORM_START_REDIS=true HASHSTORM_CONFIG="$PWD/configs/config.json" scripts/run-all.sh
```

Do not use `HASHSTORM_START_REDIS=true` on a host where Redis is managed by systemd, Docker, Kubernetes, or another supervisor.

Stop helper-started processes:

```bash
scripts/stop-all.sh
```

## Manual Startup

Pool Core:

```bash
dotnet run --project src/HashStormCore/HashStormCore.csproj -- -c ./configs/config.json
```

DbWriter:

```bash
dotnet run --project src/HashStormCore.DbWriter/HashStormCore.DbWriter.csproj
```

LiveAggregator:

```bash
dotnet run --project src/HashStormCore.LiveAggregator/HashStormCore.LiveAggregator.csproj
```

ApiProvider:

```bash
dotnet run --project src/HashStormCore.ApiProvider/HashStormCore.ApiProvider.csproj
```

For production, run each process under a real supervisor such as systemd, Docker, Kubernetes, or another process manager suitable for your environment.

## API Surfaces

This repository no longer ships or serves a bundled legacy static pool website. HashStormCore exposes API endpoints only.

New frontend work should use `HashStormCore.ApiProvider`. Pool Core's embedded HTTP host remains available for compatibility and operations, but it is no longer the preferred frontend read surface.

Pool Core embedded host, when `poolCore.publicApiEnabled=true`, keeps these existing surfaces:

- `/api/...` controllers
- `/metrics`
- `/notifications` WebSocket endpoint

Treat Pool Core `/api/pools`, `/api/v2/pools`, and `/api/live` routes as legacy/deprecated compatibility routes. They are still present and are not removed by this release, but removal or disabling will be staged after frontend migration. Pool Core `/metrics` and health endpoints are operational surfaces and should not be treated as frontend APIs. Pool Core `/api/admin` endpoints have not been redesigned yet and should remain restricted to trusted operators.

ApiProvider serves frontend-facing reads without an `/api` prefix:

- `GET /live/pools/{poolId}/summary`
- `GET /live/pools/{poolId}/top-miners`
- `GET /live/pools/{poolId}/miners/{miner}`
- `GET /live/pools/{poolId}/miners/{miner}/workers`
- `GET /live/pools/{poolId}/status`
- `GET /historical/pools`
- `GET /historical/pools/{poolId}/info`
- `GET /historical/pools/{poolId}/blocks`
- `GET /historical/pools/{poolId}/payments`
- `GET /historical/pools/{poolId}/miners/{miner}/payments`
- `GET /historical/pools/{poolId}/miners/{miner}/balance`
- `GET /historical/pools/{poolId}/miners/{miner}/balance-changes`
- `GET /historical/pools/{poolId}/stats`
- `GET /historical/pools/{poolId}/stats/latest`
- `GET /historical/pools/{poolId}/miners/{miner}/stats`
- `GET /historical/pools/{poolId}/miners/{miner}/worker-stats`
- `GET /historical/pools/{poolId}/share-events`

Redis Streams are internal broker transport between Pool Core and sidecars. They are not a public frontend API.

Legacy route migration status:

| Legacy Pool Core route group | Replacement / status | Notes |
| --- | --- | --- |
| `/api/pools` and `/api/v2/pools` historical reads | ApiProvider `/historical/...` | Blocks, payments, balances, balance changes, pool stats, miner stats, worker stats, and share-events now have ApiProvider reads. Some legacy aggregate/count shapes are not full parity yet. |
| `/api/live` lite/top-miners/workers/static routes | ApiProvider `/live/...` and `/historical/pools` | Modern live reads come from Redis live models through ApiProvider. Some all-pool, round, search, and SSE shapes still need replacement if frontend requires them. |
| `/api/live` snapshots, round, search, feed | Pending LiveAggregator/ApiProvider replacement | Keep only as compatibility until equivalent live read models or frontend changes exist. |
| `/api/admin` | Admin redesign required | These are operator/admin controls, not frontend reads. They need an authenticated admin design before removal or replacement. |
| `/notifications` | Pending ApiProvider push or polling replacement | Do not expose Redis Streams directly to browsers. |
| `/metrics`, `/api/health-check`, `/api/live/health`, `/api/live/version` | Operational, keep for now | These remain useful for process operations until a separate health/metrics plan replaces them. |

## Production Notes

Linux is the recommended production environment. Windows is useful for development/debugging, but is not recommended for operating a public pool.

A realistic production deployment includes:

- Pool Core process.
- Redis with persistence/monitoring appropriate for stream and live-state workloads.
- PostgreSQL with base schema and event-pipeline tables applied.
- DbWriter process.
- LiveAggregator process.
- ApiProvider process.
- reverse proxy for TLS, routing, rate-limiting, and cache policy.
- external frontend, for example a Next.js app.

Pool Core mining latency is protected by the local in-memory handoff and WAL/outbox. Critical accepted share/block candidate events must not depend on frontend availability.

Monitor at minimum:

- Pool Core uptime, CPU, and memory.
- WAL/outbox backlog size.
- Redis stream length and pending entries.
- DbWriter lag.
- LiveAggregator freshness/warming state.
- PostgreSQL connections, slow queries, disk I/O, and autovacuum.
- Stratum port health.
- hashrate anomalies.
- worker churn.

## Security Requirements

Secure coin daemons:

- run daemons on private networks when possible.
- never expose coin daemon RPC publicly.
- use firewall rules.

Secure PostgreSQL:

- restrict access to trusted hosts only.
- use a dedicated database user.
- monitor connections, disk, slow queries, and autovacuum.

Secure Redis:

- do not expose Redis publicly.
- use `maxmemory-policy noeviction` for event-stream deployments unless you fully understand the consequences.
- monitor memory, stream length, pending entries, and persistence.

Use a reverse proxy. Recommended options:

- nginx
- Caddy
- Traefik

These provide TLS termination, rate limiting, request normalization, and cache headers for API/frontend endpoints.

## Support

There is no guaranteed commercial support for HashStormCore.

- Use at your own risk.
- Issues and PRs are welcome.
- For general pool operations, you can still refer to the original Miningcore documentation and adapt it to this fork, but prefer this repository's config examples for HashStormCore-specific runtime behavior.

## Contributions

Contributions are welcome.

- Default stable branch: `production`
- Active development branch: `dev`

See `CONTRIBUTING.md` for guidelines.

## Credits

HashStormCore stands on the shoulders of:

- Coinfoundry Miningcore
- Miningcore community forks and contributors

Without that base, this project would not exist.

## Donations

If you wish to support HashStormCore development:

Sponsorship: GitHub Sponsors coming soon.

- ETH: `TO ADD`
- BTC: `TO ADD`
