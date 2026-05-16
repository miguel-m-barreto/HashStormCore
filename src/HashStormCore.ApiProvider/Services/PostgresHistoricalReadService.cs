using Dapper;
using Npgsql;
using System.Text;

namespace HashStormCore.ApiProvider.Services;

public class PostgresHistoricalReadService
{
    public const int DefaultPageLimit = 50;
    public const int MaxPageLimit = 500;

    public PostgresHistoricalReadService(string connectionString)
    {
        this.connectionString = connectionString;
    }

    private readonly string connectionString;

    public async Task<HistoricalPoolStatsDto> GetLatestPoolStatsAsync(string poolId, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(connectionString))
            return null;

        await using var con = new NpgsqlConnection(connectionString);
        const string query = @"
            SELECT id AS Id, poolid AS PoolId, connectedminers AS ConnectedMiners, poolhashrate AS PoolHashrate,
                sharespersecond AS SharesPerSecond, networkhashrate AS NetworkHashrate, networkdifficulty AS NetworkDifficulty,
                lastnetworkblocktime AS LastNetworkBlockTime, blockheight AS BlockHeight, connectedpeers AS ConnectedPeers,
                created AS Created
            FROM poolstats
            WHERE poolid = @poolId
            ORDER BY created DESC, id DESC
            FETCH NEXT 1 ROWS ONLY";

        return await con.QueryFirstOrDefaultAsync<HistoricalPoolStatsDto>(
            new CommandDefinition(query, new { poolId }, cancellationToken: ct));
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

    public async Task<IReadOnlyList<HistoricalBlockDto>> GetBlocksAsync(string poolId, string status, string type, string miner,
        DateTime? from, DateTime? to, int limit, int offset, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(connectionString))
            return Array.Empty<HistoricalBlockDto>();

        var parameters = CreatePagedParameters(poolId, limit, offset);
        var filters = CreateBaseFilters(parameters, from, to);
        AddOptionalFilter(filters, parameters, "status = @status", "status", status);
        AddOptionalFilter(filters, parameters, "type = @type", "type", type);
        AddOptionalFilter(filters, parameters, "miner = @miner", "miner", miner);

        await using var con = new NpgsqlConnection(connectionString);
        var query = $@"
            SELECT id AS Id, poolid AS PoolId, blockheight AS BlockHeight, networkdifficulty AS NetworkDifficulty,
                status AS Status, type AS Type, confirmationprogress AS ConfirmationProgress, effort AS Effort,
                minereffort AS MinerEffort, transactionconfirmationdata AS TransactionConfirmationData,
                miner AS Miner, reward AS Reward, source AS Source, hash AS Hash, created AS Created
            FROM blocks
            {BuildWhereClause(filters)}
            ORDER BY created DESC, id DESC
            LIMIT @limit OFFSET @offset";

        var rows = await con.QueryAsync<HistoricalBlockDto>(new CommandDefinition(query, parameters, cancellationToken: ct));

