using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbGuildInterfaceTests
{
    [Fact]
    public async Task ClientOpenUsesAssignedIdentityAndPublishesDecodedRoster()
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        await using var client = new EnbNext.Client.EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        typeof(EnbNext.Client.EnbClient).GetField("_net7", flags)!.SetValue(client, session);
        typeof(EnbNext.Client.EnbClient).GetProperty("State")!.SetValue(client, EnbNext.Client.ClientLifecycleState.InWorld);
        EnbR11FlightTests.FlightWorld(session.World);
        const uint assigned = 0x40000007;
        session.World.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0037, BitConverter.GetBytes(assigned)), 1000);
        var rank = session.World.Objects.FindFirst(7)!.AuxData!.Properties.Single(p => p.Path == "ShipAuxData.GuildRank");
        rank.Value = BitConverter.GetBytes(6); rank.State = 1;
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        void Publish(uint time) => typeof(EnbNext.Client.EnbClient).GetMethod("OnNet7PresentationStateChanged", flags)!.Invoke(client, [time]);
        session.PresentationStateChanged += Publish;
        Publish(1000);
        var owner = client.LatestPresentationSnapshot!.Objects.Single(o => o.IsLocalShip).RenderIdentity;
        var frames = new List<(ushort Opcode, byte[] Payload)>();
        channel.FrameSent += (frame, _) => frames.Add((frame.Opcode, frame.Payload.ToArray()));
        Assert.True(await client.GuildAsync(new(7, owner, EnbGuildAction.Open)));
        Assert.True(session.Gameplay.Social.GuildWindowActive);
        Assert.Equal(new ushort[] { 0xD4, 0x33 }, frames.Select(f => f.Opcode));
        Assert.Equal(Net7GameplayWire.BuildGuildRankNamesRequest(assigned), frames[0].Payload);
        Assert.Equal(Net7GameplayWire.BuildPlayerCommand(assigned, Net7ChatCommandType.LocalArea, "/gc list all"), frames[1].Payload);
        // Replay the live rank response first: it previously threw here and
        // prevented the subsequent roster from ever being processed.
        await (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync", flags)!.Invoke(session,
            [EnbNext.Protocol.Net.Packet.ForOpcode(0xD3, Net7GuildRankCaptureTests.CapturedRanks), "offline-guild", CancellationToken.None])!;
        Assert.Equal(10, client.LatestPresentationSnapshot!.Hud.Guild!.Ranks.Length);
        // Feed a decoded server packet through session dispatch, not directly into the UI fixture.
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data);
        writer.Write(3u);
        foreach (string text in new[] { "Kestralion \"Master Chief\"\nBarnabas \"Ensign\" (offline)", "Star Wardens" })
        { var bytes = System.Text.Encoding.Latin1.GetBytes(text); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        await (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync", flags)!.Invoke(session,
            [EnbNext.Protocol.Net.Packet.ForOpcode(0xD0, data.ToArray()), "offline-guild", CancellationToken.None])!;
        Assert.Equal(new[] { "Kestralion", "Barnabas" }, client.LatestPresentationSnapshot!.Hud.Guild!.Members.Select(m => m.Identity));
        async Task Receive(ushort opcode, byte[] payload) => await (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync", flags)!.Invoke(session,
            [EnbNext.Protocol.Net.Packet.ForOpcode(opcode, payload), "offline-guild", CancellationToken.None])!;
        await Receive(0xD2, Convert.FromHexString("00000FFF000000060000000600000000"));
        Assert.Equal(0xfffu, client.LatestPresentationSnapshot!.Hud.Guild!.Permissions.Mask);
        await Receive(0xD3, Convert.FromHexString("000000010600456E7369676E00000001"));
        Assert.Equal("Ensign", client.LatestPresentationSnapshot!.Hud.Guild!.Ranks.Single().Name);
        await Receive(0xC8, Convert.FromHexString("0400506565720500466C656574"));
        var prompt = client.LatestPresentationSnapshot!.Hud.Guild!.Prompt;
        Assert.NotNull(prompt);
        Assert.True(await client.GuildAsync(new(7, owner, EnbGuildAction.Accept, Prompt: prompt)));
        Assert.Equal(Net7GameplayWire.BuildGuildRecruitAccept(assigned, true), frames[^1].Payload);
        Assert.True(await client.GuildAsync(new(7, owner, EnbGuildAction.Close)));
        Assert.False(session.Gameplay.Social.GuildWindowActive);
    }

    [Fact]
    public void RosterSeparatesIdentityAndQuotedRankAndSortsOnlineFirst()
    {
        var social = new Net7SocialState(); social.SetGuildWindow(true);
        social.ApplyGuildMessage(new(3, "Alpha \"Master Chief\" (offline)\nZulu \"Recruit\"\nBeta \"Officer\"", ""));
        Assert.Equal(new[] { "Beta", "Zulu", "Alpha" }, social.GuildRosterEntries.Select(m => m.Identity));
        var offline = social.GuildRosterEntries[2];
        Assert.Equal("Master Chief", offline.RankName);
        Assert.Equal("\"Master Chief\" (offline)", offline.Status);
        Assert.True(offline.IsOffline);
        Assert.Equal("A Name", Net7GuildRosterEntry.Parse("\"A Name\" \"Officer\"").Identity);
        Assert.Equal("", Net7GuildRosterEntry.Parse("Unranked").RankName);
    }

    [Fact]
    public void ControlsUseServerRanksAndPermissionsWithoutOptimisticMembership()
    {
        var guild = new EnbSceneGuildSnapshot(new(0xfff, 5, 5, 0), [new("Officer", 3)],
            [Net7GuildRosterEntry.Parse("Beta \"Officer\"")], [], [], null);
        Assert.True(guild.Allows(EnbGuildAction.Promote, 6, "Beta"));
        Assert.False(guild.Allows(EnbGuildAction.Promote, 3, "Beta"));
        Assert.False(guild.Allows(EnbGuildAction.Promote, 6, "Gone"));
        Assert.False((guild with { Ranks = [] }).Allows(EnbGuildAction.Promote, 6, "Beta"));
        Assert.False((guild with { Permissions = new(0, uint.MaxValue, uint.MaxValue, uint.MaxValue) }).Allows(EnbGuildAction.Disband, 6, "Beta"));
        Assert.True(guild.Allows(EnbGuildAction.Create, -1, null));
        Assert.False(guild.Allows(EnbGuildAction.Leave, -1, null));
    }

    [Fact]
    public void SnapshotIsDetachedAndReopeningClearsRetainedListings()
    {
        var state = new Net7GameplayState(); state.Social.SetGuildWindow(true);
        state.Social.ApplyGuildMessage(new(3, "Beta \"Officer\"", ""));
        state.Social.ApplyGuildMessage(new(2, "Statistics", ""));
        var snapshot = EnbSceneGuildSnapshot.From(state);
        state.Social.SetGuildWindow(false); state.Social.SetGuildWindow(true);
        Assert.Single(snapshot.Members); Assert.Equal("Statistics", snapshot.FirstLines.Single());
        Assert.Empty(state.Social.GuildRosterEntries); Assert.Empty(state.Social.GuildSecondaryFirstLines);
        state.Social.GuildSecondarySubview = Net7GuildSecondarySubview.Second;
        state.Social.ApplyGuildMessage(new(2, "Contributions", ""));
        Assert.Equal("Contributions", EnbSceneGuildSnapshot.From(state).SecondLines.Single());
    }

    [Fact]
    public void DisplayOpensThenAppendsChunksAndReplacesAfterClose()
    {
        var state = new Net7GameplayState(); state.Social.SetGuildWindow(true);
        state.Social.ApplyGuildMessage(new(2, "First", ""));
        state.Social.ApplyGuildMessage(new(2, "Second", ""));
        var snapshot = EnbSceneGuildSnapshot.From(state);
        Assert.Equal(new[] { "First", "Second" }, snapshot.DisplayLines);
        state.Social.GuildSecondarySubview = Net7GuildSecondarySubview.First;
        state.Social.ApplyGuildMessage(new(2, "Replacement", ""));
        Assert.Equal("Replacement", EnbSceneGuildSnapshot.From(state).DisplayLines.Single());
        Assert.True(EnbSceneGuildSnapshot.From(state).DisplayRevision > snapshot.DisplayRevision);
    }
}
