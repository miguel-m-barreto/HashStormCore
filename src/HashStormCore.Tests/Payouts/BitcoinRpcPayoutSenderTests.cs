using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Payouts.Bitcoin;
using HashStormCore.Payouts.Profiles;
using HashStormCore.Persistence.Model;
using Xunit;

namespace HashStormCore.Tests.Payouts;

public class BitcoinRpcPayoutSenderTests
{
    [Fact]
    public void Constructor_NullClientThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new BitcoinRpcPayoutSender(null));
    }

    [Fact]
    public async Task SendAsync_NullContextThrows()
    {
        var sender = new BitcoinRpcPayoutSender(new FakeBitcoinPayoutRpcClient());

        await Assert.ThrowsAsync<ArgumentNullException>(() => sender.SendAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_MissingBatchAttemptOrIntentsFailsPreAcceptWithoutClientCall()
    {
        var client = new FakeBitcoinPayoutRpcClient();
        var sender = new BitcoinRpcPayoutSender(client);

        var missingBatch = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)) with { Batch = null },
            CancellationToken.None);
        var missingAttempt = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)) with { Attempt = null },
            CancellationToken.None);
        var missingIntents = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)) with { Intents = null },
            CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, missingBatch.Status);
        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, missingAttempt.Status);
        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, missingIntents.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidContextErrorCode, missingBatch.ErrorCode);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidContextErrorCode, missingAttempt.ErrorCode);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidContextErrorCode, missingIntents.ErrorCode);
        Assert.Equal(0, client.TotalCallCount);
    }

    [Fact]
    public async Task SendAsync_SendManyValidBatchCallsClientOnce()
    {
        var client = new FakeBitcoinPayoutRpcClient
        {
            SendManyResult = BitcoinPayoutRpcResult.Accepted("txid-sendmany")
        };
        var sender = new BitcoinRpcPayoutSender(client);

        var result = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m), Intent("addr-b", 2m)),
            CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, result.Status);
        Assert.Equal(1, client.SendManyCallCount);
        Assert.Equal(0, client.SendToAddressCallCount);
        Assert.Equal("pool-a", client.LastSendManyRequest.PoolId);
        Assert.Equal("bitcoin", client.LastSendManyRequest.Coin);
        Assert.Equal(10, client.LastSendManyRequest.BatchId);
        Assert.Equal(20, client.LastSendManyRequest.AttemptId);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendMany, client.LastSendManyRequest.Method);
        Assert.Equal(2, client.LastSendManyRequest.Recipients.Count);
        Assert.Equal(1m, client.LastSendManyRequest.Recipients["addr-a"]);
        Assert.Equal(2m, client.LastSendManyRequest.Recipients["addr-b"]);
    }

    [Fact]
    public async Task SendAsync_SendManyAggregatesDuplicateAddresses()
    {
        var client = new FakeBitcoinPayoutRpcClient
        {
            SendManyResult = BitcoinPayoutRpcResult.Accepted("txid-duplicates")
        };
        var sender = new BitcoinRpcPayoutSender(client);

        var result = await sender.SendAsync(SendManyContext(Intent("addr-a", 1.25m), Intent("addr-a", 2.75m)),
            CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, result.Status);
        Assert.Equal(1, client.LastSendManyRequest.Recipients.Count);
        Assert.Equal(4.00m, client.LastSendManyRequest.Recipients["addr-a"]);
    }

    [Fact]
    public async Task SendAsync_SendManyAcceptedTxIdReturnsTxIdEvidenceOnly()
    {
        var sender = new BitcoinRpcPayoutSender(new FakeBitcoinPayoutRpcClient
        {
            SendManyResult = BitcoinPayoutRpcResult.Accepted("txid-accepted")
        });

        var result = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, result.Status);
        Assert.Equal(PayoutExternalConfirmationKinds.TxId, result.Evidence.Kind);
        Assert.Equal("txid-accepted", result.Evidence.Value);
        Assert.Empty(result.AdditionalEvidence);
    }

    [Fact]
    public async Task SendAsync_SendManyFailedPreAcceptMapsToFailedPreAccept()
    {
        var sender = new BitcoinRpcPayoutSender(new FakeBitcoinPayoutRpcClient
        {
            SendManyResult = BitcoinPayoutRpcResult.FailedPreAccept("wallet_rejected", "wallet rejected")
        });

        var result = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, result.Status);
        Assert.Equal("wallet_rejected", result.ErrorCode);
        Assert.Equal("wallet rejected", result.ErrorMessage);
    }

    [Fact]
    public async Task SendAsync_SendManyAmbiguousMapsToAmbiguousRequiresReview()
    {
        var sender = new BitcoinRpcPayoutSender(new FakeBitcoinPayoutRpcClient
        {
            SendManyResult = BitcoinPayoutRpcResult.AmbiguousRequiresReview("wallet_timeout", "wallet timeout")
        });

        var result = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.AmbiguousRequiresReview, result.Status);
        Assert.Equal("wallet_timeout", result.ErrorCode);
        Assert.Equal("wallet timeout", result.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("send:fake-txid")]
    [InlineData("fake-placeholder-txid")]
    public async Task SendAsync_SendManyInvalidAcceptedTxIdReturnsAmbiguous(string txId)
    {
        var client = new FakeBitcoinPayoutRpcClient
        {
            SendManyResult = BitcoinPayoutRpcResult.Accepted(txId)
        };
        var sender = new BitcoinRpcPayoutSender(client);

        var result = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.AmbiguousRequiresReview, result.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidRpcResultErrorCode, result.ErrorCode);
        Assert.Equal(1, client.SendManyCallCount);
    }

    [Fact]
    public async Task SendAsync_NullRpcResultReturnsAmbiguous()
    {
        var client = new FakeBitcoinPayoutRpcClient
        {
            SendManyResult = null
        };
        var sender = new BitcoinRpcPayoutSender(client);

        var result = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.AmbiguousRequiresReview, result.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidRpcResultErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task SendAsync_SendManyEmptyIntentsFailsPreAcceptWithoutClientCall()
    {
        var client = new FakeBitcoinPayoutRpcClient();
        var sender = new BitcoinRpcPayoutSender(client);

        var result = await sender.SendAsync(SendManyContext(), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidRecipientErrorCode, result.ErrorCode);
        Assert.Equal(0, client.TotalCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SendAsync_SendManyNonPositiveAmountFailsPreAcceptWithoutClientCall(decimal amount)
    {
        var client = new FakeBitcoinPayoutRpcClient();
        var sender = new BitcoinRpcPayoutSender(client);

        var result = await sender.SendAsync(SendManyContext(Intent("addr-a", amount)), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidAmountErrorCode, result.ErrorCode);
        Assert.Equal(0, client.TotalCallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task SendAsync_SendManyEmptyAddressFailsPreAcceptWithoutClientCall(string address)
    {
        var client = new FakeBitcoinPayoutRpcClient();
        var sender = new BitcoinRpcPayoutSender(client);

        var result = await sender.SendAsync(SendManyContext(Intent(address, 1m)), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidRecipientErrorCode, result.ErrorCode);
        Assert.Equal(0, client.TotalCallCount);
    }

    [Fact]
    public async Task SendAsync_PoolIdMismatchFailsPreAcceptWithoutClientCall()
    {
        var client = new FakeBitcoinPayoutRpcClient();
        var sender = new BitcoinRpcPayoutSender(client);

        var batchMismatch = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)) with
        {
            Batch = Batch(PayoutProfileConstants.SendShapes.BatchMultiRecipient) with { PoolId = "pool-b" }
        }, CancellationToken.None);
        var intentMismatch = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m) with
        {
            PoolId = "pool-b"
        }), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, batchMismatch.Status);
        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, intentMismatch.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidContextErrorCode, batchMismatch.ErrorCode);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidContextErrorCode, intentMismatch.ErrorCode);
        Assert.Equal(0, client.TotalCallCount);
    }

    [Fact]
    public async Task SendAsync_CoinMismatchFailsPreAcceptWithoutClientCall()
    {
        var client = new FakeBitcoinPayoutRpcClient();
        var sender = new BitcoinRpcPayoutSender(client);

        var batchMismatch = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)) with
        {
            Batch = Batch(PayoutProfileConstants.SendShapes.BatchMultiRecipient) with { Coin = "litecoin" }
        }, CancellationToken.None);
        var intentMismatch = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m) with
        {
            Coin = "litecoin"
        }), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, batchMismatch.Status);
        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, intentMismatch.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidContextErrorCode, batchMismatch.ErrorCode);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidContextErrorCode, intentMismatch.ErrorCode);
        Assert.Equal(0, client.TotalCallCount);
    }

    [Fact]
    public async Task SendAsync_AttemptIdMismatchFailsPreAcceptWithoutClientCall()
    {
        var client = new FakeBitcoinPayoutRpcClient();
        var sender = new BitcoinRpcPayoutSender(client);

        var result = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m) with
        {
            AttemptId = 21
        }), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidContextErrorCode, result.ErrorCode);
        Assert.Equal(0, client.TotalCallCount);
    }

    [Fact]
    public async Task SendAsync_IncompatibleIntentStateFailsPreAcceptWithoutClientCall()
    {
        var client = new FakeBitcoinPayoutRpcClient();
        var sender = new BitcoinRpcPayoutSender(client);

        var settledIntent = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m) with
        {
            IntentState = PayoutIntentStates.Settled
        }), CancellationToken.None);
        var supersededMapping = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m) with
        {
            AttemptIntentState = PayoutAttemptIntentStates.Superseded
        }), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, settledIntent.Status);
        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, supersededMapping.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidContextErrorCode, settledIntent.ErrorCode);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidContextErrorCode, supersededMapping.ErrorCode);
        Assert.Equal(0, client.TotalCallCount);
    }

    [Fact]
    public async Task SendAsync_SendToAddressValidPerAddressCallsClientOnce()
    {
        var client = new FakeBitcoinPayoutRpcClient
        {
            SendToAddressResult = BitcoinPayoutRpcResult.Accepted("txid-sendtoaddress")
        };
        var sender = new BitcoinRpcPayoutSender(client);

        var result = await sender.SendAsync(SendToAddressContext(Intent("addr-a", 1.5m)), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, result.Status);
        Assert.Equal(0, client.SendManyCallCount);
        Assert.Equal(1, client.SendToAddressCallCount);
        Assert.Equal("pool-a", client.LastSendToAddressRequest.PoolId);
        Assert.Equal("bitcoin", client.LastSendToAddressRequest.Coin);
        Assert.Equal(10, client.LastSendToAddressRequest.BatchId);
        Assert.Equal(20, client.LastSendToAddressRequest.AttemptId);
        Assert.Equal(PayoutProfileConstants.SendMethods.SendToAddress, client.LastSendToAddressRequest.Method);
        Assert.Equal("addr-a", client.LastSendToAddressRequest.Address);
        Assert.Equal(1.5m, client.LastSendToAddressRequest.Amount);
    }

    [Fact]
    public async Task SendAsync_SendToAddressRequiresExactlyOneIntentWithoutClientCall()
    {
        var client = new FakeBitcoinPayoutRpcClient();
        var sender = new BitcoinRpcPayoutSender(client);

        var result = await sender.SendAsync(SendToAddressContext(Intent("addr-a", 1m), Intent("addr-b", 2m)),
            CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, result.Status);
        Assert.Equal(BitcoinRpcPayoutSender.InvalidRecipientErrorCode, result.ErrorCode);
        Assert.Equal(0, client.TotalCallCount);
    }

    [Fact]
    public async Task SendAsync_SendToAddressAcceptedTxIdReturnsTxIdEvidenceOnly()
    {
        var sender = new BitcoinRpcPayoutSender(new FakeBitcoinPayoutRpcClient
        {
            SendToAddressResult = BitcoinPayoutRpcResult.Accepted("txid-per-address")
        });

        var result = await sender.SendAsync(SendToAddressContext(Intent("addr-a", 1m)), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.Accepted, result.Status);
        Assert.Equal(PayoutExternalConfirmationKinds.TxId, result.Evidence.Kind);
        Assert.Equal("txid-per-address", result.Evidence.Value);
        Assert.Empty(result.AdditionalEvidence);
    }

    [Fact]
    public async Task SendAsync_SendToAddressFailedPreAcceptMapsToFailedPreAccept()
    {
        var sender = new BitcoinRpcPayoutSender(new FakeBitcoinPayoutRpcClient
        {
            SendToAddressResult = BitcoinPayoutRpcResult.FailedPreAccept("dust", "amount too small")
        });

        var result = await sender.SendAsync(SendToAddressContext(Intent("addr-a", 1m)), CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, result.Status);
        Assert.Equal("dust", result.ErrorCode);
        Assert.Equal("amount too small", result.ErrorMessage);
    }

    [Fact]
    public async Task SendAsync_UnsupportedShapeMethodOrHandlerFailsPreAcceptWithoutClientCall()
    {
        var client = new FakeBitcoinPayoutRpcClient();
        var sender = new BitcoinRpcPayoutSender(client);

        var wrongHandler = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)) with
        {
            Batch = Batch(PayoutProfileConstants.SendShapes.BatchMultiRecipient) with { Handler = "other-handler" }
        }, CancellationToken.None);
        var wrongShape = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)) with
        {
            Batch = Batch(PayoutProfileConstants.SendShapes.AddressGroup)
        }, CancellationToken.None);
        var wrongMethod = await sender.SendAsync(SendManyContext(Intent("addr-a", 1m)) with
        {
            Attempt = Attempt(PayoutProfileConstants.SendMethods.SendToAddress)
        }, CancellationToken.None);

        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, wrongHandler.Status);
        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, wrongShape.Status);
        Assert.Equal(PayoutAttemptSendStatus.FailedPreAccept, wrongMethod.Status);
        Assert.Equal(0, client.TotalCallCount);
    }

    [Fact]
    public async Task SendAsync_ClientCancellationWithRequestedCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeBitcoinPayoutRpcClient
        {
            OnSendMany = (_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
        };
        var sender = new BitcoinRpcPayoutSender(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sender.SendAsync(SendManyContext(Intent("addr-a", 1m)), cts.Token));
    }

    [Fact]
    public async Task SendAsync_UnexpectedClientExceptionPropagates()
    {
        var client = new FakeBitcoinPayoutRpcClient
        {
            OnSendMany = (_, _) => throw new InvalidOperationException("transport failed")
        };
        var sender = new BitcoinRpcPayoutSender(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync(SendManyContext(Intent("addr-a", 1m)), CancellationToken.None));
    }

    private static PayoutSendExecutionContext SendManyContext(params PayoutSendExecutionIntent[] intents)
    {
        return Context(PayoutProfileConstants.SendShapes.BatchMultiRecipient,
            PayoutProfileConstants.SendMethods.SendMany, intents);
    }

    private static PayoutSendExecutionContext SendToAddressContext(params PayoutSendExecutionIntent[] intents)
    {
        return Context(PayoutProfileConstants.SendShapes.PerAddress,
            PayoutProfileConstants.SendMethods.SendToAddress, intents);
    }

    private static PayoutSendExecutionContext Context(string sendShape, string method,
        IReadOnlyCollection<PayoutSendExecutionIntent> intents)
    {
        return new PayoutSendExecutionContext
        {
            Batch = Batch(sendShape),
            Attempt = Attempt(method),
            Intents = intents
        };
    }

    private static PayoutBatch Batch(string sendShape)
    {
        return new PayoutBatch
        {
            Id = 10,
            PoolId = "pool-a",
            Coin = "bitcoin",
            Handler = PayoutProfileConstants.AdapterIds.BitcoinRpc,
            SendShape = sendShape
        };
    }

    private static PayoutSendAttempt Attempt(string method)
    {
        return new PayoutSendAttempt
        {
            Id = 20,
            BatchId = 10,
            PoolId = "pool-a",
            Coin = "bitcoin",
            Method = method
        };
    }

    private static PayoutSendExecutionIntent Intent(string address, decimal amount)
    {
        return new PayoutSendExecutionIntent
        {
            IntentId = 30,
            AttemptId = 20,
            PoolId = "pool-a",
            Coin = "bitcoin",
            Address = address,
            Amount = amount,
            IntentState = PayoutIntentStates.Reserved,
            AttemptIntentState = PayoutAttemptIntentStates.Active
        };
    }

    private sealed class FakeBitcoinPayoutRpcClient : IBitcoinPayoutRpcClient
    {
        public BitcoinPayoutRpcResult SendManyResult { get; init; } =
            BitcoinPayoutRpcResult.Accepted("txid-default-sendmany");
        public BitcoinPayoutRpcResult SendToAddressResult { get; init; } =
            BitcoinPayoutRpcResult.Accepted("txid-default-sendtoaddress");
        public Func<BitcoinPayoutSendManyRequest, CancellationToken, BitcoinPayoutRpcResult> OnSendMany { get; init; }
        public Func<BitcoinPayoutSendToAddressRequest, CancellationToken, BitcoinPayoutRpcResult> OnSendToAddress { get; init; }
        public int SendManyCallCount { get; private set; }
        public int SendToAddressCallCount { get; private set; }
        public int TotalCallCount => SendManyCallCount + SendToAddressCallCount;
        public BitcoinPayoutSendManyRequest LastSendManyRequest { get; private set; }
        public BitcoinPayoutSendToAddressRequest LastSendToAddressRequest { get; private set; }

        public Task<BitcoinPayoutRpcResult> SendManyAsync(BitcoinPayoutSendManyRequest request, CancellationToken ct)
        {
            SendManyCallCount++;
            LastSendManyRequest = request;
            return Task.FromResult(OnSendMany == null ? SendManyResult : OnSendMany(request, ct));
        }

        public Task<BitcoinPayoutRpcResult> SendToAddressAsync(BitcoinPayoutSendToAddressRequest request,
            CancellationToken ct)
        {
            SendToAddressCallCount++;
            LastSendToAddressRequest = request;
            return Task.FromResult(OnSendToAddress == null ? SendToAddressResult : OnSendToAddress(request, ct));
        }
    }
}
