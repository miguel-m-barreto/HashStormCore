# HashStormCore Live API

This document is a compatibility and migration reference for Pool Core's embedded `/api/live` routes. These routes still exist when the Pool Core embedded API is enabled, but they are legacy/deprecated frontend-read routes.

New frontend work should use `HashStormCore.ApiProvider`:

- modern live reads: `/live/...`
- historical/accounting reads: `/historical/...`

Some Pool Core `/api/live` snapshot, round, search, and feed shapes do not yet have full ApiProvider parity. They remain documented here for migration/reference only; do not build new frontend dependencies on them.

Redis Streams are internal transport between Pool Core, DbWriter, and LiveAggregator. They must not be exposed directly to browser clients.

Pool Core `/api/live` uses in-process structures (`LiveHashrateState`, `LiveRoundState`) to:

- avoid unnecessary database queries  
- allow aggressive polling (1-10 s)  
- provide consistent metrics per pool, per address, and per address.worker

---

## 1. Classic Stats API (Miningcore-Compatible)

The classic Pool Core API (usually on port `4000`) remains available for compatibility:

- pool list  
- basic pool stats  
- blocks  
- payments  
- balances  

Original documentation:  
https://github.com/oliverw/miningcore/wiki/API

HashStormCore has not removed these endpoints, but new frontend work should use ApiProvider where replacements exist.

Legacy route migration status:

| Pool Core route group | Replacement / status | Notes |
| --- | --- | --- |
| `/api/live/pools/{poolId}/top-miners-lite` | ApiProvider `GET /live/pools/{poolId}/top-miners` | Modern read comes from Redis live models. |
| `/api/live/pools/{poolId}/miners/{address}/workers-lite` | ApiProvider `GET /live/pools/{poolId}/miners/{miner}/workers` | Modern read comes from Redis live models. |
| `/api/live/pools/static-lite` | ApiProvider `GET /historical/pools` | ApiProvider returns a sanitized config projection, not raw Pool Core config. |
| `/api/live/pools/{poolId}/snapshot-lite` and related snapshot routes | Partial ApiProvider `/live` coverage | Exact snapshot/round/network shapes are pending LiveAggregator/ApiProvider replacement if still needed. |
| `/api/live/pools/{poolId}/round`, `/round-lite` | Pending replacement | Round/luck fields remain Pool Core-local in this legacy API. |
| `/api/live/miners/search-lite` | Pending replacement | Requires a Redis-backed search/read-model decision. |
| `/api/live/pools/{poolId}/feed` | Pending push or polling replacement | Do not expose Redis Streams directly as a replacement. |
| `/api/live/health`, `/api/live/version` | Operational, keep for now | These are process/ops surfaces, not frontend data APIs. |

---

## 2. Pool Core Live API Overview

Endpoint categories:

- **HEAVY** - LIVE + DB (network difficulty, pendingShares, blockHeight)  
- **LITE** - LIVE-only (zero DB; best performance)  
- **SSE** - Server-Sent Events (continuous streaming)

Common parameters:

- `windowSec` - hashrate calculation window (default: 600s)  
- `limit` - maximum results (default: 100, max: 500)  
- `page` / `pageSize` - pagination  

---

## 3. Endpoint Index

### HEAVY (LIVE + DB)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/live/pools/snapshot` | Full live + network snapshot for all pools |
| GET | `/api/live/pools/{poolId}/snapshot` | Full snapshot for a single pool |
| GET | `/api/live/pools/snapinfo` | Compact SnapInfo for all pools |
| GET | `/api/live/pools/{poolId}/snapinfo` | Detailed SnapInfo for one pool |
| GET | `/api/live/status` | Cluster status (hashrate, miners, network) |
| GET | `/api/live/pools/{poolId}/round` | Current round state |
| GET | `/api/live/pools/{poolId}/miners` | Top miners + pendingShares (DB) |
| GET | `/api/live/pools/{poolId}/miners-all` | All miners, paginated, with pendingShares |
| GET | `/api/live/pools/{poolId}/miners/{address}/snapshot` | Live snapshot for a miner |

---

### LITE (LIVE-only)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/live/pools/static-lite` | Static pool configuration |
| GET | `/api/live/status-lite` | Cluster live-only status |
| GET | `/api/live/pools/snapshot-lite` | Live-only snapshot of all pools |
| GET | `/api/live/pools/{poolId}/snapshot-lite` | Live-only snapshot of a single pool |
| GET | `/api/live/pools/{poolId}/online-lite` | Online miners/workers in a pool |
| GET | `/api/live/pools/online-lite` | Online miners/workers in the cluster |
| GET | `/api/live/miners/search-lite` | Global address search |
| GET | `/api/live/pools/{poolId}/top-miners-lite` | Live-only top miners |
| GET | `/api/live/pools/{poolId}/miners-lite` | Limited miner list (live-only) |
| GET | `/api/live/pools/{poolId}/miners-all-lite` | All miners, live-only (paginated) |
| GET | `/api/live/pools/{poolId}/miners/{address}/round-lite` | Round metrics for a miner |
| GET | `/api/live/pools/{poolId}/miners/{address}/workers-lite` | Workers belonging to an address |

