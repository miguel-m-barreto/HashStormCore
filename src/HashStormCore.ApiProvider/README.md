# HashStormCore.ApiProvider

Provides website-facing reads outside the Pool Core process. Live endpoints read Redis only; historical endpoints read PostgreSQL only.

## Historical Endpoints

New historical list endpoints are paginated with `limit` and `offset`. The default limit is `50`, the maximum limit is `500`, and negative offsets are rejected. Date windows on the new endpoints use `created >= from` and `created < to`. The share-events endpoint keeps its original inclusive `to` behavior and allows up to `1000` rows per page for compatibility.

- `GET /historical/pools/{poolId}/blocks`
- `GET /historical/pools/{poolId}/share-events?eventType={eventType}&miner={miner}&worker={worker}`
- `GET /historical/pools/{poolId}/payments`
- `GET /historical/pools/{poolId}/miners/{miner}/payments`
- `GET /historical/pools/{poolId}/miners/{miner}/balance`
- `GET /historical/pools/{poolId}/miners/{miner}/balance-changes`
- `GET /historical/pools/{poolId}/stats`
- `GET /historical/pools/{poolId}/miners/{miner}/stats`
- `GET /historical/pools/{poolId}/miners/{miner}/worker-stats?worker={worker}`
- `GET /historical/pools`
- `GET /historical/pools/{poolId}/info`

Pool stats fetch the latest matching page first, then return those points ordered ascending by `created` for chart-friendly responses. The existing `GET /historical/pools/{poolId}/stats/latest` endpoint is preserved for latest-point reads.

Share-events responses expose a typed historical event projection and omit internal or privacy-sensitive fields such as IP address, user agent, inserted-at bookkeeping, and raw internal error messages.

Miner stats read raw PostgreSQL `minerstats` rows. HashStormCore currently persists worker-level rows, not normal miner aggregate rows, so `GET /historical/pools/{poolId}/miners/{miner}/stats` includes a `worker` field and may return multiple rows per timestamp.

Pool info endpoints return a strict sanitized config projection: pool id, enabled flag, coin id, pool fee percent, public port settings, and payment minimum/scheme. They do not return raw cluster config, daemon endpoints, wallet addresses, credentials, database/Redis connection strings, private keys, ZMQ/CURVE settings, or arbitrary extension data.
