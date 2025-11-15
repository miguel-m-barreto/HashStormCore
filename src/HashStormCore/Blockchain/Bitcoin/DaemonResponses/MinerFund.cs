using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HashStormCore.Blockchain.Bitcoin.DaemonResponses
{
    public class MinerFundTemplateExtra
    {
        public string[] Addresses { get; set; }
        public ulong MinimumValue { get; set; }
    }
}
