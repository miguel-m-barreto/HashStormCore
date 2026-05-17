using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace HashStormCore.Tests.Persistence.Postgres;

public abstract class PostgresIntegrationTestBase
{
    private static readonly string[] RequiredTables =
    {
        "payout_batches",
        "payout_intents",
        "payout_send_attempts",
        "payout_attempt_intents",
        "payout_external_confirmations",
        "payout_admin_actions"
    };

    protected static async Task WithRollbackAsync(Func<NpgsqlConnection, NpgsqlTransaction, Task> action)
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresIntegrationFactAttribute.ConnectionStringEnvironmentVariable);
        if(string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"{PostgresIntegrationFactAttribute.ConnectionStringEnvironmentVariable} is required");

        await using var con = new NpgsqlConnection(connectionString);
        await con.OpenAsync();

        await VerifyRequiredTablesAsync(con);

        await using var tx = await con.BeginTransactionAsync();
        try
        {
            await action(con, tx);
        }
        finally
        {
            await tx.RollbackAsync();
        }
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

    protected static string NewPoolId(string suffix)
    {
        return $"test_pool_{suffix}_{Guid.NewGuid():N}";
    }
}
