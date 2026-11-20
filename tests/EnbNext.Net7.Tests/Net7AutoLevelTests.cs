using System.Reflection;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7AutoLevelTests
{
    [Fact]
    public async Task ToggleUsesAssignedIdentityAndLeavesAuthoritativeStateUntouched()
    {
        await using var client = new Client.EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(Client.EnbClient).GetField("_net7", flags)!.SetValue(client, session);
        typeof(Client.EnbClient).GetProperty("State")!.SetValue(client, Client.ClientLifecycleState.InWorld);
        EnbR11FlightTests.FlightWorld(session.World);
        typeof(World.Net7WorldState).GetProperty("ClientAvatarId")!.SetValue(session.World, 42u);
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(session.Gameplay, 0);
        session.Gameplay.SetLocalSelectedTarget(99);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        typeof(Client.EnbClient).GetMethod("OnNet7PresentationStateChanged", flags)!.Invoke(client, [1000u]);
        ulong owner = client.LatestPresentationSnapshot!.Objects.Single(o => o.IsLocalShip).RenderIdentity;
        var frames = new List<(ushort Opcode, byte[] Payload)>();
        channel.FrameSent += (frame, _) => frames.Add((frame.Opcode, frame.Payload.ToArray()));
        var before = session.World.Objects.FindFirst(7)!.AuxData!.Resolve(25);
        Assert.False(await client.ToggleAutoLevelAsync(8, owner));
        Assert.False(await client.ToggleAutoLevelAsync(7, owner + 1));
        Assert.Empty(frames);
        Assert.True(await client.ToggleAutoLevelAsync(7, owner));
        var sent = Assert.Single(frames);
        Assert.Equal(0x0014, sent.Opcode);
        Assert.Equal(Net7GameplayWire.BuildMove(42, 13), sent.Payload);
        Assert.Equal(before, session.World.Objects.FindFirst(7)!.AuxData!.Resolve(25));
        Assert.Equal(99u, session.Gameplay.LocalSelectedTargetId);
        // 007436A0 sends Move 13 without a presentation-mode test.
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(session.Gameplay, 2);
        Assert.True(await client.ToggleAutoLevelAsync(7, owner));
        Assert.Equal(2, frames.Count);
        typeof(Client.EnbClient).GetProperty("State")!.SetValue(client, Client.ClientLifecycleState.Disconnected);
        Assert.False(await client.ToggleAutoLevelAsync(7, owner));
        Assert.Equal(2, frames.Count);
    }
}
