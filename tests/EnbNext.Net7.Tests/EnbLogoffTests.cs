using System.Reflection;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbLogoffTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public async Task ConfirmationUsesAssignedAvatarRatherThanStartIdentity(uint type)
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var lifetime = receiver;
        session.World.SetLocalPlayerId(0x01020304);
        session.World.Apply(Packet.ForOpcode(0x37, Convert.FromHexString("44332211")), 0);
        var offer = new byte[10];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(offer, type);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(offer.AsSpan(4), 77);
        session.Gameplay.Apply(Packet.ForOpcode(0xBE, offer));
        var sent = new List<byte[]>();
        channel.FrameSent += (frame, _) => { if(frame.Opcode == 0xC0) sent.Add(frame.Payload.ToArray()); };
        Assert.True(await session.RespondConfirmedActionAsync(type, 77, true));
        Assert.Equal("112233440000004D00000001", Convert.ToHexString(Assert.Single(sent)));
        Assert.False(await session.RespondConfirmedActionAsync(type, 77, true));
        Assert.Single(sent);
    }

    [Theory]
    [InlineData(Net7LogoffDestination.Desktop)]
    [InlineData(Net7LogoffDestination.CharacterSelection)]
    [InlineData(Net7LogoffDestination.LoginScreen)]
    public async Task ChosenDestinationCompletesOnlyOnBAAndRetiresCharacter(Net7LogoffDestination destination)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await using var client = new EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        typeof(EnbClient).GetProperty("SelectedFixedSessionId")!.SetValue(client,7u);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session,0xCAFEBABEu);
        session.LogoffConfirmed += d => typeof(EnbClient).GetMethod("OnNet7LogoffConfirmed",flags)!.Invoke(client,[session,d]);
        session.LoginStateChanged += s => typeof(EnbClient).GetMethod("OnNet7LoginStateChanged",flags)!.Invoke(client,[s]);
        var (transport,channel,receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var packets = new List<(ushort Opcode, byte[] Bytes)>();
        channel.FrameSent += (f,_) => packets.Add((f.Opcode,f.Payload.ToArray()));
        var completed = new List<Net7LogoffDestination>();
        client.LogoffCompleted += d =>
        {
            Assert.Equal(ClientLifecycleState.CharacterSelection,client.State);
            Assert.Equal(Net7LoginState.CharacterSelection,session.LoginState);
            Assert.Null(client.SelectedFixedSessionId);
            completed.Add(d);
        };
        ulong handoff = session.World.PresentationHandoffVersion;
        Assert.False(await client.RequestLogoffAsync(8,handoff,destination));
        Assert.False(await client.RequestLogoffAsync(7,handoff+1,destination));
        typeof(EnbNext.Net7.World.Net7WorldState).GetProperty("IsTransitioning")!.SetValue(session.World,true);
        Assert.False(await client.RequestLogoffAsync(7,handoff,destination));
        session.World.ObserveSectorStart();
        transport.SetGameplayForwardingActive(false);
        Assert.False(await client.RequestLogoffAsync(7,handoff,destination));
        Assert.Null(session.PendingLogoffDestination); Assert.Empty(completed);
        transport.SetGameplayForwardingActive(true);
        Assert.Empty(packets);
        Assert.True(await client.RequestLogoffAsync(7,handoff,destination));
        // 0074BB50: a choice repeated before the confirmation sends the request again (no duplicate suppression).
        Assert.True(await client.RequestLogoffAsync(7,handoff,destination));
        Assert.Equal(2,packets.Count);
        Assert.Equal(destination,session.PendingLogoffDestination);
        var packet = packets[1];
        Assert.Equal(0xB9,packet.Opcode); Assert.Equal(Convert.FromHexString("BEBAFECA"),packet.Bytes);
        Assert.Empty(completed); Assert.Equal(7u,client.SelectedFixedSessionId);
        Assert.Equal(ClientLifecycleState.InWorld,client.State);
        // A server confirmation must not be mistaken for an automatic handoff
        // failure if a handoff began after the outgoing request.
        typeof(EnbClient).GetField("_automaticSectorTransitionActive",flags)!.SetValue(client,true);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.Redirecting);
        typeof(Net7Session).GetMethod("HandleLogoffConfirmation",flags)!.Invoke(session,[Packet.ForOpcode(0xBA,Array.Empty<byte>())]);
        Assert.Equal(destination,Assert.Single(completed));
        Assert.Null(client.SelectedFixedSessionId); Assert.Null(client.SelectedCharacter); Assert.Null(client.Route);
        Assert.Equal(ClientLifecycleState.CharacterSelection,client.State);
        Assert.Equal(0u,session.FixedSessionId);
        Assert.Null(typeof(EnbClient).GetField("_automaticHandoffRecoveryTask",flags)!.GetValue(client));
        Assert.Same(session,typeof(EnbClient).GetField("_net7",flags)!.GetValue(client));
        Assert.False(await client.RequestLogoffAsync(7,handoff,destination));
        // A subsequent unsolicited BA must not reuse the previous destination.
        typeof(Net7Session).GetMethod("HandleLogoffConfirmation",flags)!.Invoke(session,[Packet.ForOpcode(0xBA,Array.Empty<byte>())]);
        Assert.Single(completed);
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,null);
        typeof(EnbClient).GetMethod("OnNet7LogoffConfirmed",flags)!.Invoke(client,[session,destination]);
        Assert.Single(completed);
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,session);
    }
}
