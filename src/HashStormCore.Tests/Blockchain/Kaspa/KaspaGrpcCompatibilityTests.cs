using Google.Protobuf;
using kaspad = HashStormCore.Blockchain.Kaspa.Kaspad;
using Xunit;

namespace HashStormCore.Tests.Blockchain.Kaspa;

public class KaspaGrpcCompatibilityTests
{
    [Fact]
    public void KaspadMessage_ProtobufRoundTrip_PreservesRequestPayloadCase()
    {
        var original = new kaspad.KaspadMessage
        {
            GetInfoRequest = new kaspad.GetInfoRequestMessage()
        };

        var bytes = original.ToByteArray();
        var roundTrip = kaspad.KaspadMessage.Parser.ParseFrom(bytes);

        Assert.Equal(kaspad.KaspadMessage.PayloadOneofCase.GetInfoRequest, roundTrip.PayloadCase);
        Assert.NotNull(roundTrip.GetInfoRequest);
    }
}
