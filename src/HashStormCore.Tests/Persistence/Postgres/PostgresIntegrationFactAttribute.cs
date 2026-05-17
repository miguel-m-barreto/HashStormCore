using System;
using Xunit;

namespace HashStormCore.Tests.Persistence.Postgres;

public class PostgresIntegrationFactAttribute : FactAttribute
{
    public const string ConnectionStringEnvironmentVariable = "HASHSTORM_TEST_POSTGRES_CONNECTION_STRING";

    public PostgresIntegrationFactAttribute()
    {
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)))
            Skip = $"Set {ConnectionStringEnvironmentVariable} to run PostgreSQL integration tests";
    }
}
