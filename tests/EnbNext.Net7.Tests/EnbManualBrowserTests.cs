using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class EnbManualBrowserTests
{

    [Fact]
    public async Task ManualQueryUsesRetainedSessionAndRejectsStalePopupWithoutPackets()
    {
        await using var client = new EnbClient(new());
        Assert.Null(client.GetManualBrowserQuery(7, 0));
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        typeof(EnbClient).GetField("_net7", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client, ClientLifecycleState.InWorld);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        int packets = 0; channel.FrameSent += (_, _) => packets++;
        ulong handoff = session.World.PresentationHandoffVersion;
        Assert.Null(client.GetManualBrowserQuery(7, handoff));
        typeof(Net7Session).GetProperty("CharacterSelection")!.SetValue(session,
            new Net7CharacterSelectionResult(0, null!, null!, 7, 0x6f, Net7SectorJoinClientData.CreateSuccessful(7, 42, 0)));
        Assert.Equal("characterid=117440512&lkey=", client.GetManualBrowserQuery(7, handoff));
        Assert.Null(client.GetManualBrowserQuery(0, handoff));
        Assert.Null(client.GetManualBrowserQuery(8, handoff));
        Assert.Null(client.GetManualBrowserQuery(7, handoff + 1));
        typeof(EnbClient).GetProperty("State")!.SetValue(client, ClientLifecycleState.Disconnected);
        Assert.Null(client.GetManualBrowserQuery(7, handoff));
        Assert.Equal(0, packets);
    }
}
