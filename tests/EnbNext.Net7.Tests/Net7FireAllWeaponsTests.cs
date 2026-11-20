using System.Reflection;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7FireAllWeaponsTests
{
    [Fact]
    public async Task FacadeRejectsStaleShipAndOwnerBeforeDispatch()
    {
        await using var client = new Client.EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(Client.EnbClient).GetField("_net7", flags)!.SetValue(client, session);
        typeof(Client.EnbClient).GetProperty("State")!.SetValue(client, Client.ClientLifecycleState.InWorld);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session, 7u);
        EnbR11FlightTests.FlightWorld(session.World);
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(session.Gameplay, 0);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        typeof(Client.EnbClient).GetMethod("OnNet7PresentationStateChanged", flags)!.Invoke(client, [1000u]);
        ulong owner = client.LatestPresentationSnapshot!.Objects.Single(o => o.IsLocalShip).RenderIdentity;
        int sent = 0;
        channel.FrameSent += (_, _) => sent++;
        Assert.False(await client.FireAllWeaponsAsync(8, owner));
        Assert.False(await client.FireAllWeaponsAsync(7, owner + 1));
        Assert.Equal(0, sent);
        Assert.True(await client.FireAllWeaponsAsync(7, owner));
        Assert.Equal(1, sent);
        typeof(Client.EnbClient).GetProperty("State")!.SetValue(client, Client.ClientLifecycleState.Disconnected);
        Assert.False(await client.FireAllWeaponsAsync(7, owner));
        Assert.Equal(1, sent);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(99u)]
    public async Task SendsOneSelfAddressedActionUsingAssignedIdWithoutChangingSelection(uint selected)
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(session.Gameplay, 0);
        // Deliberately distinguish Start identity, 0037 assigned identity, and selected target.
        typeof(World.Net7WorldState).GetProperty("ClientAvatarId")!.SetValue(session.World, 42u);
        session.Gameplay.SetLocalSelectedTarget(selected);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var frames = new List<(ushort Opcode, byte[] Payload)>();
        channel.FrameSent += (frame, _) => frames.Add((frame.Opcode, frame.Payload.ToArray()));
        Assert.True(await session.FireAllWeaponsAsync());
        var sent = Assert.Single(frames);
        Assert.Equal(0x002C, sent.Opcode);
        Assert.Equal(Net7GameplayWire.BuildAction(42, Net7PlayerAction.FireAllWeapons, 42, 0), sent.Payload);
        Assert.Equal(selected == 0 ? (uint?)null : selected, session.Gameplay.LocalSelectedTargetId);
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(session.Gameplay, 2);
        Assert.False(await session.FireAllWeaponsAsync());
        Assert.Single(frames);
    }
}

