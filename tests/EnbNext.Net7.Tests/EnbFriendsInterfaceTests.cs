using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbFriendsInterfaceTests
{
    [Fact]
    public async Task WindowRequestsPublishActualSocialPacketsWithoutWorldDeltas()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await using var client = new EnbNext.Client.EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        typeof(EnbNext.Client.EnbClient).GetField("_net7", flags)!.SetValue(client, session);
        typeof(EnbNext.Client.EnbClient).GetProperty("State")!.SetValue(client, EnbNext.Client.ClientLifecycleState.InWorld);
        EnbR11FlightTests.FlightWorld(session.World);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session, 0x12345678u);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var lifetime = receiver;
        void Publish(uint time) => typeof(EnbNext.Client.EnbClient).GetMethod("OnNet7PresentationStateChanged", flags)!.Invoke(client, [time]);
        session.PresentationStateChanged += Publish; Publish(1000);
        ulong owner = client.LatestPresentationSnapshot!.Objects.Single(o => o.IsLocalShip).RenderIdentity;
        var frames = new List<(ushort Opcode, byte[] Payload)>();
        channel.FrameSent += (frame, _) => frames.Add((frame.Opcode, frame.Payload.ToArray()));
        Assert.False(await client.FriendsAsync(new(8, owner, EnbFriendsAction.Open)));
        Assert.False(await client.FriendsAsync(new(7, owner + 1, EnbFriendsAction.Open)));
        Assert.True(await client.FriendsAsync(new(7, owner, EnbFriendsAction.Open)));
        Assert.Equal(0xA3, frames.Single().Opcode);
        Assert.Equal(24u, BitConverter.ToUInt32(frames.Single().Payload, 4));
        Assert.Equal(0x12345678u, BitConverter.ToUInt32(frames.Single().Payload, 0));
        var before = client.LatestPresentationSnapshot.Hud.Chat;
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data);
        void String(string value) { byte[] bytes = System.Text.Encoding.Latin1.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        writer.Write(0u); String(""); writer.Write(2u); String("Barnabas"); String("OnlinePeer"); writer.Write(2u); String("offline"); String("Earth");
        async Task Receive(ushort opcode, byte[] payload) => await (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync", flags)!.Invoke(session,
            [EnbNext.Protocol.Net.Packet.ForOpcode(opcode, payload), "offline-friends", CancellationToken.None])!;
        await Receive(0xA4, data.ToArray());
        var after = client.LatestPresentationSnapshot!.Hud.Chat!;
        Assert.Equal(new[] { "Barnabas", "OnlinePeer" }, after.Friends.Select(f => f.Identity));
        Assert.True(after.Friends[0].IsOffline);
        Assert.Equal(before!.Lines, after.Lines);
        Assert.Empty(before.Friends);
        // The real list is 516 bytes including its inner header, crossing the
        // non-POPT split threshold. Exercise transport before session dispatch.
        await using (var engine = new EnbNext.Net7.Reliability.Net7ReliabilityEngine(
            new Net7ServerOptions { FeatureFlags = Net7FeatureFlags.None }, new EnbNext.Net7.Timing.SystemNet7RuntimeClock(), () => true,
            (_, _, _) => Task.FromResult(false), (_, _) => Task.FromResult(EnbNext.Net7.Reliability.Net7DecodedPacketDisposition.Unhandled),
            (packet, source, token) => (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync", flags)!.Invoke(session, [packet, source, token])!))
        {
            byte[] payload = FriendsCaptureTests.Payload;
            byte[] inner = new byte[payload.Length + 4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(inner, (ushort)inner.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(inner.AsSpan(2), 0xA4);
            payload.CopyTo(inner, 4);
            engine.PrepareForMasterLogin();
            await engine.AcceptOuterAsync(new ServerUdpFrame(0x2016, 7, 0, inner));
            Assert.Equal(27, client.LatestPresentationSnapshot!.Hud.Chat!.Friends.Length);
            Assert.Equal("Quinto", client.LatestPresentationSnapshot.Hud.Chat.Friends[^1].Identity);
            Assert.Equal(before.Lines, client.LatestPresentationSnapshot.Hud.Chat.Lines);
        }
        Assert.True(await client.FriendsAsync(new(7, owner, EnbFriendsAction.Remove, "Barnabas")));
        Assert.Equal(9u, BitConverter.ToUInt32(frames[^1].Payload, 4));
        Assert.True(await client.FriendsAsync(new(7, owner, EnbFriendsAction.Add, "NewPeer")));
        Assert.Equal(8u, BitConverter.ToUInt32(frames[^1].Payload, 4));
        var target=session.World.MaterializeCreate(new EnbNext.Net7.World.Net7CreateUpdate(99,1,175,1,0,0,0));
        var selectedProperty=session.World.Objects.FindFirst(7)!.AuxData!.Resolve(EnbNext.Net7.World.Net7AuxDataPropertyIds.ShipTargetGameId);
        selectedProperty.Value=99;selectedProperty.State=1;
        session.Gameplay.SetLocalSelectedTarget(99);Publish(1001);
        Assert.True(await client.FriendsAsync(new(7,owner,EnbFriendsAction.AddTarget,"Unknown")));
        var ownerProperty=target.AuxData!.Root.Children.Single(p=>p.Path=="ShipAuxData.Owner");
        ownerProperty.Value=new EnbNext.Net7.World.Net7AuxDataString([]);ownerProperty.State=1;Publish(1002);
        Assert.True(await client.FriendsAsync(new(7,owner,EnbFriendsAction.AddTarget,"")));
        Assert.Equal(18,frames[^1].Payload.Length);
        Assert.Equal(8u,BitConverter.ToUInt32(frames[^1].Payload,4));
        Assert.False(await client.FriendsAsync(new(7,owner,EnbFriendsAction.AddTarget,"StaleName")));
        selectedProperty.Value=0;session.Gameplay.SetLocalSelectedTarget(0);Publish(1003);
        byte[] FriendEvent(uint kind, string name, string location)
        {
            using var output = new MemoryStream(); using var w = new BinaryWriter(output);
            w.Write(kind); w.Write(0u);
            foreach (string text in new[] { "", "", "", "", name, location })
            { byte[] bytes = System.Text.Encoding.Latin1.GetBytes(text); w.Write((ushort)bytes.Length); w.Write(bytes); }
            w.Write(0u); return output.ToArray();
        }
        await Receive(0xA5, FriendEvent(21, "NewPeer", "Mars"));
        Assert.Contains(client.LatestPresentationSnapshot!.Hud.Chat!.Friends, f => f.Identity == "NewPeer" && f.Presence == "Mars");
        await Receive(0xA5, FriendEvent(22, "Barnabas", ""));
        Assert.DoesNotContain(client.LatestPresentationSnapshot!.Hud.Chat!.Friends, f => f.Identity == "Barnabas");
        Assert.Contains(after.Friends, f => f.Identity == "Barnabas");
        Assert.False(await client.FriendsAsync(new(7, owner, EnbFriendsAction.Remove, "Barnabas")));
        Assert.True(await client.FriendsAsync(new(7, owner, EnbFriendsAction.Restricted)));
        Assert.Equal(28u, BitConverter.ToUInt32(frames[^1].Payload, 4));
        Assert.True(client.LatestPresentationSnapshot!.Hud.Chat!.RestrictedStatus);
        int count = frames.Count;
        Assert.True(await client.FriendsAsync(new(7, owner, EnbFriendsAction.Restricted)));
        Assert.Equal(count, frames.Count);
        Assert.True(await client.FriendsAsync(new(7, owner, EnbFriendsAction.Unrestricted)));
        Assert.Equal(29u, BitConverter.ToUInt32(frames[^1].Payload, 4));
        Assert.False(client.LatestPresentationSnapshot!.Hud.Chat!.RestrictedStatus);
        Assert.True(await client.FriendsAsync(new(7, owner, EnbFriendsAction.Close)));
        Assert.False(session.Gameplay.Social.FriendsWindowActive);
        Assert.False(await client.FriendsAsync(new(7, owner, EnbFriendsAction.Add, "Hidden")));
    }
}
