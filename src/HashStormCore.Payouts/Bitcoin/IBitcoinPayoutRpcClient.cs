namespace HashStormCore.Payouts.Bitcoin;

public interface IBitcoinPayoutRpcClient
{
    Task<BitcoinPayoutRpcResult> SendManyAsync(BitcoinPayoutSendManyRequest request, CancellationToken ct);
    Task<BitcoinPayoutRpcResult> SendToAddressAsync(BitcoinPayoutSendToAddressRequest request, CancellationToken ct);
}
