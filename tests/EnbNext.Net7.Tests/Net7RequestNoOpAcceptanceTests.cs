using System.Reflection;
using EnbNext.Protocol.Net;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class Net7RequestNoOpAcceptanceTests
{
    [Theory]
    [InlineData(0x0015)]
    [InlineData(0x008D)]
    [InlineData(0x008E)]
    [InlineData(0x008F)]
    [InlineData(0x0027)]
    [InlineData(0x0028)]
    [InlineData(0x0029)]
    [InlineData(0x002C)]
    [InlineData(0x004E)]
    [InlineData(0x0079)]
    [InlineData(0x007A)]
    [InlineData(0x007B)]
    [InlineData(0x007C)]
    [InlineData(0x007E)]
    [InlineData(0x0080)]
    [InlineData(0x0086)]
    [InlineData(0x0087)]
    public async Task UnusedRequestFieldsNeverRejectOrCompleteGameplay(int opcode)
    {
        List<string> trace = [];
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" }, trace.Add);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Net7Session).GetField("_gameplayClientTargetActive", flags)!.SetValue(session, true);
        var dispatch = typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync", flags)!;
        int delivered = 0;
        session.GameplayPacketReceived += _ => delivered++;
        foreach (int length in new[] { 0, 1, 3, 4, 5, 11, 21, 24, 37 })
        {
            byte[] payload = Enumerable.Repeat((byte)0xFF, length).ToArray();
            await (Task)dispatch.Invoke(session, [Packet.ForOpcode((ushort)opcode, payload), "direct outer UDP", CancellationToken.None])!;
        }
        Assert.Equal(0, delivered);
        Assert.Equal(0, session.World.Objects.Count);
        Assert.Empty(session.Gameplay.Effects);
        Assert.DoesNotContain(trace, line => line.Contains("rejected", StringComparison.OrdinalIgnoreCase));
    }
}