        return rows.ToArray();
    }

    public async Task<IReadOnlyList<HistoricalPaymentDto>> GetPaymentsAsync(string poolId, string address, string coin,
        DateTime? from, DateTime? to, int limit, int offset, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(connectionString))
            return Array.Empty<HistoricalPaymentDto>();

        var parameters = CreatePagedParameters(poolId, limit, offset);
        var filters = CreateBaseFilters(parameters, from, to);
        AddOptionalFilter(filters, parameters, "address = @address", "address", address);
        AddOptionalFilter(filters, parameters, "coin = @coin", "coin", coin);

        await using var con = new NpgsqlConnection(connectionString);
        var query = $@"
            SELECT id AS Id, poolid AS PoolId, coin AS Coin, address AS Address, amount AS Amount,
                transactionconfirmationdata AS TransactionConfirmationData, created AS Created
            FROM payments
            {BuildWhereClause(filters)}
            ORDER BY created DESC, id DESC
            LIMIT @limit OFFSET @offset";

        var rows = await con.QueryAsync<HistoricalPaymentDto>(new CommandDefinition(query, parameters, cancellationToken: ct));

        return rows.ToArray();
    }

    public Task<IReadOnlyList<HistoricalPaymentDto>> GetMinerPaymentsAsync(string poolId, string miner,
        DateTime? from, DateTime? to, int limit, int offset, CancellationToken ct) =>
        GetPaymentsAsync(poolId, miner, null, from, to, limit, offset, ct);

    public async Task<HistoricalBalanceDto> GetMinerBalanceAsync(string poolId, string miner, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(connectionString))
            return null;

        await using var con = new NpgsqlConnection(connectionString);
        const string query = @"
            SELECT poolid AS PoolId, address AS Address, amount AS Amount, created AS Created, updated AS Updated
            FROM balances
            WHERE poolid = @poolId
              AND address = @miner";

        return await con.QueryFirstOrDefaultAsync<HistoricalBalanceDto>(
            new CommandDefinition(query, new { poolId, miner }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<HistoricalBalanceChangeDto>> GetMinerBalanceChangesAsync(string poolId, string miner,
        DateTime? from, DateTime? to, string usage, string tag, int limit, int offset, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(connectionString))
            return Array.Empty<HistoricalBalanceChangeDto>();

        var parameters = CreatePagedParameters(poolId, limit, offset);
        parameters.Add("miner", miner);

        var filters = CreateBaseFilters(parameters, from, to);
        filters.Add("address = @miner");
        AddOptionalFilter(filters, parameters, "usage = @usage", "usage", usage);
        AddOptionalFilter(filters, parameters, "tags @> ARRAY[@tag]::text[]", "tag", tag);

        await using var con = new NpgsqlConnection(connectionString);
        var query = $@"
            SELECT id AS Id, poolid AS PoolId, address AS Address, amount AS Amount, usage AS Usage,
                tags AS Tags, created AS Created
            FROM balance_changes
            {BuildWhereClause(filters)}
            ORDER BY created DESC, id DESC
            LIMIT @limit OFFSET @offset";

        var rows = await con.QueryAsync<HistoricalBalanceChangeDto>(new CommandDefinition(query, parameters, cancellationToken: ct));

        return rows.ToArray();
    }

    public async Task<IReadOnlyList<HistoricalPoolStatsDto>> GetPoolStatsAsync(string poolId, DateTime? from, DateTime? to,
        int limit, int offset, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(connectionString))
            return Array.Empty<HistoricalPoolStatsDto>();

        var parameters = CreatePagedParameters(poolId, limit, offset);
        var filters = CreateBaseFilters(parameters, from, to);

        await using var con = new NpgsqlConnection(connectionString);
        var query = $@"
            SELECT *
            FROM (
                SELECT id AS Id, poolid AS PoolId, connectedminers AS ConnectedMiners, poolhashrate AS PoolHashrate,
                    sharespersecond AS SharesPerSecond, networkhashrate AS NetworkHashrate, networkdifficulty AS NetworkDifficulty,
                    lastnetworkblocktime AS LastNetworkBlockTime, blockheight AS BlockHeight, connectedpeers AS ConnectedPeers,
                    created AS Created
                FROM poolstats
                {BuildWhereClause(filters)}
                ORDER BY created DESC, id DESC
                LIMIT @limit OFFSET @offset
            ) recent_stats
            ORDER BY Created ASC, Id ASC";

        var rows = await con.QueryAsync<HistoricalPoolStatsDto>(new CommandDefinition(query, parameters, cancellationToken: ct));

        return rows.ToArray();
    }

    private static DynamicParameters CreatePagedParameters(string poolId, int limit, int offset)
    {
        var parameters = new DynamicParameters();
        parameters.Add("poolId", poolId);
        parameters.Add("limit", NormalizeLimit(limit));
        parameters.Add("offset", offset);

        return parameters;
    }

    private static List<string> CreateBaseFilters(DynamicParameters parameters, DateTime? from, DateTime? to)
    {
        var filters = new List<string> { "poolid = @poolId" };

        if(from.HasValue)
        {
            filters.Add("created >= @from");
            parameters.Add("from", from);
        }

        if(to.HasValue)
        {
            filters.Add("created < @to");
            parameters.Add("to", to);
        }

        return filters;
    }

    private static void AddOptionalFilter(List<string> filters, DynamicParameters parameters, string sqlFragment, string parameterName, string value)
    {
        var normalizedValue = NormalizeFilter(value);
        if(normalizedValue == null)
            return;

        filters.Add(sqlFragment);
        parameters.Add(parameterName, normalizedValue);
    }

    private static string BuildWhereClause(IEnumerable<string> filters)
    {
        var builder = new StringBuilder("WHERE ");
        builder.AppendJoin($"{Environment.NewLine}              AND ", filters);
        return builder.ToString();
    }

    public static int NormalizeLimit(int limit) =>
        Math.Clamp(limit <= 0 ? DefaultPageLimit : limit, 1, MaxPageLimit);

    private static string NormalizeFilter(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public class HistoricalBlockDto
{
    public long Id { get; set; }
    public string PoolId { get; set; }
    public long BlockHeight { get; set; }
    public double NetworkDifficulty { get; set; }
    public string Status { get; set; }
    public string Type { get; set; }
    public double ConfirmationProgress { get; set; }
    public double? Effort { get; set; }
    public double? MinerEffort { get; set; }
    public string TransactionConfirmationData { get; set; }
    public string Miner { get; set; }
    public decimal? Reward { get; set; }
    public string Source { get; set; }
    public string Hash { get; set; }
    public DateTime Created { get; set; }
}

public class HistoricalPaymentDto
{
    public long Id { get; set; }
    public string PoolId { get; set; }
    public string Coin { get; set; }
    public string Address { get; set; }
    public decimal Amount { get; set; }
    public string TransactionConfirmationData { get; set; }
    public DateTime Created { get; set; }
}

public class HistoricalBalanceDto
{
    public string PoolId { get; set; }
    public string Address { get; set; }
    public decimal Amount { get; set; }
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
}

public class HistoricalBalanceChangeDto
{
    public long Id { get; set; }
    public string PoolId { get; set; }
    public string Address { get; set; }
    public decimal Amount { get; set; }
    public string Usage { get; set; }
    public string[] Tags { get; set; }
    public DateTime Created { get; set; }
}

public class HistoricalPoolStatsDto
{
    public long Id { get; set; }
    public string PoolId { get; set; }
    public int ConnectedMiners { get; set; }
    public double PoolHashrate { get; set; }
    public double SharesPerSecond { get; set; }
    public double NetworkHashrate { get; set; }
    public double NetworkDifficulty { get; set; }
    public DateTime? LastNetworkBlockTime { get; set; }
    public long BlockHeight { get; set; }
    public int ConnectedPeers { get; set; }
    public DateTime Created { get; set; }
}
