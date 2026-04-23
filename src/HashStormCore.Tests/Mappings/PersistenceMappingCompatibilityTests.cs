using System;
using HashStormCore.Mappings;
using HashStormCore.Persistence.Model;
using Xunit;

namespace HashStormCore.Tests.Mappings;

public class PersistenceMappingCompatibilityTests
{
    private readonly ObjectMapper mapper = new();

    [Fact]
    public void ShareMapping_PreservesUtcTimestampAndBlockHeight()
    {
        var created = new DateTime(2026, 04, 23, 12, 34, 56, 789, DateTimeKind.Utc).AddTicks(1234);
        var share = new Share
        {
            PoolId = "pool-a",
            BlockHeight = 123456789,
            Miner = "miner-a",
            Worker = "worker-a",
            UserAgent = "agent-a",
            Difficulty = 128.5,
            NetworkDifficulty = 1024.25,
            IpAddress = "127.0.0.1",
            Source = "unit-test",
            Created = created
        };

        var entity = mapper.MapShareEntity(share);
        var roundTrip = mapper.MapShare(entity);

        Assert.Equal(created, entity.Created);
        Assert.Equal(DateTimeKind.Utc, entity.Created.Kind);
        Assert.Equal((long) share.BlockHeight, entity.BlockHeight);

        Assert.Equal(share.Created, roundTrip.Created);
        Assert.Equal(DateTimeKind.Utc, roundTrip.Created.Kind);
        Assert.Equal(share.BlockHeight, roundTrip.BlockHeight);
        Assert.Equal(share.NetworkDifficulty, roundTrip.NetworkDifficulty);
        Assert.Equal(share.Source, roundTrip.Source);
    }

    [Fact]
    public void PaymentMapping_PreservesUtcTimestampAndAmount()
    {
        var created = new DateTime(2026, 04, 23, 14, 15, 16, 17, DateTimeKind.Utc).AddTicks(4321);
        var payment = new Payment
        {
            PoolId = "pool-a",
            Coin = "BTC",
            Address = "wallet-address",
            Amount = 1.23456789m,
            TransactionConfirmationData = "txid-123",
            Created = created
        };

        var entity = mapper.MapPaymentEntity(payment);
        var roundTrip = mapper.MapPayment(entity);

        Assert.Equal(created, entity.Created);
        Assert.Equal(DateTimeKind.Utc, entity.Created.Kind);
        Assert.Equal(payment.Amount, entity.Amount);

        Assert.Equal(payment.PoolId, roundTrip.PoolId);
        Assert.Equal(payment.Coin, roundTrip.Coin);
        Assert.Equal(payment.Address, roundTrip.Address);
        Assert.Equal(payment.Amount, roundTrip.Amount);
        Assert.Equal(payment.TransactionConfirmationData, roundTrip.TransactionConfirmationData);
        Assert.Equal(payment.Created, roundTrip.Created);
        Assert.Equal(DateTimeKind.Utc, roundTrip.Created.Kind);
    }
}
