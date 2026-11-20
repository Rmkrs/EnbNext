using EnbNext.Net7.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class Net7ResidualInboundWireTests
{
    [Fact]
    public void FourResidualFamiliesUseTheirExactCurrentEndianRules()
    {
        Net7ModeRequestUpdate mode = Net7ResidualInboundWire.DecodeModeRequest(
            Convert.FromHexString("4433221101000000"));
        Assert.Equal(0x11223344u, mode.Field0);
        Assert.Equal(1u, mode.Mode);

        Assert.Equal((short)1, Net7ResidualInboundWire.DecodeGlobalSetReferrerResponse(Convert.FromHexString("0001")));
        Assert.Equal((short)-2, Net7ResidualInboundWire.DecodeGlobalSetReferrerResponse(Convert.FromHexString("FFFE")));

        Assert.Equal(0x12345678u, Net7ResidualInboundWire.DecodeWarpIndex(Convert.FromHexString("78563412")));

        Net7NotifyEmoteUpdate emote = Net7ResidualInboundWire.DecodeNotifyEmote(
            Convert.FromHexString("0102030444332211"));
        Assert.Equal(0x01020304u, emote.GameId);
        Assert.Equal(0x11223344u, emote.Emote);
    }

    [Theory]
    [InlineData("", 0xFFFFFFFFu)]
    [InlineData("12", 0xFFFFFF12u)]
    [InlineData("1234", 0xFFFF3412u)]
    [InlineData("123456", 0xFF563412u)]
    [InlineData("12345678", 0x78563412u)]
    public void WarpIndexKeepsUnreadInitializedBytes(string hex, uint expected)
    {
        Assert.Equal(expected, Net7ResidualInboundWire.DecodeWarpIndex(Convert.FromHexString(hex)));
    }

    [Fact]
    public void ResidualFamiliesIgnoreUnusedTail()
    {
        var fixtures = new (Func<byte[], object> Decode, byte[] Payload)[]
        {
            (p => Net7ResidualInboundWire.DecodeModeRequest(p), new byte[8]),
            (p => Net7ResidualInboundWire.DecodeGlobalSetReferrerResponse(p), new byte[2]),
            (p => Net7ResidualInboundWire.DecodeWarpIndex(p), new byte[4]),
            (p => Net7ResidualInboundWire.DecodeNotifyEmote(p), new byte[8]),
        };

        foreach ((Func<byte[], object> decode, byte[] payload) in fixtures)
        {
            Assert.Equal(decode(payload), decode(payload.Concat(new byte[] { 0xEE }).ToArray()));
        }
    }
}
