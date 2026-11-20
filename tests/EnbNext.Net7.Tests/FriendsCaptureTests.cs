using EnbNext.Net7.Social;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;
public class FriendsCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedResponseCrossesReliabilityAsCompleteOrFragmentedPacket(bool fragmented)
    {
        var state = new Net7SocialState(); state.SetFriendsWindow(true);
        await using var engine = new EnbNext.Net7.Reliability.Net7ReliabilityEngine(
            new Net7ServerOptions { FeatureFlags = Net7FeatureFlags.None }, new EnbNext.Net7.Timing.SystemNet7RuntimeClock(), () => true,
            (_, _, _) => Task.FromResult(false), (_, _) => Task.FromResult(EnbNext.Net7.Reliability.Net7DecodedPacketDisposition.Unhandled),
            (packet, _, _) => { state.ApplyClientChatList(Net7SocialWire.DecodeClientChatList(packet.Payload.Span)); return Task.CompletedTask; });
        var inner = new byte[Payload.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(inner, (ushort)inner.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(inner.AsSpan(2), 0xA4);
        Payload.CopyTo(inner, 4);
        engine.PrepareForMasterLogin();
        // Same complete 516-byte inner frame as captured sequence 53; sequence zero anchors this isolated replay.
        await engine.AcceptOuterAsync(new ServerUdpFrame(0x2016, 0x4000420A, 0, fragmented ? inner[..512] : inner));
        if (fragmented)
        {
            Assert.Empty(state.Friends);
            await engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, 0x4000420A, 1, inner[512..]));
        }
        Assert.Equal(27, state.Friends.Count);
    }
    public static byte[] Payload => Convert.FromHexString("0000000000001B00000008004261726E61626173080046726F737477696E0700476C616369657206004D617276696E0600526F73616E610500546F6D656B0B004D65726361746F7269616E0C0049726F6E7761726C6F7264730A0050616C65736861646F770800456D62657274616C0A004C6F72656E646176696E050056616E746109004B657374726162656C07004B65737472697806004B657374726F05004D6F726C610500506978656C0900526176656E686F6C6D0A00537465656C666F72676505005A6F726261060054616C626F7405004F727665780500506C756D650700576967676C65730A00576179666172657269780A004361707461696E626F6206005175696E746F1B00000007006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E6507006F66666C696E65");
    [Fact] public void CapturedListDecodesAndInvalidatesPriorSnapshot()
    {
        var state = new Net7SocialState(); state.SetFriendsWindow(true);
        var before = EnbSceneChatSnapshot.From(state);
        state.ApplyClientChatList(Net7SocialWire.DecodeClientChatList(Payload));
        var after = EnbSceneChatSnapshot.From(state);
        Assert.Equal(27, after.Friends.Length);
        Assert.Equal("Barnabas", after.Friends[0].Identity);
        Assert.Equal("Quinto", after.Friends[^1].Identity);
        Assert.All(after.Friends, f => Assert.True(f.IsOffline));
        Assert.Empty(before.Friends);
    }
}
