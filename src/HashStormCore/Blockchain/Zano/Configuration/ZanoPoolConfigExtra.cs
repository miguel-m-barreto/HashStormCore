using HashStormCore.Configuration;
using Newtonsoft.Json.Linq;

namespace HashStormCore.Blockchain.Zano.Configuration;

public class ZanoPoolConfigExtra
{
    /// <summary>
    /// Base directory for generated DAGs
    /// </summary>
    public string DagDir { get; set; }

    /// <summary>
    /// Blocktemplate stream published via ZMQ
    /// </summary>
    public ZmqPubSubEndpointConfig BtStream { get; set; }
}
