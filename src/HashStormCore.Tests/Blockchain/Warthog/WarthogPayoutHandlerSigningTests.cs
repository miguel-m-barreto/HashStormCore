using System;
using System.Net.Http;
using Autofac;
using HashStormCore.Blockchain.Warthog;
using HashStormCore.Crypto.Hashing.Algorithms;
using HashStormCore.Extensions;
using HashStormCore.Mappings;
using HashStormCore.Messaging;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Repositories;
using HashStormCore.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NBitcoin.Secp256k1;
using NSubstitute;
using Xunit;
using IBlockRepository = HashStormCore.Persistence.Repositories.IBlockRepository;

namespace HashStormCore.Tests.Blockchain.Warthog;

public class WarthogPayoutHandlerSigningTests
{
    [Fact]
    public void WarthogPayoutHandler_SerializesSigningPayloadAndProducesDeterministicSignature()
    {
        var handler = CreateHandler();
        var wrapper = new PrivateObject(handler);

        var pinHashBytes = "ffeeddccbbaa99887766554433221100ffeeddccbbaa99887766554433221100".HexToByteArray();
        var pinHeightNonceIdFeeBytes = (byte[]) wrapper.Invoke("SerializePinHeightNonceIdFee", 123456u, 424242u, 4200000000ul);
        var amountBytes = (byte[]) wrapper.Invoke("SerializeAmount", 9876543210ul);
        var toAddressBytes = "11223344556677889900aabbccddeeff00112233".HexToByteArray();
        var signaturePayloadBytes = (byte[]) wrapper.Invoke("SerializeSignature", pinHashBytes, pinHeightNonceIdFeeBytes, toAddressBytes, amountBytes);

        Assert.Equal(
            "ffeeddccbbaa99887766554433221100ffeeddccbbaa998877665544332211000001e2400006793200000000000000fa56ea0011223344556677889900aabbccddeeff00112233000000024cb016ea",
            signaturePayloadBytes.ToHexString());

        Span<byte> signatureHashBytes = stackalloc byte[32];
        new Sha256S().Digest(signaturePayloadBytes, signatureHashBytes);

        Assert.Equal(
            "e0e26c142bb7d9982887c4ac2553044e779f21696014693dbe746b01a47054fa",
            signatureHashBytes.ToHexString());

        var ellipticPrivateKey = Context.Instance.CreateECPrivKey("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f".HexToByteArray());
        var signed = ellipticPrivateKey.TrySignECDSA(signatureHashBytes.ToArray(), null, out var recid, out var signature);

        Assert.True(signed);
        Assert.Equal(0, recid);
        Assert.Equal("bdcd9e6d75481b7deaf33df430df00b77b2f675a337a3f2314aff2a97d2453c0", signature.r.ToBytes().ToHexString());
        Assert.Equal("694b65e8232193a5c717e54b9e541c9ba638a81637b75136a1b99565f4570aa4", signature.s.ToBytes().ToHexString());

        var fullSignatureBytes = (byte[]) wrapper.Invoke("SerializeFullSignature", signature.r.ToBytes(), signature.s.ToBytes(), (byte) recid);

        Assert.Equal(
            "bdcd9e6d75481b7deaf33df430df00b77b2f675a337a3f2314aff2a97d2453c0694b65e8232193a5c717e54b9e541c9ba638a81637b75136a1b99565f4570aa400",
            fullSignatureBytes.ToHexString());
        Assert.Equal(WarthogConstants.FullSignatureByteSize, fullSignatureBytes.Length);
    }

    private static WarthogPayoutHandler CreateHandler()
    {
        return new WarthogPayoutHandler(
            Substitute.For<IComponentContext>(),
            Substitute.For<IConnectionFactory>(),
            Substitute.For<IObjectMapper>(),
            Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(),
            Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(),
            Substitute.For<IMasterClock>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IMessageBus>());
    }
}
