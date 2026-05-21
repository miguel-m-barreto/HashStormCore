using System.Data;

namespace HashStormCore.Persistence;

public interface IConnectionFactory
{
    Task<IDbConnection> OpenConnectionAsync();
}
