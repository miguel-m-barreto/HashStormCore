using Dapper;
using Npgsql;

namespace HashStormCore.ApiProvider.Services;

public class PostgresHistoricalReadService
{
    public PostgresHistoricalReadService(string connectionString)
    {
        this.connectionString = connectionString;
    }

    private readonly string connectionString;

    public async Task<object> GetLatestPoolStatsAsync(string poolId, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(connectionString))
            return null;

        await using var con = new NpgsqlConnection(connectionString);
        const string query = "SELECT * FROM poolstats WHERE poolid = @poolId ORDER BY created DESC FETCH NEXT 1 ROWS ONLY";
        return await con.QueryFirstOrDefaultAsync(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<object>> GetShareEventsAsync(string poolId, DateTime? from, DateTime? to, int limit, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(connectionString))
            return Array.Empty<object>();

        await using var con = new NpgsqlConnection(connectionString);
        const string query = @"SELECT * FROM share_events
            WHERE pool_id = @poolId
              AND (@from IS NULL OR created >= @from)
              AND (@to IS NULL OR created <= @to)
            ORDER BY created DESC
            LIMIT @limit";

        var rows = await con.QueryAsync<object>(new CommandDefinition(query, new
        {
            poolId,
            from,
            to,
            limit = Math.Clamp(limit, 1, 1000)
        }, cancellationToken: ct));

        return rows.ToArray();
    }
}
