using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace HashStormCore.Tests.Util;

public class Globals
{
    public static readonly JsonSerializerSettings JsonSerializerSettings = new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };
}
