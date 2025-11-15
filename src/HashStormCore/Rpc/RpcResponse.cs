using HashStormCore.JsonRpc;

namespace HashStormCore.Rpc;

public record RpcResponse<T>(T Response, JsonRpcError Error = null);