---

### SSE

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/live/pools/{poolId}/feed` | Continuous SSE hashrate stream |

---

## 4. HEAVY Endpoints (LIVE + DB)

### 4.1 Snapshots

#### `GET /api/live/pools/snapshot`
Full snapshot for all pools.

Includes:
- live: `currentHashrate`, `sharesPerSec`, `minersOnline`, `round.actualShares`  
- DB: `network.height`, `network.difficulty`, `network.hashrate`

Query:
- `windowSec?`

---

#### `GET /api/live/pools/{poolId}/snapshot`
Same structure for a single pool.

---

#### `GET /api/live/pools/snapinfo`
Compact SnapInfo for all pools:

Includes:
- coin metadata  
- pool static config  
- live metrics  
- network stats  
- round state  

---

#### `GET /api/live/pools/{poolId}/snapinfo`
Single-pool version.

---

#### `GET /api/live/status`
Cluster status (LIVE + DB):

- `poolId`
- `algo`
- `unit`
- `currentHashrate`
- `minersOnline`
- `difficulty` / `blockHeight` (DB-backed)

---

### 4.2 Round

#### `GET /api/live/pools/{poolId}/round`
Current round state:

- `height`
- `startedAt`
- `actualShares` (live)
- `expectedShares` (from difficulty)
- `luckPercent`

---

### 4.3 Miners (DB-backed)

#### `GET /api/live/pools/{poolId}/miners`
Top miners with `pendingShares`.

Query:
- `windowSec`
- `limit`

---

#### `GET /api/live/pools/{poolId}/miners-all`
All miners, with pagination.

Query:
- `windowSec`
- `page`
- `pageSize`

---

#### `GET /api/live/pools/{poolId}/miners/{address}/snapshot`
Live-only snapshot for a miner:

- `hashrate`
- `sharesPerSec`
- `online`
- `lastShareAt`
- `unit`
- `windowSec`

---

## 5. LITE Endpoints (LIVE-only)

### 5.1 Cluster & Pools

#### `GET /api/live/pools/static-lite`
Static configuration for UI.

---

#### `GET /api/live/status-lite`
Live-only cluster status.

---

#### `GET /api/live/pools/snapshot-lite`
Live-only snapshot for all pools.

---

#### `GET /api/live/pools/{poolId}/snapshot-lite`
Live-only snapshot for a single pool.

---

### 5.2 Online Counters

#### `GET /api/live/pools/{poolId}/online-lite`
Online miners/workers.

Query:
- `mode=window|live`
- `windowSec?`

---

#### `GET /api/live/pools/online-lite`
Cluster-wide version.

---

### 5.3 Miners (address-level)

#### `GET /api/live/miners/search-lite`
Global address search.

Query:
- `q`
- `limit`
- `windowSec`

---

#### `GET /api/live/pools/{poolId}/top-miners-lite`
Live-only top miners.

---

#### `GET /api/live/pools/{poolId}/miners-lite`
Limited miner list (live-only).

---

#### `GET /api/live/pools/{poolId}/miners-all-lite`
All miners live-only, paginated.

---

#### `GET /api/live/pools/{poolId}/miners/{address}/round-lite`
Round metrics + miner view.

---

### 5.4 Workers

#### `GET /api/live/pools/{poolId}/miners/{address}/workers-lite`
Workers under an address:

- `worker`
- `hashrate`
- `sharesPerSecond`
- `online`
- `lastShareAt`

---

## 6. SSE

### `GET /api/live/pools/{poolId}/feed`

Continuous stream:

- `poolId`
- `asOf`
- `currentHashrate`
- `windowSec`
- `unit`

Query:
- `intervalSec`
- `windowSec?`

Ideal for real-time charts.

---

## 7. Migration Guidance

- Use ApiProvider `/live/...` for new dashboard reads.
- Use ApiProvider `/historical/...` for historical/accounting reads.
- Treat **LITE**, **HEAVY**, and **SSE** routes in this document as legacy compatibility surfaces.
- Keep using Pool Core `/api/live` routes only where a current frontend still needs an exact snapshot, round, search, or feed shape that has not been migrated yet.
- Do not expose Redis Streams directly to browsers as a replacement for `/api/live` or SSE.

---
