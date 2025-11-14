´live_api.md


## API

HashStormCore exposes **two** API layers:

1. **Classic Stats API** (inherited from Miningcore)  
2. **HashStormCore Live API** (new, in-memory, low-latency)

### 1. Classic Stats API (Miningcore-compatible)

The legacy HTTP API (typically on port `4000`) is kept for compatibility:

- Pool list  
- Basic stats per pool  
- Blocks, payments, balances, etc.  

For full reference, see the original Miningcore documentation and adapt paths/configs as needed:

- Miningcore API docs: `https://github.com/oliverw/miningcore/wiki/API`

HashStormCore keeps these endpoints to avoid breaking existing tooling and frontends.

---

### 2. HashStormCore Live API (in-memory metrics)

All live endpoints are mounted under:

```text
/api/live/...
```

They are built on top of the in-memory LiveHashrateState and LiveRoundState structures, and are designed to:

- avoid DB hits whenever possible
- serve UIs with low latency
- work well with short polling intervals (1-10 s)

There are three categories:

- HEAVY - Live + DB (network difficulty, pending shares, etc.)
- LITE - Live-only (no DB, UI-friendly)
- SSE - Server-Sent Events streaming

### 2.1 HEAVY endpoints (LIVE + DB)

**Use live rings for hashrate plus PostgreSQL for network stats and accounting.**

*Pool & cluster snapshots*

Snapshot for all enabled pools (live hashrate + DB network stats).
```
GET /api/live/pools/snapshot
```

Snapshot for a single pool.
```
GET /api/live/pools/{poolId}/snapshot
```

"SnapInfo" view for all pools (live hashrate, live round, miners online, network stats).
```
GET /api/live/pools/snapinfo
```

"SnapInfo" for a single pool.
```
GET /api/live/pools/{poolId}/snapinfo
```

Cluster status (per-pool current hashrate, miners online, network difficulty & height).
```
GET /api/live/status
```

*Round information*

Current round state: live actualShares + expected shares via network difficulty.
```
GET /api/live/pools/{poolId}/round
```

Miners (address-level, with DB stats)

Top miners for a pool (live hashrate + pendingShares from DB, limited list).
```
GET /api/live/pools/{poolId}/miners
```

All miners for a pool, with pagination + pendingShares from DB.
```
GET /api/live/pools/{poolId}/miners-all
```

*Single miner snapshot*

Live snapshot for a given address (hashrate, last share, online state).
```
GET /api/live/pools/{poolId}/miners/{address}/snapshot
```

## 2.2 LITE endpoints (LIVE-only, zero DB)

**These are tuned for frontends. They never touch PostgreSQL.**

*Cluster & pools*

Static configuration for enabled pools (coin metadata, ports, payout config).
```
GET /api/live/pools/static-lite
```

Cluster-wide, live-only view: hashrate + online miners per pool.
```
GET /api/live/status-lite
```

Live snapshot for all pools (hashrate, shares/s, miners online, round actualShares).
```
GET /api/live/pools/snapshot-lite
```

Live snapshot for a single pool (no DB).
```
GET /api/live/pools/{poolId}/snapshot-lite
```

*Online counters*

Online miners/workers for one pool.
```
GET /api/live/pools/{poolId}/online-lite?mode=window|live&windowSec=...
```

mode=window - uses in-memory window presence (recommended)

mode=live - uses poolInst.Stats when available

Same as above, but aggregated for all pools.
```
GET /api/live/pools/online-lite?mode=window|live&windowSec=...
```

*Miners (address-level)*

Search across all pools by address (substring match, live only).
```
GET /api/live/miners/search-lite?q=...
```

Top miners for a pool, live-only (address, hashrate, online, last share).
```
GET /api/live/pools/{poolId}/top-miners-lite
```

Miners for a pool (limited list, live-only, no pendingShares).
```
GET /api/live/pools/{poolId}/miners-lite
```

All miners for a pool, paginated, live-only.
```
GET /api/live/pools/{poolId}/miners-all-lite
```

Live hashrate + current round state for a specific address.
```
GET /api/live/pools/{poolId}/miners/{address}/round-lite
```

*Workers (address.worker-level)*

All workers under a given address:
```
GET /api/live/pools/{poolId}/miners/{address}/workers-lite
```

- live hashrate per worker
- online state
- last share timestamp
- effective window used for the calculation

## 2.3 SSE (Server-Sent Events)

**Streaming pool hashrate**

```
GET /api/live/pools/{poolId}/feed?intervalSec=2&windowSec=...
```

*Pushes a continuous SSE stream with:*

- poolId
- asOf (ISO timestamp)
- unit (H/s or Sol/s)
- windowSec
- currentHashrate

Designed for lightweight real-time charts without constant full HTTP polling.

---