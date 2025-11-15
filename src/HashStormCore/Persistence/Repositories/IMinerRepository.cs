using System.Data;
using HashStormCore.Persistence.Model;

namespace HashStormCore.Persistence.Repositories;

public interface IMinerRepository
{
    Task<MinerSettings> GetSettingsAsync(IDbConnection con, IDbTransaction tx, string poolId, string address);
    Task UpdateSettingsAsync(IDbConnection con, IDbTransaction tx, MinerSettings settings);
}
