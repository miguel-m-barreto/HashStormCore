namespace HashStormCore.ApiProvider.Configuration;

public class ApiProviderConfig
{
    public string RedisConnectionString { get; set; } = "localhost:6379,abortConnect=false";
    public string PostgresConnectionString { get; set; } = string.Empty;
    public string ListenUrl { get; set; } = "http://127.0.0.1:4100";
}
