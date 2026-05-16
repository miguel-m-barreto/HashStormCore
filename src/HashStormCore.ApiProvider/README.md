# HashStormCore.ApiProvider

Provides website-facing reads outside the Pool Core process. Live endpoints read Redis only; historical endpoints read PostgreSQL only.

## Historical Endpoints

New historical list endpoints are paginated with `limit` and `offset`. The default limit is `50`, the maximum limit is `500`, and negative offsets are rejected. Date windows on the new endpoints use `created >= from` and `created < to`. The existing share-events endpoint keeps its original inclusive `to` behavior.

- `GET /historical/pools/{poolId}/blocks`
- `GET /historical/pools/{poolId}/payments`
- `GET /historical/pools/{poolId}/miners/{miner}/payments`
- `GET /historical/pools/{poolId}/miners/{miner}/balance`
- `GET /historical/pools/{poolId}/miners/{miner}/balance-changes`
- `GET /historical/pools/{poolId}/stats`

Pool stats fetch the latest matching page first, then return those points ordered ascending by `created` for chart-friendly responses. The existing `GET /historical/pools/{poolId}/stats/latest` endpoint is preserved for latest-point reads.
