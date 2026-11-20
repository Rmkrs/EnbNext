using EnbNext.Net7.Social;

namespace EnbNext.Net7.Tests;

/// <summary>00747510 HandleGroupSlashCommand (GROUP-D15/D16/D17).</summary>
public sealed class Net7GroupSlashTests
{
    private static async Task<(Net7Session Session, List<(ushort Opcode, uint[] Words)> Packets, IDisposable Receiver)> Session()
    {
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        session.World.SetLocalPlayerId(42);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session, 42u);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        var packets = new List<(ushort, uint[])>();
        channel.FrameSent += (frame, _) =>
        {
            byte[] p = frame.Payload.ToArray();
            packets.Add((frame.Opcode, Enumerable.Range(0, p.Length / 4).Select(i => BitConverter.ToUInt32(p, i * 4)).ToArray()));
        };
        return (session, packets, receiver);
    }

    [Theory]
    [InlineData("/group disband", 0x0Du)]
    [InlineData("/group remove", 0x0Eu)]
    public async Task DisbandAndRemoveSendTheirOwnActionWhateverTheLeadership(string command, uint action)
    {
        var (session, packets, receiver) = await Session();
        await using var _ = session; using var __ = receiver;
        Assert.True(await session.SubmitChatAsync(new(command)));
        var packet = Assert.Single(packets);
        Assert.Equal((ushort)0x2C, packet.Opcode);
        Assert.Contains(action, packet.Words);
        Assert.DoesNotContain(packets, p => p.Opcode == 0x33);
    }

    [Fact]
    public async Task HelpWritesTheTwoRetailLinesAndSendsNothing()
    {
        var (session, packets, receiver) = await Session();
        await using var _ = session; using var __ = receiver;
        Assert.True(await session.SubmitChatAsync(new("/group help")));
        Assert.Empty(packets);
        Assert.Equal(["TIP: Usage: /group [invite | accept | refuse | disband | kick | help]", "TIP: For commands that need an object to act on, the target is used."],
            session.Gameplay.Social.Messages.Where(m => m.Type == 6).Select(m => m.Text));
    }

    [Theory]
    [InlineData("/group invite", "Cannot invite - no target.")]
    [InlineData("/group kick", "Target required.")]
    public async Task InviteOrKickWithoutATargetIsAHandledLocalNotice(string command, string notice)
    {
        var (session, packets, receiver) = await Session();
        await using var _ = session; using var __ = receiver;
        Assert.True(await session.SubmitChatAsync(new(command)));
        Assert.Empty(packets);
        Assert.Contains(session.Gameplay.Social.Messages, m => m.Type == 17 && m.Text.EndsWith(notice, StringComparison.Ordinal));
    }
}
