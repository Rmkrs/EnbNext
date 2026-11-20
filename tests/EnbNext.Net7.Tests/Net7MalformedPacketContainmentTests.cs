using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

// Safety rule (undefined native reads): a malformed ordinary packet is consumed
// and the session continues. Before this containment, a decoder exception
// escaped the gameplay dispatcher, reached the routed pump's catch-all and
// ended the session; Net7Proxy (00401480) logs and returns instead, and the
// retail client has no packet-validation disconnect.
public sealed class Net7MalformedPacketContainmentTests
{
    [Fact]
    public async Task MalformedOrdinaryPacketsNeverEndTheSessionAndLaterPacketsStillApply()
    {
        var method = typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[][] payloads = [[], [0x01], [0x01, 0x02], [0x01, 0x02, 0x03], [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], Enumerable.Repeat((byte)0xFF, 40).ToArray()];
        var escaped = new List<string>();
        for (int op = 0; op < 0x100; op++)
        {
            // 0x0005 Start requires the selected-character route that exists before
            // any routed gameplay channel is opened; this harness has no such route.
            if (op == 0x0005) continue;
            await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
            var (_, _, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
            using var r = receiver;
            session.World.SetLocalPlayerId(0x01020304);
            foreach (byte[] payload in payloads)
            {
                try
                {
                    var task = (Task)method.Invoke(session, [Packet.ForOpcode((ushort)op, payload), "decoded-inner", CancellationToken.None])!;
                    Assert.True(await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) == task, $"0x{op:X4} len={payload.Length} did not finish");
                    await task;
                }
                catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                {
                    var e = ex is TargetInvocationException t ? t.InnerException! : ex;
                    escaped.Add($"0x{op:X4} len={payload.Length}: {e.GetType().Name}: {e.Message}");
                }
            }

            const uint followUpId = 0x00C0FFEE;
            await (Task)method.Invoke(session, [Packet.ForOpcode(0x0004, BuildCreate(followUpId)), "decoded-inner", CancellationToken.None])!;
            Assert.True(session.World.Objects.FindFirst(followUpId) is not null, $"Create after malformed 0x{op:X4} did not apply");
        }
        Assert.Empty(escaped);
    }

    private static byte[] BuildCreate(uint gameId)
    {
        byte[] payload = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4, 4), 1f);
        return payload;
    }
}
