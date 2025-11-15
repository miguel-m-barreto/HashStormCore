using System.Net;
using HashStormCore.Configuration;

namespace HashStormCore.Stratum;

public record StratumEndpoint(IPEndPoint IPEndPoint, PoolEndpoint PoolEndpoint);
