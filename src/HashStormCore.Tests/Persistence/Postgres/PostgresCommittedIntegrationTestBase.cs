using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace HashStormCore.Tests.Persistence.Postgres;

public abstract class PostgresCommittedIntegrationTestBase
{
    protected const string TestPoolPrefix = "executor_commit_test_";

    private static readonly string[] RequiredTables =
    {
        "payout_batches",
        "payout_intents",
        "payout_send_attempts",
        "payout_attempt_intents",
        "payout_external_confirmations",
        "payout_admin_actions"
    };

    protected static async Task WithCommittedCleanupAsync(string poolId, Func<NpgsqlConnection, Task> action)
    {
        RequireTestPool(poolId);

        var connectionString = GetConnectionString();

        await using var con = new NpgsqlConnection(connectionString);
        await con.OpenAsync();

        await VerifyRequiredTablesAsync(con);

        try
        {
            await action(con);
        }
        finally
        {
            await CleanupPoolAsync(con, poolId);
        }
    }

    protected static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresIntegrationFactAttribute.ConnectionStringEnvironmentVariable);
        if(string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"{PostgresIntegrationFactAttribute.ConnectionStringEnvironmentVariable} is required");

        return connectionString;
    }

    protected static string NewCommittedPoolId(string suffix)
    {
        return $"{TestPoolPrefix}{suffix}_{Guid.NewGuid():N}";
    }

    private static async Task CleanupPoolAsync(NpgsqlConnection con, string poolId)
    {
        RequireTestPool(poolId);

        await con.ExecuteAsync("DELETE FROM payout_external_confirmations WHERE poolid = @poolid", new { poolid = poolId });
        await con.ExecuteAsync("DELETE FROM payout_admin_actions WHERE poolid = @poolid", new { poolid = poolId });
        await con.ExecuteAsync("DELETE FROM payout_attempt_intents WHERE poolid = @poolid", new { poolid = poolId });
        await con.ExecuteAsync("DELETE FROM payout_send_attempts WHERE poolid = @poolid", new { poolid = poolId });
        await con.ExecuteAsync("DELETE FROM payout_intents WHERE poolid = @poolid", new { poolid = poolId });
        await con.ExecuteAsync("DELETE FROM payout_batches WHERE poolid = @poolid", new { poolid = poolId });
        await con.ExecuteAsync("DELETE FROM payments WHERE poolid = @poolid", new { poolid = poolId });
        await con.ExecuteAsync("DELETE FROM balance_changes WHERE poolid = @poolid", new { poolid = poolId });
        await con.ExecuteAsync("DELETE FROM balances WHERE poolid = @poolid", new { poolid = poolId });
    }

    private static void RequireTestPool(string poolId)
    {
        if(string.IsNullOrWhiteSpace(poolId) || !poolId.StartsWith(TestPoolPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Committed PostgreSQL tests may only clean exact pool ids with prefix {TestPoolPrefix}");
    }

    private static async Task VerifyRequiredTablesAsync(NpgsqlConnection con)
    {
        const string query = @"SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = current_schema() AND table_name = ANY(@tables)";

        var found = (await con.QueryAsync<string>(query, new { tables = RequiredTables })).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RequiredTables.Where(x => !found.Contains(x)).ToArray();

        if(missing.Length > 0)
            throw new InvalidOperationException($"PostgreSQL test database is missing required payout tables: {string.Join(", ", missing)}");
    }
}
