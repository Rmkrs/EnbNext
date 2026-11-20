using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Net7.Timing;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // retail/hardware verification, >=5 s; run with --filter Category=Heavy
[Collection(LoadSensitiveCollection.Name)]
public sealed class Net7SessionEntryTests
{
    [Theory]
    [InlineData("scan_complete", 0)]
    [InlineData("scan_complete", 1)]
    [InlineData("", 0)]
    public async Task CountedSoundWithoutNulKeepsSessionAndControlsAlive(string name, byte queue)
    {
        await using var harness = await EntryHarness.CreateAsync();
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var session = new Net7Session(harness.Options, trace.Enqueue);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;
        var enter = session.EnterSelectedCharacterAsync();
        _ = await harness.MasterUdp.ReceiveAsync();
        var request = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, request.RemoteEndPoint, 0x01020304);
        var login = await harness.GameplayUdp.ReceiveAsync();
        await SendStartAsync(harness.GameplayUdp, login.RemoteEndPoint, 0xAABBCCDD, 0, 0xCAFEBABE);
        for (int i = 0; i < 3; i++) _ = await ReceiveWithinAsync(harness.GameplayUdp);
        await enter.WaitAsync(TimeSpan.FromSeconds(2));

        byte[] sound = new byte[9 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(sound, (uint)name.Length);
        Encoding.Latin1.GetBytes(name).CopyTo(sound, 4);
        sound[^1] = queue;
        // Two inner packets in one datagram: the sound must not abort dispatch
        // of the following stop command, nor the session's receive loop.
        var payload = BuildInnerFrame(0x006A, sound)
            .Concat(BuildInnerFrame(0x006B, new byte[8])).ToArray();
        await harness.GameplayUdp.SendAsync(new ServerUdpFrame(0x2016, 0xAABBCCDD, 0, payload).ToWireBytes(), login.RemoteEndPoint);
        await WaitUntilAsync(() => session.Gameplay.ClientSoundEvents.Count == 2);
        Assert.Equal(name, session.Gameplay.ClientSoundEvents[0].Name);
        Assert.Equal(Net7LoginState.SectorReady, session.LoginState);
        Assert.True(await session.SendHorizontalSteeringAsync(0.5f));
        var steering = ServerUdpFrame.Parse((await ReceiveWithinAsync(harness.GameplayUdp)).Buffer);
        Assert.Equal(0x0012, steering.Opcode);
        Assert.DoesNotContain(trace, line => line.Contains("fatal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OrdinaryGalaxyMapCommand4RepliesBeforeStartWithoutCompletingLogin()
    {
        await using var harness = await EntryHarness.CreateAsync();
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var session = new Net7Session(harness.Options, trace.Enqueue);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;
        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync();
        _ = await harness.MasterUdp.ReceiveAsync();
        var request = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, request.RemoteEndPoint, 0x01020304);
        var sectorLogin = await harness.GameplayUdp.ReceiveAsync();
        byte[] payload = Convert.FromHexString(Net7GalaxyMapLocationTests.Captured);
        await harness.GameplayUdp.SendAsync(new ServerUdpFrame(0x2016, 0xAABBCCDD, 0,
            BuildInnerFrame(0x0097, payload)).ToWireBytes(), sectorLogin.RemoteEndPoint);
        var reply = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
        Assert.Equal(0x0098, reply.Opcode);
        Assert.Equal(0xAABBCCDDu, reply.FixedSessionId);
        Assert.Equal(Net7GalaxyMapLocationWire.BuildPresenceReply(Net7GalaxyMapLocationWire.Decode(payload),
            session.GalaxyMap!), reply.Payload.ToArray());
        Assert.Null(session.PlayerId); // Reply channel cannot be borrowed from Start.
        Assert.Equal("Swooping Eagle", session.GalaxyMapLocation!.SectorName);
        Assert.False(enter.IsCompleted);

        // Malformed later input advances reliability safely, preserves the last
        // location and produces no new reply or fabricated completion.
        await harness.GameplayUdp.SendAsync(new ServerUdpFrame(0x2016, 0xAABBCCDD, 1,
            BuildInnerFrame(0x0097, payload[..^1])).ToWireBytes(), sectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() => session.ReliabilityState!.Cursor == 2);
        Assert.Equal("Swooping Eagle", session.GalaxyMapLocation.SectorName);
        Assert.Contains(trace, line => line.Contains("GalaxyMap 0x0097 rejected safely"));
        Assert.DoesNotContain(trace, line => line.Contains("unsupported ordinary inbound 0x0097"));
        Assert.False(enter.IsCompleted);

        var before = session.GalaxyMap!;
        await harness.GameplayUdp.SendAsync(new ServerUdpFrame(0x2016, 0xAABBCCDD, 2,
            BuildInnerFrame(0x0097, Net7GalaxyMapUpdatesTests.Packet(3,new byte[63]))).ToWireBytes(), sectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() => session.GalaxyMap!.AuxiliaryBlob.All(b => b == 0));
        Assert.Equal(before.Sectors,session.GalaxyMap!.Sectors);
        var removed = before.Sectors[0];
        await harness.GameplayUdp.SendAsync(new ServerUdpFrame(0x2016, 0xAABBCCDD, 3,
            BuildInnerFrame(0x0097, Net7GalaxyMapUpdatesTests.Packet(2,BitConverter.GetBytes(removed.Field0)))).ToWireBytes(), sectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() => session.GalaxyMap!.Sectors.Count == before.Sectors.Count-1);
        Assert.DoesNotContain(session.GalaxyMap!.Sectors,s=>s.SectorHashId==removed.SectorHashId);
        Assert.True(before.IsKnown(removed));
        Assert.False(enter.IsCompleted); // Neither mutation manufactures login completion or a 0098.

        await harness.GameplayUdp.SendAsync(new ServerUdpFrame(0x2011, 0xAABBCCDD, 94,
            ReadOnlyMemory<byte>.Empty).ToWireBytes(), sectorLogin.RemoteEndPoint);
        await SendStartAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD, 95, 0xCAFEBABE);
        for (int i = 0; i < 3; i++)
            Assert.NotEqual(0x0098, ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2))).Buffer).Opcode);
        await enter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(Net7LoginState.SectorReady, session.LoginState);
        Assert.Contains(session.GalaxyMap!.Sectors,s=>s.SectorHashId==removed.SectorHashId);
        Assert.All(session.GalaxyMap.Sectors,s=>Assert.True(session.GalaxyMap.IsKnown(s)));
        var retainedMap=session.GalaxyMap;
        Assert.True(await session.ToggleGalaxyMapAsync());
        var toggle=ServerUdpFrame.Parse((await ReceiveWithinAsync(harness.GameplayUdp)).Buffer);
        Assert.Equal(0x0098,toggle.Opcode);
        Assert.Equal(Net7GameplayWire.BuildGalaxyMap2(1,[],0xCAFEBABE,true),toggle.Payload.ToArray());
        Assert.True(session.GalaxyMapShowAll);
        Assert.Same(retainedMap,session.GalaxyMap);
    }

    [Fact]
    public async Task ZeroControlIdentityRetainsRoutedIdentityAndRepliesWithZero200F()
    {
        await using var harness = await EntryHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);

        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Assert.Equal(0xAABBCCDDu, session.FixedSessionId);
        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");

        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x2005, 0, 124, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            client);

        ServerUdpFrame control = ServerUdpFrame.Parse((await ReceiveWithinAsync(harness.MasterUdp)).Buffer);
        Assert.Equal(0x200F, control.Opcode);
        Assert.Equal(0u, control.FixedSessionId);
        Assert.Empty(control.Payload.ToArray());
        Assert.Equal(0xAABBCCDDu, session.FixedSessionId);
    }

    [Fact]
    public async Task ShortMasterControlProspectExtensionIsDroppedAndThePumpContinues()
    {
        await using var harness = await EntryHarness.CreateAsync();
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var session = new Net7Session(harness.Options, trace.Enqueue);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;
        var enter = session.EnterSelectedCharacterAsync();
        _ = await harness.MasterUdp.ReceiveAsync();
        var request = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, request.RemoteEndPoint, 0x01020304);
        var login = await harness.GameplayUdp.ReceiveAsync();
        await SendStartAsync(harness.GameplayUdp, login.RemoteEndPoint, 0xAABBCCDD, 0, 0xCAFEBABE);
        for (int i = 0; i < 3; i++) _ = await ReceiveWithinAsync(harness.GameplayUdp);
        await enter.WaitAsync(TimeSpan.FromSeconds(2));
        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");

        // Safety rule: a 0x2012 shorter than its 20-byte record is dropped like on
        // the routed channel; the master/control pump keeps serving later frames.
        await harness.MasterUdp.SendAsync(new ServerUdpFrame(0x2012, 0xAABBCCDD, 123, new byte[4]).ToWireBytes(), client);
        await harness.MasterUdp.SendAsync(new ServerUdpFrame(0x2005, 0, 124, ReadOnlyMemory<byte>.Empty).ToWireBytes(), client);
        ServerUdpFrame control = ServerUdpFrame.Parse((await ReceiveWithinAsync(harness.MasterUdp)).Buffer);
        Assert.Equal(0x200F, control.Opcode);
        Assert.Contains(trace, line => line.Contains("master/control UDP 0x2012 rejected safely"));
        Assert.DoesNotContain(trace, line => line.Contains("fatal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DirectOuterSetClientTypeWithoutGameplayTargetDoesNotMutateClientPresentation()
    {
        await using var harness = await EntryHarness.CreateAsync();
        var suppressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(
            harness.Options,
            trace: message =>
            {
                if (message.Contains("ENTRY-09 0x003C", StringComparison.Ordinal)
                    && message.Contains("no active gameplay client target", StringComparison.Ordinal))
                {
                    suppressed.TrySetResult(true);
                }
            });

        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Assert.Null(session.Gameplay.SectorType);
        Assert.Equal<int?>(2, session.Gameplay.PresentationMode);
        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");
        byte[] sectorType = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sectorType, 1);
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x003C, 0xAABBCCDD, 125, sectorType).ToWireBytes(),
            client);

        await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(session.Gameplay.SectorType);
        Assert.Equal<int?>(2, session.Gameplay.PresentationMode);
        Assert.False(session.Gameplay.PresentationNotificationPending);
    }

    [Fact]
    public async Task DirectOuterSimplePositionWithoutGameplayTargetDoesNotReachClientTimeOrWorld()
    {
        await using var harness = await EntryHarness.CreateAsync();
        var suppressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(
            harness.Options,
            trace: message =>
            {
                if (message.Contains("direct outer 0x0008", StringComparison.Ordinal)
                    && message.Contains("client materialization suppressed", StringComparison.Ordinal))
                {
                    suppressed.TrySetResult(true);
                }
            });

        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Assert.Equal(0, session.World.Time.DriftSampleCount);
        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x0008, 0xAABBCCDD, 125, BuildSimplePositionPayload(0x10203040, 100)).ToWireBytes(),
            client);

        await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
        Assert.Equal(0, session.World.Time.DriftSampleCount);
        Assert.Null(session.World.Objects.FindFirst(0x10203040));
    }

    [Fact]
    public async Task DirectOuterAdvancedPositionWithoutGameplayTargetDoesNotReachClientTimeOrWorld()
    {
        await using var harness = await EntryHarness.CreateAsync();
        var suppressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(
            harness.Options,
            trace: message =>
            {
                if (message.Contains("WORLD positional direct outer 0x003E", StringComparison.Ordinal)
                    && message.Contains("no active gameplay client target", StringComparison.Ordinal))
                {
                    suppressed.TrySetResult(true);
                }
            });

        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Assert.Equal(0, session.World.Time.DriftSampleCount);
        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x003E, 0xAABBCCDD, 126, BuildAdvancedPositionPayload(0x10203040, 100)).ToWireBytes(),
            client);

        await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
        Assert.Equal(0, session.World.Time.DriftSampleCount);
        Assert.Null(session.World.Objects.FindFirst(0x10203040));
    }

    [Fact]
    public async Task DirectOuterFixedAndComponentPositionWithoutGameplayTargetDoNotMutateClientWorld()
    {
        await using var harness = await EntryHarness.CreateAsync();
        int suppressedCount = 0;
        var suppressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(
            harness.Options,
            trace: message =>
            {
                if (message.Contains("WORLD positional direct outer", StringComparison.Ordinal)
                    && message.Contains("no active gameplay client target", StringComparison.Ordinal)
                    && Interlocked.Increment(ref suppressedCount) == 2)
                {
                    suppressed.TrySetResult(true);
                }
            });

        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        const uint fixedId = 0x10203040;
        const uint componentId = 0x10203041;
        session.World.MaterializeCreate(new Net7CreateUpdate(fixedId, 1f, 0, 0, 0f, 0f, 0f));
        session.World.MaterializeCreate(new Net7CreateUpdate(componentId, 1f, 0, 0x04, 0f, 0f, 0f));

        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x0040, 0xAABBCCDD, 127, BuildFixedPositionPayload(fixedId)).ToWireBytes(),
            client);
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x0046, 0xAABBCCDD, 128, BuildComponentPositionPayload(componentId, 100)).ToWireBytes(),
            client);

        await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
        Assert.Null(session.World.Objects.FindFirst(fixedId)!.Spatial);
        Assert.Null(session.World.Objects.FindFirst(componentId)!.Spatial);
    }

    [Fact]
    public async Task DirectOuter2019WithoutGameplayTargetDoesNotReachIntegratedClientWorld()
    {
        await using var harness = await EntryHarness.CreateAsync();
        var suppressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(
            harness.Options,
            trace: message =>
            {
                if (message.Contains("WORLD-06 direct outer 0x2019", StringComparison.Ordinal)
                    && message.Contains("raw client delivery suppressed", StringComparison.Ordinal))
                {
                    suppressed.TrySetResult(true);
                }
            });

        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x2019, 0xAABBCCDD, 129, BuildWorld2019Payload(0x10203042, "NoTargetOre")).ToWireBytes(),
            client);

        await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
        Assert.Null(session.World.Objects.FindFirst(0x10203042));
    }

    [Theory]
    [InlineData(Net7FeatureFlags.None)]
    [InlineData(Net7FeatureFlags.UdpPacketOptimisation)]
    public async Task DirectOuterCreateRemoveWithoutGameplayTargetDoNotMaterializeClientWorld(Net7FeatureFlags featureFlags)
    {
        await using var harness = await EntryHarness.CreateAsync(featureFlags: featureFlags);
        int suppressedCount = 0;
        var suppressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(
            harness.Options,
            trace: message =>
            {
                if (message.Contains("WORLD-01 direct outer", StringComparison.Ordinal)
                    && message.Contains("no active gameplay client target", StringComparison.Ordinal)
                    && Interlocked.Increment(ref suppressedCount) == 3)
                {
                    suppressed.TrySetResult(true);
                }
            });

        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        const uint gameId = 0x10203040;
        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");

        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x0004, 0xAABBCCDD, 126, BuildCreatePayload(gameId)).ToWireBytes(),
            client);
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x0004, 0xAABBCCDD, 127, BuildCreatePayload(gameId)).ToWireBytes(),
            client);
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x0007, 0xAABBCCDD, 128, BuildRemovePayload(gameId)).ToWireBytes(),
            client);

        await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
        Assert.Null(session.World.Objects.FindFirst(gameId));
        Assert.Equal(0, session.World.Objects.Count);
    }

    [Fact]
    public async Task RelationshipFamilyWithoutGameplayTargetDoesNotMutateClientWorld()
    {
        await using var harness = await EntryHarness.CreateAsync();
        int suppressedCount = 0;
        var suppressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(
            harness.Options,
            trace: message =>
            {
                if (message.Contains("WORLD-07 direct outer UDP", StringComparison.Ordinal)
                    && message.Contains("relationship materialization suppressed", StringComparison.Ordinal)
                    && Interlocked.Increment(ref suppressedCount) == 3)
                {
                    suppressed.TrySetResult(true);
                }
            });

        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        const uint gameId = 0x10203043;
        session.World.MaterializeCreate(new Net7CreateUpdate(gameId, 1f, 1, 1, 0f, 0f, 0f));
        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");

        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x0089, 0xAABBCCDD, 130, BuildRelationshipPayload(gameId, 3, 1)).ToWireBytes(), client);
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x008A, 0xAABBCCDD, 131, BuildReactionBatchPayload(gameId, 0)).ToWireBytes(), client);
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x008B, 0xAABBCCDD, 132, BuildAttackerBatchPayload(gameId, 1)).ToWireBytes(), client);

        await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Net7WorldObject obj = session.World.Objects.FindFirst(gameId)!;
        Assert.Equal((uint)Net7Reaction.Shun, obj.RelationshipRaw);
        Assert.Equal((byte)0, obj.IsAttacking);
    }

    [Fact]
    public async Task NavigationFamilyWithoutGameplayTargetDoesNotMutateClientWorld()
    {
        await using var harness = await EntryHarness.CreateAsync();
        int suppressedCount = 0;
        var suppressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(
            harness.Options,
            trace: message =>
            {
                if (message.Contains("WORLD-08 direct outer UDP", StringComparison.Ordinal)
                    && message.Contains("navigation materialization suppressed", StringComparison.Ordinal)
                    && Interlocked.Increment(ref suppressedCount) == 2)
                {
                    suppressed.TrySetResult(true);
                }
            });

        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        const uint gameId = 0x10203044;
        session.World.MaterializeCreate(new Net7CreateUpdate(gameId, 1f, 1, 1, 0f, 0f, 0f));
        byte[] initial = BuildNavigationPayload(gameId, 25f, 1, 1, 0);
        _ = session.World.Apply(Packet.ForOpcode(0x0099, initial), 0);

        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x0099, 0xAABBCCDD, 133, BuildNavigationPayload(gameId, 999f, 0, 7, 1)).ToWireBytes(), client);
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x009A, 0xAABBCCDD, 134, BuildRemovePayload(gameId)).ToWireBytes(), client);

        await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Net7NavigationUpdate nav = Assert.IsType<Net7NavigationUpdate>(session.World.Objects.FindFirst(gameId)!.Navigation);
        Assert.Equal(25f, nav.Signature);
        Assert.Equal(1u, nav.NavType);
    }

    [Fact]
    public async Task AuxDataFamilyWithoutGameplayTargetDoesNotMutateClientRoots()
    {
        await using var harness = await EntryHarness.CreateAsync();
        int suppressedCount = 0;
        var suppressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(
            harness.Options,
            trace: message =>
            {
                if (message.Contains("WORLD-09 direct outer UDP", StringComparison.Ordinal)
                    && message.Contains("AuxData materialization suppressed", StringComparison.Ordinal)
                    && Interlocked.Increment(ref suppressedCount) == 2)
                {
                    suppressed.TrySetResult(true);
                }
            });

        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Net7AuxDataProperty local = session.World.LocalPlayerAuxData.Resolve(2);
        Net7AuxDataProperty playerVar = session.World.PlayerVar0AuxData.Resolve(4);
        object? originalLocal = local.Value;
        object? originalPlayerVar = playerVar.Value;
        int originalLocalState = local.State;
        int originalPlayerVarState = playerVar.State;

        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x001B, 0xAABBCCDD, 135, BuildAuxEnvelope(0, BuildDefaultInt32Delta(2, 1234))).ToWireBytes(), client);
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x001C, 0xAABBCCDD, 136, BuildAuxEnvelope(0, BuildDefaultInt32Delta(4, 5678))).ToWireBytes(), client);

        await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
        Assert.Equal(originalLocalState, local.State);
        Assert.Equal(originalLocal, local.Value);
        Assert.Equal(originalPlayerVarState, playerVar.State);
        Assert.Equal(originalPlayerVar, playerVar.Value);
    }

    [Fact]
    public async Task FullEnb03OrdinaryFlow_ReachesStableSectorEntryWithExactOrdering()
    {
        await using var harness = await EntryHarness.CreateAsync();
        var clock = new TestClock([0x11111111, 0x22222222]);
        await using var session = new Net7Session(harness.Options, clock: clock);
        var sent = new List<(ushort Opcode, string Channel, uint Sequence)>();
        session.UdpFrameSent += (frame, channel) => sent.Add((frame.Opcode, channel, frame.Sequence));

        Task server = harness.RunAccountThroughSelectionAsync();
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await server;

        Net7GalaxyMapData map = Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap());
        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(map);

        UdpReceiveResult master200F = await harness.MasterUdp.ReceiveAsync();
        ServerUdpFrame p200F = ServerUdpFrame.Parse(master200F.Buffer);
        Assert.Equal(0x200F, p200F.Opcode);
        Assert.Equal(0xAABBCCDDu, p200F.FixedSessionId);
        Assert.Empty(p200F.Payload.ToArray());

        UdpReceiveResult req2008 = await harness.HandoffUdp.ReceiveAsync();
        ServerUdpFrame p2008 = ServerUdpFrame.Parse(req2008.Buffer);
        Assert.Equal(0x2008, p2008.Opcode);
        Assert.Equal(0xAABBCCDDu, p2008.FixedSessionId);
        Assert.Equal(4, p2008.Payload.Length);
        Assert.Equal(1079u, BinaryPrimitives.ReadUInt32LittleEndian(p2008.Payload.Span));
        Assert.Equal(0u, p2008.Sequence);

        byte[] masterReply = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(masterReply.AsSpan(0, 4), 0x12345678);
        BinaryPrimitives.WriteUInt16LittleEndian(masterReply.AsSpan(4, 2), checked((ushort)harness.GameplayPort));
        await harness.HandoffUdp.SendAsync(
            new ServerUdpFrame(0x2009, 0xAABBCCDD, 90, masterReply).ToWireBytes(),
            req2008.RemoteEndPoint);

        UdpReceiveResult sectorLoginDatagram = await harness.GameplayUdp.ReceiveAsync();
        ServerUdpFrame sectorLogin = ServerUdpFrame.Parse(sectorLoginDatagram.Buffer);
        Assert.Equal(0x0002, sectorLogin.Opcode);
        Assert.Equal(Net7SectorLoginWire.PayloadSize, sectorLogin.Payload.Length);
        Assert.Equal(0xAABBCCDDu, sectorLogin.FixedSessionId);
        Assert.Equal(1u, sectorLogin.Sequence);
        byte[] expectedJoin = Net7SectorJoinClientData.CreateSuccessful(0xAABBCCDD, 1079, 2).ToWireBytes();
        Assert.Equal(expectedJoin, sectorLogin.Payload.Span[..0x40].ToArray());
        Assert.Equal(0x11111111u, BinaryPrimitives.ReadUInt32LittleEndian(sectorLogin.Payload.Span.Slice(0x40, 4)));
        Assert.All(sectorLogin.Payload.Span.Slice(0x44, 0x41).ToArray(), b => Assert.Equal((byte)0, b));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(sectorLogin.Payload.Span.Slice(0x85, 4)));

        // Current integrated startup reaches a real server-side
        // in-sector session from Start/StartAck without any outer 0x2010/0x2011.
        byte[] startPayload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(startPayload, 0xCAFEBABE);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x0005, 0xAABBCCDD, 91, startPayload).ToWireBytes(),
            sectorLoginDatagram.RemoteEndPoint);

        ServerUdpFrame requestTime = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync()).Buffer);
        ServerUdpFrame startAck = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync()).Buffer);
        ServerUdpFrame synthetic = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync()).Buffer);
        Assert.Equal(new ushort[] { 0x0044, 0x0006, 0x3004 }, new ushort[] { requestTime.Opcode, startAck.Opcode, synthetic.Opcode });
        Assert.Equal(new uint[] { 2, 3, 4 }, new uint[] { requestTime.Sequence, startAck.Sequence, synthetic.Sequence });
        Assert.Equal(0x22222222u, BinaryPrimitives.ReadUInt32LittleEndian(requestTime.Payload.Span));
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32LittleEndian(startAck.Payload.Span));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(synthetic.Payload.Span));

        Net7RouteResult route = await enter;
        Assert.Equal(harness.GameplayPort, route.GameplaySectorEndPoint.Port);
        Assert.Equal(0x12345678u, route.RetainedMasterResponseDword);
        Assert.Equal(0xCAFEBABEu, session.PlayerId);
        Assert.Equal(0xCAFEBABEu, session.World.LocalPlayerId);
        Assert.Same(map, session.GalaxyMap);
        Assert.Equal(Net7LoginState.SectorReady, session.LoginState);
        Assert.Equal(harness.GameplayPort, session.ActiveGameplaySectorPort);

        Assert.Contains(sent, x => x.Opcode == 0x200F && x.Channel == "master/control");
        Assert.Equal(new ushort[] { 0x2008, 0x0002, 0x0044, 0x0006, 0x3004 },
            sent.Where(x => x.Channel.StartsWith("routed->", StringComparison.Ordinal)).Select(x => x.Opcode).ToArray());

        // Clearing only the control ID leaves routed header identity intact.
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x2005, 0, 92, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            master200F.RemoteEndPoint);
        ServerUdpFrame zeroControlAck = ServerUdpFrame.Parse((await ReceiveWithinAsync(harness.MasterUdp)).Buffer);
        Assert.Equal(0x200F, zeroControlAck.Opcode);
        Assert.Equal(0u, zeroControlAck.FixedSessionId);

        // There is no duplicate suppression. A second admitted Start
        // repeats Request_Time, StartAck and the synthetic companion with fresh
        // routed sequences, ignoring bytes after the player ID.
        startPayload = [.. startPayload, 0xFE, 0xDC];
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x0005, 0xAABBCCDD, 93, startPayload).ToWireBytes(),
            sectorLoginDatagram.RemoteEndPoint);
        ServerUdpFrame duplicateTime = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync()).Buffer);
        ServerUdpFrame duplicateAck = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync()).Buffer);
        ServerUdpFrame duplicateSynthetic = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync()).Buffer);
        Assert.Equal(
            new ushort[] { 0x0044, 0x0006, 0x3004 },
            new ushort[] { duplicateTime.Opcode, duplicateAck.Opcode, duplicateSynthetic.Opcode });
        Assert.Equal(
            new uint[] { 5, 6, 7 },
            new uint[] { duplicateTime.Sequence, duplicateAck.Sequence, duplicateSynthetic.Sequence });

        Assert.Equal(0xAABBCCDDu, duplicateSynthetic.FixedSessionId);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(duplicateSynthetic.Payload.Span));
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32LittleEndian(duplicateAck.Payload.Span));

        // Valid SetClientType is delivered to the active gameplay
        // client, arms native presentation work, and autonomously emits the
        // unique Code-30 A3 on the next presentation pass.
        byte[] sectorType = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sectorType, 1);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x003C, 0xAABBCCDD, 94, sectorType).ToWireBytes(),
            sectorLoginDatagram.RemoteEndPoint);

        ServerUdpFrame presentationA3 = ServerUdpFrame.Parse((await ReceiveWithinAsync(harness.GameplayUdp)).Buffer);
        Assert.Equal(0x00A3, presentationA3.Opcode);
        Assert.Equal(8u, presentationA3.Sequence);
        Assert.Equal(18, presentationA3.Payload.Length);
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32LittleEndian(presentationA3.Payload.Span[..4]));
        Assert.Equal(30u, BinaryPrimitives.ReadUInt32LittleEndian(presentationA3.Payload.Span.Slice(4, 4)));
        Assert.All(presentationA3.Payload.Span[8..].ToArray(), b => Assert.Equal((byte)0, b));
        Assert.Equal(1u, session.Gameplay.SectorType);
        Assert.Equal<int?>(1, session.Gameplay.PresentationMode);
        Assert.False(session.Gameplay.PresentationNotificationPending);
        for (int i = 0; i < 100 && session.Gameplay.PresentationRefreshCountdown != 0; i++)
            await Task.Delay(1);
        Assert.Equal(0, session.Gameplay.PresentationRefreshCountdown);
    }

    [Fact]
    public async Task MasterLoginRetriesWithFreshRoutedSequenceUntilOwned2009Arrives()
    {
        await using var harness = await EntryHarness.CreateAsync(masterAttempts: 3, pollInterval: TimeSpan.FromMilliseconds(20));
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Net7GalaxyMapData map = Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap());
        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(map);
        _ = await harness.MasterUdp.ReceiveAsync();

        UdpReceiveResult first = await harness.HandoffUdp.ReceiveAsync();
        ServerUdpFrame p1 = ServerUdpFrame.Parse(first.Buffer);
        Assert.Equal(0x2008, p1.Opcode);
        UdpReceiveResult second = await harness.HandoffUdp.ReceiveAsync();
        ServerUdpFrame p2 = ServerUdpFrame.Parse(second.Buffer);
        Assert.Equal(0x2008, p2.Opcode);
        Assert.Equal(p1.Sequence + 1, p2.Sequence);

        byte[] masterReply = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(masterReply.AsSpan(4, 2), checked((ushort)harness.GameplayPort));
        await harness.HandoffUdp.SendAsync(
            new ServerUdpFrame(0x2009, 0xAABBCCDD, 5, masterReply).ToWireBytes(),
            second.RemoteEndPoint);

        UdpReceiveResult sectorLogin = await harness.GameplayUdp.ReceiveAsync();
        await CompleteEntryServerSideAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD);
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        await enter;
    }

    [Fact]
    public async Task InvalidInitialMasterPortUses2026AndAcceptsOnlyValid2027()
    {
        await using var harness = await EntryHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        _ = await harness.MasterUdp.ReceiveAsync();
        UdpReceiveResult masterReq = await harness.HandoffUdp.ReceiveAsync();

        byte[] invalid = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(4, 2), 3499);
        await harness.HandoffUdp.SendAsync(
            new ServerUdpFrame(0x2009, 0xAABBCCDD, 7, invalid).ToWireBytes(), masterReq.RemoteEndPoint);

        UdpReceiveResult confirm = await harness.HandoffUdp.ReceiveAsync();
        ServerUdpFrame p2026 = ServerUdpFrame.Parse(confirm.Buffer);
        Assert.Equal(0x2026, p2026.Opcode);
        Assert.Equal(1u, p2026.Sequence);
        Assert.Equal(1079u, BinaryPrimitives.ReadUInt32LittleEndian(p2026.Payload.Span));

        // Four rejected responses exceed the former three-transaction product cap.
        for (int i = 0; i < 4; i++)
        {
            await harness.HandoffUdp.SendAsync(
                new ServerUdpFrame(0x2027, 0xAABBCCDD, (uint)(20 + i), new byte[] { 0, 0 }).ToWireBytes(), confirm.RemoteEndPoint);
            confirm = await harness.HandoffUdp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0x2026, ServerUdpFrame.Parse(confirm.Buffer).Opcode);
            Assert.False(enter.IsCompleted);
        }

        // A reply too short for its port is no valid confirmation either: proxy
        // 00401820 reads the port through 004144A0's stored buffer pointer, so the
        // bytes are undefined there (safety rule) and confirmation continues.
        await harness.HandoffUdp.SendAsync(
            new ServerUdpFrame(0x2027, 0xAABBCCDD, 30, new byte[] { 0x7F }).ToWireBytes(), confirm.RemoteEndPoint);
        confirm = await harness.HandoffUdp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0x2026, ServerUdpFrame.Parse(confirm.Buffer).Opcode);
        Assert.False(enter.IsCompleted);

        byte[] valid = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(valid, checked((ushort)harness.GameplayPort));
        await harness.HandoffUdp.SendAsync(
            new ServerUdpFrame(0x2027, 0xAABBCCDD, 8, valid).ToWireBytes(), confirm.RemoteEndPoint);

        UdpReceiveResult sectorLogin = await harness.GameplayUdp.ReceiveAsync();
        await CompleteEntryServerSideAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD);
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        Assert.Equal(harness.GameplayPort, (await enter).GameplaySectorEndPoint.Port);
    }

    [Fact]
    public async Task UnknownMasterCapabilityBitFailsBeforeAnyMasterTraffic()
    {
        await using var harness = await EntryHarness.CreateAsync(featureFlags: (Net7FeatureFlags)0x08);
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await session.EnterSelectedCharacterAsync(
                Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap())));

        using var noTraffic = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await harness.MasterUdp.ReceiveAsync(noTraffic.Token));
        using var noHandoff = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await harness.HandoffUdp.ReceiveAsync(noHandoff.Token));
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
    }

    [Theory]
    [InlineData(Net7FeatureFlags.UdpPacketOptimisation, 0x01)]
    [InlineData(Net7FeatureFlags.SpMode, 0x02)]
    [InlineData(Net7FeatureFlags.SectorCaching, 0x04)]
    public async Task OptionalMasterCapabilitiesAreAdvertisedInExact2008FeatureByte(
        Net7FeatureFlags featureFlags,
        byte expectedFeatureByte)
    {
        await using var harness = await EntryHarness.CreateAsync(featureFlags: featureFlags);
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()),
            stop.Token);

        ServerUdpFrame master200F = ServerUdpFrame.Parse((await harness.MasterUdp.ReceiveAsync(stop.Token)).Buffer);
        Assert.Equal(0x200F, master200F.Opcode);
        ServerUdpFrame request = ServerUdpFrame.Parse((await harness.HandoffUdp.ReceiveAsync(stop.Token)).Buffer);
        Assert.Equal(0x2008, request.Opcode);
        Assert.Equal(5, request.Payload.Length);
        Assert.Equal(1079u, BinaryPrimitives.ReadUInt32LittleEndian(request.Payload.Span[..4]));
        Assert.Equal(expectedFeatureByte, request.Payload.Span[4]);

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { _ = await enter; });
        Assert.Equal(Net7LoginState.Faulted, session.LoginState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(65535)]
    public async Task Embedded2010Inner0097IsAnAlternateFinalCompletionForm(ushort unusedLength)
    {
        await using var harness = await EntryHarness.CreateAsync();
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var session = new Net7Session(harness.Options, trace: trace.Enqueue);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        _ = await harness.MasterUdp.ReceiveAsync();
        UdpReceiveResult masterReq = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, masterReq.RemoteEndPoint, 0x10203040);

        UdpReceiveResult sectorLogin = await harness.GameplayUdp.ReceiveAsync();
        byte[] sentinel = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(sentinel.AsSpan(0, 2), unusedLength);
        BinaryPrimitives.WriteUInt16LittleEndian(sentinel.AsSpan(2, 2), 0x0097);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2010, 0xAABBCCDD, 20, sentinel).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await SendStartAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD, 21, 0x01020304);

        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        Assert.Equal(Net7LoginState.SectorReady, (await AwaitAndReturnSessionStateAsync(enter, session)));
        Assert.Contains(trace, message => message.Contains("final login complete <- routed UDP 0x2010 / inner 0x0097 sentinel"));
    }

    [Fact]
    public async Task DirectMasterControl2011CanOwnFinalCompletion()
    {
        await using var harness = await EntryHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        UdpReceiveResult master200F = await harness.MasterUdp.ReceiveAsync();
        UdpReceiveResult masterReq = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, masterReq.RemoteEndPoint, 0x55667788);

        UdpReceiveResult sectorLogin = await harness.GameplayUdp.ReceiveAsync();
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x2011, 0xAABBCCDD, 44, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            master200F.RemoteEndPoint);
        await SendStartAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD, 45, 0xA0B0C0D0);

        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        await enter;
        Assert.Equal(Net7LoginState.SectorReady, session.LoginState);
    }

    [Fact]
    public async Task IntermediateLoginWatchdogUsesFourFixed2022CyclesAndOne1008ThenReturnsToLogin()
    {
        var clock = new WatchdogStepClock();
        await using var harness = await EntryHarness.CreateAsync(
            watchdogPolls: 1,
            watchdogInterval: WatchdogStepClock.Interval);
        await using var session = new Net7Session(harness.Options, clock: clock);
        async Task<UdpReceiveResult> Receive(UdpClient socket) => await socket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        UdpReceiveResult master200F = await Receive(harness.MasterUdp);
        ServerUdpFrame normal200F = ServerUdpFrame.Parse(master200F.Buffer);
        UdpReceiveResult masterReq = await Receive(harness.HandoffUdp);
        await SendValidMasterReplyAsync(harness, masterReq.RemoteEndPoint, 0x01010101);

        UdpReceiveResult sectorLogin = await Receive(harness.GameplayUdp);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2011, 0xAABBCCDD, 50, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        // Deliberately withhold Start, so neither incoming Start nor outgoing
        // StartAck can satisfy the intermediate-login watchdog.

        await clock.StepAsync();
        ServerUdpFrame retry1 = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        await clock.StepAsync();
        ServerUdpFrame retry2 = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        await clock.StepAsync();
        ServerUdpFrame retry3 = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        await clock.StepAsync();
        ServerUdpFrame retry4 = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        ServerUdpFrame failure = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        Assert.Equal(
            new ushort[] { 0x2022, 0x2022, 0x2022, 0x2022, 0x1008 },
            new ushort[] { retry1.Opcode, retry2.Opcode, retry3.Opcode, retry4.Opcode, failure.Opcode });
        Assert.All(new[] { retry1, retry2, retry3, retry4, failure }, frame =>
        {
            Assert.Equal(0xAABBCCDDu, frame.FixedSessionId);
            Assert.Equal(normal200F.Sequence, frame.Sequence);
            Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span));
        });

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(async () => await enter);
        Assert.Contains("watchdog", ex.Message.ToLowerInvariant());
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        Assert.Equal(0xAABBCCDDu, session.FixedSessionId);
        Assert.Equal(((IPEndPoint)harness.GameplayUdp.Client.LocalEndPoint!).Port, session.ActiveGameplaySectorPort);
        Assert.Null(session.CharacterSelection);

        // Failure removes the client target, not the proxy's routed UDP owner.
        // Its control response remains reachable until ordinary session disposal.
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2005, 0xAABBCCDD, 51, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        ServerUdpFrame retainedResponse = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        Assert.Equal(0x200F, retainedResponse.Opcode);
        Assert.Equal(0xAABBCCDDu, retainedResponse.FixedSessionId);
    }

    [Fact]
    public async Task IntermediateLoginWatchdogRereadsCurrentFixedSessionIdForEachFixedSend()
    {
        var clock=new WatchdogStepClock();
        await using var harness = await EntryHarness.CreateAsync(
            pollInterval: TimeSpan.FromSeconds(10),
            watchdogPolls: 1,
            watchdogInterval: WatchdogStepClock.Interval);
        await using var session = new Net7Session(harness.Options,clock:clock);
        async Task<UdpReceiveResult> Receive(UdpClient socket)=>await socket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        UdpReceiveResult initial200FDatagram = await Receive(harness.MasterUdp);
        ServerUdpFrame initial200F = ServerUdpFrame.Parse(initial200FDatagram.Buffer);
        UdpReceiveResult masterReq = await Receive(harness.HandoffUdp);
        await SendValidMasterReplyAsync(harness, masterReq.RemoteEndPoint, 0x01010101);
        _ = await Receive(harness.GameplayUdp);

        // First watchdog send uses the identity established by avatar selection.
        await clock.StepAsync();
        ServerUdpFrame retry1 = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        Assert.Equal(0x2022, retry1.Opcode);
        Assert.Equal(0xAABBCCDDu, retry1.FixedSessionId);
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(retry1.Payload.Span));
        Assert.Equal(initial200F.Sequence, retry1.Sequence);

        // Zero-ID 0x2005 is admitted as an identity clear, and
        // the watchdog rereads master/control +0x10 for every later send,
        // rather than retaining the value captured when the worker started.
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x2005, 0, 99, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            initial200FDatagram.RemoteEndPoint);

        ServerUdpFrame cleared200F = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        Assert.Equal(0x200F, cleared200F.Opcode);
        Assert.Equal(0u, cleared200F.FixedSessionId);

        await clock.StepAsync();
        ServerUdpFrame retry2 = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        await clock.StepAsync();
        ServerUdpFrame retry3 = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        await clock.StepAsync();
        ServerUdpFrame retry4 = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        ServerUdpFrame failure = ServerUdpFrame.Parse((await Receive(harness.MasterUdp)).Buffer);
        Assert.Equal(
            new ushort[] { 0x2022, 0x2022, 0x2022, 0x1008 },
            new ushort[] { retry2.Opcode, retry3.Opcode, retry4.Opcode, failure.Opcode });
        Assert.All(new[] { retry2, retry3, retry4 }, frame =>
        {
            Assert.Equal(0u, frame.FixedSessionId);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span));
            Assert.Equal(cleared200F.Sequence, frame.Sequence);
        });

        // Failure helper004147C0 receives the routed owner, unlike watchdog00414860.
        Assert.Equal(0xAABBCCDDu, failure.FixedSessionId);
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(failure.Payload.Span));
        Assert.Equal(cleared200F.Sequence, failure.Sequence);

        _ = await Assert.ThrowsAsync<TimeoutException>(async () => await enter);
        Assert.Equal(Net7LoginState.CharacterSelection, session.LoginState);
        Assert.Equal(0xAABBCCDDu, session.FixedSessionId);
        Assert.Equal(((IPEndPoint)harness.GameplayUdp.Client.LocalEndPoint!).Port, session.ActiveGameplaySectorPort);
    }

    [Fact]
    public async Task ReliabilityCacheIngressBeforeGameplayRouteIsIgnoredAndCannotSatisfyMasterTransaction()
    {
        await using var harness = await EntryHarness.CreateAsync(
            pollInterval: TimeSpan.FromMilliseconds(150));
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        _ = await harness.MasterUdp.ReceiveAsync();
        UdpReceiveResult masterReq = await harness.HandoffUdp.ReceiveAsync();

        // Outside active login/gameplay forwarding and outside gate-cache
        // mode, login-cache ingress is suppressed. It must not satisfy or fault the
        // concurrent Master 0x2009 transaction.
        await harness.HandoffUdp.SendAsync(
            new ServerUdpFrame(0x2016, 0xAABBCCDD, 0, new byte[8]).ToWireBytes(), masterReq.RemoteEndPoint);
        await Task.Delay(30);
        Assert.Equal(uint.MaxValue, session.ReliabilityState!.Cursor);

        await SendValidMasterReplyAsync(harness, masterReq.RemoteEndPoint, 0x01020304);
        UdpReceiveResult sectorLogin = await harness.GameplayUdp.ReceiveAsync();
        Assert.Equal(uint.MaxValue, session.ReliabilityState!.Cursor);

        await CompleteEntryServerSideAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD);
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();

        _ = await enter;
        Assert.Equal(Net7LoginState.SectorReady, session.LoginState);
    }

    [Fact]
    public async Task Reliability2016OwnsDecoded2020WhileDirectOuter2020UsesRawGenericFallback()
    {
        await using var harness = await EntryHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        _ = await harness.MasterUdp.ReceiveAsync();
        UdpReceiveResult masterReq = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, masterReq.RemoteEndPoint, 0x01020304);

        UdpReceiveResult sectorLogin = await harness.GameplayUdp.ReceiveAsync();
        Assert.Equal(uint.MaxValue, session.ReliabilityState!.Cursor);

        var rawDirect = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x2020)
                rawDirect.TrySetResult(packet);
        };

        byte[] stage = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(stage, 0x55667788);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2020, 0xAABBCCDD, 50, stage).ToWireBytes(),
            sectorLogin.RemoteEndPoint);

        Packet directPacket = await rawDirect.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal((ushort)0x2020, directPacket.Header.Opcode);
        Assert.Equal(stage, directPacket.Payload.ToArray());

        using (var noReply = new CancellationTokenSource(TimeSpan.FromMilliseconds(80)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                _ = await harness.GameplayUdp.ReceiveAsync(noReply.Token);
            });
        }
        Assert.Equal(uint.MaxValue, session.ReliabilityState!.Cursor);

        byte[] inner2020 = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(inner2020.AsSpan(0, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(inner2020.AsSpan(2, 2), 0x2020);
        stage.CopyTo(inner2020, 4);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2016, 0xAABBCCDD, 0, inner2020).ToWireBytes(),
            sectorLogin.RemoteEndPoint);

        ServerUdpFrame stageAck = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync()).Buffer);
        Assert.Equal(0x2021, stageAck.Opcode);
        Assert.Equal(2u, stageAck.Sequence);
        Assert.Equal(0x55667788u, BinaryPrimitives.ReadUInt32LittleEndian(stageAck.Payload.Span));

        // Its handling emits 0x2021 from inside decoded-inner dispatch. The
        // sequence entry is marked processed and the cursor advances only after
        // that dispatch returns, so the fake UDP peer can observe the ACK a few
        // scheduler ticks before Cursor becomes 1. Wait boundedly for the
        // post-dispatch bookkeeping instead of racing it.
        await WaitUntilAsync(() => session.ReliabilityState!.Cursor == 1u);
        Assert.Equal(1u, session.ReliabilityState!.Cursor);

        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2011, 0xAABBCCDD, 51, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await SendStartAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD, 52, 0xCAFEBABE);
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        await enter;
        Assert.Equal(Net7LoginState.SectorReady, session.LoginState);
    }


    [Fact]
    public async Task DirectOuter2025IsRawGenericTraffic_NotDecodedCDataSpecial()
    {
        await using var harness = await EntryHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        _ = await harness.MasterUdp.ReceiveAsync();
        UdpReceiveResult masterReq = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, masterReq.RemoteEndPoint, 0x01020304);

        UdpReceiveResult sectorLogin = await harness.GameplayUdp.ReceiveAsync();
        var rawDirect = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x2025)
                rawDirect.TrySetResult(packet);
        };

        byte[] recordId = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(recordId, 0x11223344);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2025, 0xAABBCCDD, 60, recordId).ToWireBytes(),
            sectorLogin.RemoteEndPoint);

        Packet directPacket = await rawDirect.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(recordId, directPacket.Payload.ToArray());
        Assert.Null(session.Gameplay.LastCDataRecord);
        Assert.Equal(uint.MaxValue, session.ReliabilityState!.Cursor);

        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2011, 0xAABBCCDD, 61, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await SendStartAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD, 62, 0xCAFEBABE);
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        await enter;
    }

    [Fact]
    public async Task World2018TransportContext_DirectOuterDoesNotMaterialize_DecodedInnerDoes()
    {
        await using var harness = await EntryHarness.CreateAsync();
        int directOuterConsumedCount = 0;
        var directOuterConsumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(
            harness.Options,
            trace: message =>
            {
                if (message.Contains("direct outer", StringComparison.Ordinal)
                    && message.Contains("current Net7Proxy scratch bundle is not flushed", StringComparison.Ordinal)
                    && Interlocked.Increment(ref directOuterConsumedCount) == 2)
                {
                    directOuterConsumed.TrySetResult(true);
                }
            });
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        _ = await harness.MasterUdp.ReceiveAsync();
        UdpReceiveResult masterRequest = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, masterRequest.RemoteEndPoint, 0x01020304);
        UdpReceiveResult sectorLogin = await harness.GameplayUdp.ReceiveAsync();

        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2018, 0xAABBCCDD, 89, new byte[59]).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await Task.Delay(25);
        Assert.Equal(0, session.World.Objects.Count);

        byte[] direct2018 = BuildWorld2018Payload(0x0A0B0C0D, "DirectWorldA");
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2018, 0xAABBCCDD, 90, direct2018).ToWireBytes(),
            sectorLogin.RemoteEndPoint);

        byte[] masterDirect2018 = BuildWorld2018Payload(0x0A0B0C0F, "MasterDirectWorldA");
        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x2018, 0xAABBCCDD, 91, masterDirect2018).ToWireBytes(),
            harness.MasterClientEndPoint
                ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint."));

        await directOuterConsumed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(session.World.Objects.FindFirst(0x0A0B0C0D));
        Assert.Null(session.World.Objects.FindFirst(0x0A0B0C0F));
        Assert.Equal(0, session.World.Objects.Count);

        byte[] malformedInner = BuildInnerFrame(0x2018, new byte[59]);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2016, 0xAABBCCDD, 0, malformedInner).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        for (int i = 0; i < 50 && session.ReliabilityState!.Cursor != 1; i++)
            await Task.Delay(10);
        Assert.Equal(1u, session.ReliabilityState!.Cursor);
        Assert.Equal(0, session.World.Objects.Count);

        byte[] decoded2018 = BuildWorld2018Payload(0x0A0B0C0E, "DecodedWorldA");
        byte[] inner = BuildInnerFrame(0x2018, decoded2018);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2016, 0xAABBCCDD, 1, inner).ToWireBytes(),
            sectorLogin.RemoteEndPoint);

        for (int i = 0; i < 50 && session.World.Objects.FindFirst(0x0A0B0C0E) is null; i++)
            await Task.Delay(10);

        Assert.Equal("DecodedWorldA", session.World.Objects.FindFirst(0x0A0B0C0E)!.Name);
        Assert.Equal(1, session.World.Objects.Count);
        Assert.Equal(2u, session.ReliabilityState!.Cursor);

        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2011, 0xAABBCCDD, 92, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await SendStartAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD, 93, 0xCAFEBABE);
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        await enter;
    }

    [Fact]
    public async Task World2019TransportContext_DirectOuterStaysRawAndDecodedInnerMaterializes()
    {
        await using var harness = await EntryHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        _ = await harness.MasterUdp.ReceiveAsync();
        UdpReceiveResult masterRequest = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, masterRequest.RemoteEndPoint, 0x01020304);
        UdpReceiveResult sectorLogin = await harness.GameplayUdp.ReceiveAsync();

        const uint directId = 0x0A0B0C11;
        var rawDirect = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x2019)
                rawDirect.TrySetResult(packet);
        };

        byte[] direct2019 = BuildWorld2019Payload(directId, "DirectRawB");
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2019, 0xAABBCCDD, 93, direct2019).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        Packet raw = await rawDirect.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(direct2019, raw.Payload.ToArray());
        Assert.Null(session.World.Objects.FindFirst(directId));

        byte[] malformedInner = BuildInnerFrame(0x2019, new byte[47]);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2016, 0xAABBCCDD, 0, malformedInner).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() => session.ReliabilityState!.Cursor == 1);
        Assert.Equal(1u, session.ReliabilityState!.Cursor);
        Assert.Equal(0, session.World.Objects.Count);

        const uint decodedId = 0x0A0B0C12;
        byte[] decoded2019 = BuildWorld2019Payload(decodedId, "DecodedOre");
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2016, 0xAABBCCDD, 1, BuildInnerFrame(0x2019, decoded2019)).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        //Creation precedes its AuxData fill; observe completed dispatch, not
        //the transient presence of the newly allocated world object.
        await WaitUntilAsync(() => session.World.Objects.FindFirst(decodedId)?.Name == "DecodedOre" && session.ReliabilityState!.Cursor == 2);

        Net7WorldObject decoded = session.World.Objects.FindFirst(decodedId)!;
        Assert.Equal((byte)0x26, decoded.ObjectType);
        Assert.Equal("DecodedOre", decoded.Name);
        Net7AuxDataSchema aux = Assert.IsType<Net7AuxDataSchema>(decoded.AuxData);
        Net7AuxDataString name = Assert.IsType<Net7AuxDataString>(aux.Root.Children[0].Value);
        Assert.Equal("DecodedOre", Encoding.Latin1.GetString(name.Bytes));
        Assert.Equal(2, aux.Root.Children[0].State);
        Assert.Equal(2u, session.ReliabilityState!.Cursor);

        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2011, 0xAABBCCDD, 94, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await SendStartAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD, 95, 0xCAFEBABE);
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        await enter;
    }

    [Fact]
    public async Task Enb06GameplayExtensionsMaterializeNativelyAcrossDirectAndDecodedContexts()
    {
        await using var harness = await EntryHarness.CreateAsync();
        // Snapshot the result after the translator completes, before its timed
        // effect can expire. LastProspect is assigned before the message/effect.
        var prospectCompleted = new TaskCompletionSource<(uint? EffectId, bool Message, bool Effect)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Net7Session? observedSession = null;
        await using var session = new Net7Session(harness.Options, message =>
        {
            if (message == "routed UDP 0x2012 -> ENB-06 direct translator owner")
            {
                var gameplay = observedSession!.Gameplay;
                prospectCompleted.TrySetResult((gameplay.LastProspect?.EffectId,
                    gameplay.Messages.Any(item => item.Text == "Prospect ability activated."),
                    gameplay.Effects.ContainsKey(0x33333333)));
            }
        });
        observedSession = session;
        session.SetCDataCatalog(Net7CDataCatalogTests.BuildCatalogForSession());
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        _ = await ReceiveWithinAsync(harness.MasterUdp);
        UdpReceiveResult masterRequest = await ReceiveWithinAsync(harness.HandoffUdp);
        await SendValidMasterReplyAsync(harness, masterRequest.RemoteEndPoint, 0x01020304);
        UdpReceiveResult sectorLogin = await ReceiveWithinAsync(harness.GameplayUdp);

        byte[] prospect = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(prospect.AsSpan(0, 4), 0xAABBCCDD);
        BinaryPrimitives.WriteUInt32LittleEndian(prospect.AsSpan(4, 4), 0x22222222);
        BinaryPrimitives.WriteUInt32LittleEndian(prospect.AsSpan(8, 4), 0x33333333);
        BinaryPrimitives.WriteUInt32LittleEndian(prospect.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(prospect.AsSpan(16, 4), 1000);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2012, 0xAABBCCDD, 80, prospect).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        var completedProspect = await prospectCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0x33333333u, completedProspect.EffectId);
        Assert.True(completedProspect.Message);
        Assert.True(completedProspect.Effect);

        byte[] tractorB = Net7GameplayWireTests.BuildTractor(
            0x2014, "TractorB", 1000, 40f, new System.Numerics.Vector3(10, 20, 30),
            basset: 0x44444444, effect: 0x45454545);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2014, 0xAABBCCDD, 81, tractorB).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() =>
            session.World.Objects.FindFirst(0x44444444)?.Name == "TractorB" &&
            session.Gameplay.CameraEvents.Count == 2);
        Assert.Equal(2, session.Gameplay.CameraEvents.Count);
        Assert.False(session.Gameplay.TractorVariantAValidityApplied);

        byte[] tractorA = Net7GameplayWireTests.BuildTractor(
            0x2013, "Trac\0torA", 1000, 50f, new System.Numerics.Vector3(30, 40, 50),
            basset: 0x55555555, effect: 0x56565656);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2016, 0xAABBCCDD, 0, BuildInnerFrame(0x2013, tractorA)).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() =>
            session.Gameplay.LastTractor?.Opcode == 0x2013 &&
            session.World.Objects.FindFirst(0x55555555)?.Name == "Trac" &&
            session.Gameplay.CameraEvents.Count == 4);
        Assert.True(session.Gameplay.TractorVariantAValidityApplied);
        Assert.Equal("Trac", session.World.Objects.FindFirst(0x55555555)!.Name);
        Assert.Equal(4, session.Gameplay.CameraEvents.Count);
        Assert.Equal(2, session.World.LocalPlayerAuxData.Resolve(Net7AuxDataPropertyIds.LocalPlayerSkillAbility0Validity).State);
        Assert.Equal(Convert.FromHexString("00010000"), Assert.IsType<byte[]>(session.World.LocalPlayerAuxData.Resolve(Net7AuxDataPropertyIds.LocalPlayerSkillAbility0Validity).Value));
        Assert.Equal(2, session.World.LocalPlayerAuxData.Resolve(Net7AuxDataPropertyIds.LocalPlayerSkillAbility28Validity).State);

        byte[] cdataRequest = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(cdataRequest, 0x11223344);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2016, 0xAABBCCDD, 1, BuildInnerFrame(0x2025, cdataRequest)).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() => session.ItemTemplates.TryGet(0x11223344u, out _));
        Assert.Equal(Net7CDataCatalogTests.BuildSessionItemTemplatePayload(), session.Gameplay.LastCDataRecord!.Payload);
        Assert.True(session.ItemTemplates.TryGet(0x11223344u, out Net7ItemTemplate? sessionTemplate));
        Assert.Equal("Synthetic Session Item", Assert.IsType<Net7ItemTemplate>(sessionTemplate).Name.EffectiveText);

        byte[] missingCDataRequest = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(missingCDataRequest, 0x55667788);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2016, 0xAABBCCDD, 2, BuildInnerFrame(0x2025, missingCDataRequest)).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() => session.ReliabilityState!.Cursor == 3);
        Assert.Equal(0x11223344u, session.Gameplay.LastCDataRecord!.RecordId);
        Assert.Single(session.ItemTemplates.Items);

        var rawCData = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x2025)
                rawCData.TrySetResult(true);
        };
        byte[] directCData = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(directCData, 0xDEADBEEF);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2025, 0xAABBCCDD, 82, directCData).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await rawCData.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0x11223344u, session.Gameplay.LastCDataRecord!.RecordId);

        byte[] create = BuildCreatePayload(0xAABBCCDD);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x0004, 0xAABBCCDD, 83, create).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() => session.World.Objects.FindFirst(0xAABBCCDD) is not null);

        byte[] warp = new byte[36];
        WriteSingle(warp, 0, 100f);
        WriteSingle(warp, 4, 200f);
        WriteSingle(warp, 8, 300f);
        WriteQuaternion(warp, 12, System.Numerics.Quaternion.Identity);
        BinaryPrimitives.WriteUInt32LittleEndian(warp.AsSpan(28, 4), 0x01020304);
        BinaryPrimitives.WriteUInt32LittleEndian(warp.AsSpan(32, 4), 0x05060708);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2016, 0xAABBCCDD, 3, BuildInnerFrame(0x202A, warp)).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await WaitUntilAsync(
            () => session.World.Objects.FindFirst(0xAABBCCDD)?.Spatial?.Advanced?.MovementId == 0x01020304,
            TimeSpan.FromSeconds(2));
        Assert.Equal(new System.Numerics.Vector3(100, 200, 300),
            session.World.Objects.FindFirst(0xAABBCCDD)!.Spatial!.Advanced!.Position);

        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2011, 0xAABBCCDD, 91, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            sectorLogin.RemoteEndPoint);
        await SendStartAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD, 92, 0xCAFEBABE);
        _ = await ReceiveWithinAsync(harness.GameplayUdp);
        _ = await ReceiveWithinAsync(harness.GameplayUdp);
        _ = await ReceiveWithinAsync(harness.GameplayUdp);
        await enter.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await session.RequestLogoffAsync(Net7LogoffDestination.CharacterSelection));
        UdpReceiveResult logoffDatagram = await ReceiveWithinAsync(harness.GameplayUdp);
        ServerUdpFrame logoffFrame = ServerUdpFrame.Parse(logoffDatagram.Buffer);
        Assert.Equal(0x00B9, logoffFrame.Opcode);
        Assert.Equal(Convert.FromHexString("BEBAFECA"), logoffFrame.Payload.ToArray());

        // Another test already covers the negative case that legacy 0x00BB and
        // 0x003D are not accepted as current-session logoff substitutes.
        // Synchronize only on the proven graceful lifecycle:
        // 0x00B9 request -> 0x00BA confirmation.
        Assert.Equal(Net7LoginState.SectorReady, session.LoginState);
        Assert.Equal(0xAABBCCDDu, session.FixedSessionId);
        Assert.Equal(Net7LogoffDestination.CharacterSelection, session.PendingLogoffDestination);

        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x00BA, 0xAABBCCDD, 93, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            logoffDatagram.RemoteEndPoint);
        await WaitUntilAsync(() => session.LoginState == Net7LoginState.CharacterSelection);
        Assert.Equal(0u, session.FixedSessionId);
        Assert.Equal(Net7LogoffDestination.CharacterSelection, session.LastConfirmedLogoffDestination);
        Assert.Equal(2, session.LastConfirmedLogoffTransitionState);
        Assert.Null(session.PlayerId);
        Assert.Null(session.World.LocalPlayerId);
    }

    [Fact]
    public async Task GracefulLogoffPreflightFailureDoesNotArmPendingTransaction()
    {
        await using var session = new Net7Session(new Net7ServerOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RequestLogoffAsync(Net7LogoffDestination.CharacterSelection));

        Assert.Null(session.PendingLogoffDestination);
        Assert.Null(session.LastConfirmedLogoffDestination);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoffConfirmationWithoutLocalPendingRequestStillClearsAuthoritativeSessionState(bool trailingBytes)
    {
        await using var harness = await EntryHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> enter = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        _ = await harness.MasterUdp.ReceiveAsync();
        UdpReceiveResult masterRequest = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, masterRequest.RemoteEndPoint, 0x01020304);
        UdpReceiveResult sectorLogin = await harness.GameplayUdp.ReceiveAsync();
        await CompleteEntryServerSideAsync(harness.GameplayUdp, sectorLogin.RemoteEndPoint, 0xAABBCCDD);
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await enter;

        Assert.Equal(Net7LoginState.SectorReady, session.LoginState);
        Assert.Equal(0xAABBCCDDu, session.FixedSessionId);
        Assert.Null(session.PendingLogoffDestination);

        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x00BA, 0xAABBCCDD, 96, trailingBytes ? new byte[] { 0xFF, 0x7F } : ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            sectorLogin.RemoteEndPoint);

        await WaitUntilAsync(() => session.LoginState == Net7LoginState.CharacterSelection);
        Assert.Equal(0u, session.FixedSessionId);
        Assert.Null(session.PendingLogoffDestination);
        Assert.Null(session.LastConfirmedLogoffDestination);
        Assert.Null(session.LastConfirmedLogoffTransitionState);
        Assert.Null(session.PlayerId);
        Assert.Null(session.World.LocalPlayerId);

        // The server-owned 0x00BA teardown must leave no active gameplay path.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RequestLogoffAsync(Net7LogoffDestination.CharacterSelection));
    }

    [Theory]
    [InlineData(0xAABBCCDDu)]
    [InlineData(0x11223344u)]
    public async Task ServerHandoffReturnsToJoinAndReusesExactNextSectorJoinData(uint nextIdentity)
    {
        await using var harness = await EntryHarness.CreateAsync();
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var session = new Net7Session(harness.Options, trace.Enqueue);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        Task<Net7RouteResult> firstEntry = session.EnterSelectedCharacterAsync(
            Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap()));
        _ = await harness.MasterUdp.ReceiveAsync();
        UdpReceiveResult firstMaster = await harness.HandoffUdp.ReceiveAsync();
        await SendValidMasterReplyAsync(harness, firstMaster.RemoteEndPoint, 0x11111111);
        UdpReceiveResult firstSectorLogin = await harness.GameplayUdp.ReceiveAsync();
        await CompleteEntryServerSideAsync(harness.GameplayUdp, firstSectorLogin.RemoteEndPoint, 0xAABBCCDD);
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        _ = await harness.GameplayUdp.ReceiveAsync();
        await firstEntry;

        Net7SectorJoinClientData nextJoin = Net7SectorJoinClientData.CreateSuccessful(nextIdentity, 10000, 2);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x003A, 0xAABBCCDD, 100, BuildServerHandoffPayload(nextJoin)).ToWireBytes(),
            firstSectorLogin.RemoteEndPoint);

        // PendingHandoff is published before the same receive path invalidates
        // Route/LoginState. Observe the complete handoff boundary, not its first write.
        await WaitUntilAsync(() => session.World.PendingHandoff is not null &&
            session.LoginState != Net7LoginState.SectorReady && session.Route is null, TimeSpan.FromSeconds(2));
        Assert.True(session.World.IsTransitioning);
        Assert.Equal(10000u, session.World.PendingHandoff!.NextJoinData.SelectedSector);
        Assert.NotEqual(Net7LoginState.SectorReady, session.LoginState);
        Assert.Null(session.Route);

        // Retail ClientTask result 4 automatically returns to JOIN on
        // its next iteration. No UI/test command should be required to continue.
        ServerUdpFrame second200F = ServerUdpFrame.Parse((await ReceiveWithinAsync(harness.MasterUdp)).Buffer);
        Assert.Equal(0x200F, second200F.Opcode);
        Assert.Equal(nextIdentity, second200F.FixedSessionId);
        UdpReceiveResult secondMasterDatagram = await ReceiveWithinAsync(harness.HandoffUdp);
        ServerUdpFrame second2008 = ServerUdpFrame.Parse(secondMasterDatagram.Buffer);
        Assert.Equal(0x2008, second2008.Opcode);
        Assert.Equal(nextIdentity, second2008.FixedSessionId);
        Assert.Equal(10000u, BinaryPrimitives.ReadUInt32LittleEndian(second2008.Payload.Span[..4]));
        // The old gameplay connection survives JOIN while outgoing forwarding
        // is suspended. Direct and decoded-inner updates still reach the client.
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x0004, nextIdentity, 102, BuildCreatePayload(0x12345678)).ToWireBytes(),
            firstSectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() => session.World.Objects.FindFirst(0x12345678) is not null);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2016, nextIdentity, 0,
                BuildInnerFrame(0x0089, BuildRelationshipPayload(0x12345678, 3, 1))).ToWireBytes(),
            firstSectorLogin.RemoteEndPoint);
        await WaitUntilAsync(() => session.World.Objects.FindFirst(0x12345678)?.RelationshipRaw == 3);
        await SendValidMasterReplyAsync(harness, secondMasterDatagram.RemoteEndPoint, 0x22222222, nextIdentity);

        UdpReceiveResult secondSectorLoginDatagram;
        try { secondSectorLoginDatagram = await ReceiveWithinAsync(harness.GameplayUdp); }
        catch (OperationCanceledException error)
        {
            throw new InvalidOperationException("Second-sector login was not received. Session trace:\n" +
                string.Join("\n", trace), error);
        }
        ServerUdpFrame secondSectorLogin = ServerUdpFrame.Parse(secondSectorLoginDatagram.Buffer);
        Assert.Equal(0x0002, secondSectorLogin.Opcode);
        Assert.Equal(nextJoin.ToWireBytes(), secondSectorLogin.Payload.Span[..0x40].ToArray());

        // Start clears the retail-equivalent
        // loading/transition state and current integrated readiness completes
        // from Start/StartAck itself. Outer 0x2010/0x2011 remain supported
        // compatibility forms, but this path must not fabricate either one.
        await SendStartAsync(
            harness.GameplayUdp, secondSectorLoginDatagram.RemoteEndPoint, nextIdentity, 11, 0x01020304);
        ServerUdpFrame secondRequestTime = ServerUdpFrame.Parse((await ReceiveWithinAsync(harness.GameplayUdp)).Buffer);
        ServerUdpFrame secondStartAck = ServerUdpFrame.Parse((await ReceiveWithinAsync(harness.GameplayUdp)).Buffer);
        ServerUdpFrame secondSynthetic = ServerUdpFrame.Parse((await ReceiveWithinAsync(harness.GameplayUdp)).Buffer);
        Assert.Equal(new ushort[] { 0x0044, 0x0006, 0x3008 },
            new ushort[] { secondRequestTime.Opcode, secondStartAck.Opcode, secondSynthetic.Opcode });
        Assert.False(session.World.IsTransitioning);

        await WaitUntilAsync(() => session.LoginState == Net7LoginState.SectorReady, TimeSpan.FromSeconds(2));
        Net7RouteResult secondRoute = session.Route
            ?? throw new InvalidOperationException("Automatic ENTRY-08 JOIN restart did not publish the new route.");

        Assert.Equal(10000u, secondRoute.SectorJoinData.SelectedSector);
        Assert.Equal(0x22222222u, secondRoute.RetainedMasterResponseDword);
        Assert.Equal(Net7LoginState.SectorReady, session.LoginState);
        Assert.False(session.World.IsTransitioning);
        Assert.Null(session.World.PendingHandoff);
    }

    [Fact]
    public async Task DirectOuterHandoffWithoutGameplayTargetMutatesNoClientWorldOrJoinState()
    {
        await using var harness = await EntryHarness.CreateAsync();
        await using var session = new Net7Session(harness.Options);
        await session.LoginAsync("pilot", "password");
        await session.SelectCharacterAsync(0);
        await harness.AccountServer;

        IPEndPoint client = harness.MasterClientEndPoint
            ?? throw new InvalidOperationException("Bootstrap did not publish the master/control client endpoint.");
        Net7SectorJoinClientData nextJoin = Net7SectorJoinClientData.CreateSuccessful(0xAABBCCDD, 10000, 2);

        await harness.MasterUdp.SendAsync(
            new ServerUdpFrame(0x003A, 0xAABBCCDD, 101, BuildServerHandoffPayload(nextJoin)).ToWireBytes(),
            client);

        await Task.Delay(50);
        Assert.Equal(Net7LoginState.CharacterSelected, session.LoginState);
        Assert.False(session.World.IsTransitioning);
        Assert.Null(session.World.PendingHandoff);
        Assert.Null(session.Route);
    }

    private static byte[] BuildCreatePayload(uint gameId)
    {
        byte[] payload = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        WriteSingle(payload, 4, 1f);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(8, 2), 1);
        payload[10] = 1;
        WriteSingle(payload, 11, 0f);
        WriteSingle(payload, 15, 0f);
        WriteSingle(payload, 19, 0f);
        return payload;
    }

    private static byte[] BuildRemovePayload(uint gameId)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, gameId);
        return payload;
    }

    private static byte[] BuildNavigationPayload(uint gameId, float signature, byte visited, uint navType, byte isHuge)
    {
        byte[] payload = new byte[14];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), BitConverter.SingleToInt32Bits(signature));
        payload[8] = visited;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(9, 4), navType);
        payload[13] = isHuge;
        return payload;
    }

    private static byte[] BuildRelationshipPayload(uint gameId, uint reaction, byte isAttacking)
    {
        byte[] payload = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), gameId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), reaction);
        payload[8] = isAttacking;
        return payload;
    }

    private static byte[] BuildReactionBatchPayload(uint gameId, uint reaction)
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), gameId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), reaction);
        return payload;
    }

    private static byte[] BuildAttackerBatchPayload(uint gameId, byte isAttacking)
    {
        byte[] payload = new byte[9];
        payload[0] = isAttacking;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5, 4), gameId);
        return payload;
    }

    private static byte[] BuildFixedPositionPayload(uint gameId)
    {
        byte[] payload = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        WriteSingle(payload, 4, 10f);
        WriteQuaternion(payload, 16, System.Numerics.Quaternion.Identity);
        return payload;
    }

    private static byte[] BuildComponentPositionPayload(uint gameId, uint timestamp)
    {
        byte[] payload = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), timestamp);
        WriteSingle(payload, 8, 10f);
        WriteQuaternion(payload, 20, System.Numerics.Quaternion.Identity);
        WriteSingle(payload, 48, 9.8f);
        return payload;
    }

    private static byte[] BuildSimplePositionPayload(uint gameId, uint timestamp)
    {
        byte[] payload = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), timestamp);
        WriteQuaternion(payload, 20, System.Numerics.Quaternion.Identity);
        return payload;
    }

    private static byte[] BuildAdvancedPositionPayload(uint gameId, uint timestamp)
    {
        byte[] payload = new byte[42];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2, 4), gameId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6, 4), timestamp);
        WriteQuaternion(payload, 22, System.Numerics.Quaternion.Identity);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(38, 4), 1);
        return payload;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(1));
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for ENB-06 loopback state.");
            await Task.Delay(10);
        }
    }

    private static async Task<UdpReceiveResult> ReceiveWithinAsync(UdpClient socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await socket.ReceiveAsync(timeout.Token);
    }

    private static byte[] BuildServerHandoffPayload(Net7SectorJoinClientData join)
    {
        using var stream = new MemoryStream();
        stream.Write(join.ToWireBytes());
        byte[] length = new byte[2];
        foreach (string value in new[] { "OldSector", "OldSystem", "NewSector", "NewSystem" })
        {
            byte[] bytes = Encoding.Latin1.GetBytes(value);
            BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    private static byte[] BuildWorld2018Payload(uint gameId, string name)
    {
        byte[] nameBytes = Encoding.Latin1.GetBytes(name);
        byte[] payload = new byte[60 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        payload[4] = 0x25;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5, 2), 7);
        WriteSingle(payload, 7, 1f);
        payload[23] = 2;
        WriteQuaternion(payload, 37, System.Numerics.Quaternion.Identity);
        WriteSingle(payload, 53, 100f);
        payload[57] = 0x30;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(58, 2), checked((ushort)nameBytes.Length));
        nameBytes.CopyTo(payload, 60);
        return payload;
    }

    private static byte[] BuildWorld2019Payload(uint gameId, string name)
    {
        byte[] nameBytes = Encoding.Latin1.GetBytes(name);
        byte[] payload = new byte[48 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), 9);
        WriteSingle(payload, 6, 2f);
        WriteSingle(payload, 10, 0.25f);
        WriteSingle(payload, 14, 0.75f);
        WriteSingle(payload, 18, 4f);
        WriteSingle(payload, 22, 5f);
        WriteSingle(payload, 26, 6f);
        WriteQuaternion(payload, 30, System.Numerics.Quaternion.Identity);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(46, 2), checked((ushort)nameBytes.Length));
        nameBytes.CopyTo(payload, 48);
        return payload;
    }

    private static byte[] BuildDefaultInt32Delta(uint propertyId, int value)
    {
        byte[] delta = new byte[21];
        int offset = 0;
        delta[offset++] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(delta.AsSpan(offset, 4), 1); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(delta.AsSpan(offset, 4), propertyId); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(delta.AsSpan(offset, 4), value); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(delta.AsSpan(offset, 4), 0); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(delta.AsSpan(offset, 4), 0);
        return delta;
    }

    private static byte[] BuildAuxEnvelope(uint target, byte[] delta)
    {
        byte[] payload = new byte[6 + delta.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), target);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), checked((ushort)delta.Length));
        delta.CopyTo(payload, 6);
        return payload;
    }

    private static byte[] BuildInnerFrame(ushort opcode, byte[] payload)
    {
        byte[] inner = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(inner.AsSpan(0, 2), checked((ushort)inner.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(inner.AsSpan(2, 2), opcode);
        payload.CopyTo(inner, 4);
        return inner;
    }

    private static void WriteSingle(byte[] bytes, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));

    private static void WriteQuaternion(byte[] bytes, int offset, System.Numerics.Quaternion value)
    {
        WriteSingle(bytes, offset, value.X);
        WriteSingle(bytes, offset + 4, value.Y);
        WriteSingle(bytes, offset + 8, value.Z);
        WriteSingle(bytes, offset + 12, value.W);
    }

    private static async Task SendValidMasterReplyAsync(EntryHarness harness, IPEndPoint client, uint retainedValue, uint fixedId = 0xAABBCCDD)
    {
        byte[] reply = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(0, 4), retainedValue);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(4, 2), checked((ushort)harness.GameplayPort));
        await harness.HandoffUdp.SendAsync(
            new ServerUdpFrame(0x2009, fixedId, 19, reply).ToWireBytes(), client);
    }

    private static async Task SendStartAsync(UdpClient gameplay, IPEndPoint client, uint fixedId, uint sequence, uint playerId)
    {
        byte[] start = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(start, playerId);
        await gameplay.SendAsync(new ServerUdpFrame(0x0005, fixedId, sequence, start).ToWireBytes(), client);
    }

    private static async Task<Net7LoginState> AwaitAndReturnSessionStateAsync(Task<Net7RouteResult> enter, Net7Session session)
    {
        _ = await enter;
        return session.LoginState;
    }

    private static async Task CompleteEntryServerSideAsync(UdpClient gameplay, IPEndPoint client, uint fixedId)
    {
        await gameplay.SendAsync(new ServerUdpFrame(0x2011, fixedId, 10, ReadOnlyMemory<byte>.Empty).ToWireBytes(), client);
        byte[] start = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(start, 0x01020304);
        await gameplay.SendAsync(new ServerUdpFrame(0x0005, fixedId, 11, start).ToWireBytes(), client);
    }

    private sealed class TestClock : INet7RuntimeClock
    {
        private readonly Queue<uint> _times;
        public TestClock(IEnumerable<uint> times) => _times = new Queue<uint>(times);
        public uint GetMilliseconds() => _times.Count == 0 ? 0x33333333 : _times.Dequeue();
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Task.Delay(delay, cancellationToken);
    }

    private sealed class WatchdogStepClock : INet7RuntimeClock
    {
        public static TimeSpan Interval => TimeSpan.FromMinutes(1);
        private readonly SystemNet7RuntimeClock _system=new();
        private readonly System.Threading.Channels.Channel<TaskCompletionSource> _polls=
            System.Threading.Channels.Channel.CreateUnbounded<TaskCompletionSource>();
        public uint GetMilliseconds()=>_system.GetMilliseconds();
        public Task DelayAsync(TimeSpan delay,CancellationToken cancellationToken=default)
        {
            if(delay!=Interval)return _system.DelayAsync(delay,cancellationToken);
            var pending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _polls.Writer.TryWrite(pending);
            return pending.Task.WaitAsync(cancellationToken);
        }
        public async Task StepAsync()
        {
            var pending=await _polls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            pending.SetResult();
        }
    }

    private sealed class EntryHarness : IAsyncDisposable
    {
        private readonly TcpListener _accountListener;

        private EntryHarness(
            UdpClient masterUdp,
            UdpClient handoffUdp,
            UdpClient gameplayUdp,
            TcpListener accountListener,
            Net7ServerOptions options)
        {
            MasterUdp = masterUdp;
            HandoffUdp = handoffUdp;
            GameplayUdp = gameplayUdp;
            _accountListener = accountListener;
            Options = options;
        }

        public UdpClient MasterUdp { get; }
        public UdpClient HandoffUdp { get; }
        public UdpClient GameplayUdp { get; }
        public int GameplayPort => ((IPEndPoint)GameplayUdp.Client.LocalEndPoint!).Port;
        public IPEndPoint? MasterClientEndPoint { get; private set; }
        public Net7ServerOptions Options { get; }
        public Task AccountServer { get; private set; } = Task.CompletedTask;

        public static Task<EntryHarness> CreateAsync(
            int masterAttempts = 2,
            TimeSpan? pollInterval = null,
            Net7FeatureFlags featureFlags = Net7FeatureFlags.None,
            int watchdogSends = 4,
            int watchdogPolls = 20,
            TimeSpan? watchdogInterval = null)
        {
            var master = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var handoff = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var gameplay = BindGameplayPort();
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();

            var options = new Net7ServerOptions
            {
                Host = "127.0.0.1",
                MasterControlUdpPort = ((IPEndPoint)master.Client.LocalEndPoint!).Port,
                AccountTcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port,
                MasterHandoffUdpPort = ((IPEndPoint)handoff.Client.LocalEndPoint!).Port,
                BootstrapAttempts = 2,
                // This loopback harness participates in the full-suite run.
                // Keep protocol retry-count semantics exact while giving the fake server
                // enough scheduler headroom to answer the bootstrap datagram.
                BootstrapAttemptTimeout = TimeSpan.FromSeconds(10),
                AccountResponseTimeout = TimeSpan.FromSeconds(10),
                MasterRequestAttempts = masterAttempts,
                MasterResponsePollsPerAttempt = 1,
                MasterResponsePollInterval = pollInterval ?? TimeSpan.FromMilliseconds(40),
                IntermediateLoginWatchdogSends = watchdogSends,
                IntermediateLoginPollsPerCycle = watchdogPolls,
                IntermediateLoginPollInterval = watchdogInterval ?? TimeSpan.FromMilliseconds(50),
                FeatureFlags = featureFlags,
            };
            var harness = new EntryHarness(master, handoff, gameplay, tcp, options);
            harness.AccountServer = harness.RunAccountThroughSelectionAsync();
            return Task.FromResult(harness);
        }

        public Task RunAccountThroughSelectionAsync()
        {
            if (!AccountServer.IsCompleted)
                return AccountServer;
            return RunAccountScenarioAsync();
        }

        private async Task RunAccountScenarioAsync()
        {
            UdpReceiveResult bootstrapRequest = await MasterUdp.ReceiveAsync();
            MasterClientEndPoint = (IPEndPoint)bootstrapRequest.RemoteEndPoint;
            ServerUdpFrame bootstrap = ServerUdpFrame.Parse(bootstrapRequest.Buffer);
            Assert.Equal(0x1000, bootstrap.Opcode);
            await MasterUdp.SendAsync(
                new ServerUdpFrame(0x1001, 0, 123, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
                bootstrapRequest.RemoteEndPoint);

            using TcpClient client = await _accountListener.AcceptTcpClientAsync();
            using NetworkStream stream = client.GetStream();
            Assert.Equal(0x3002, (await ReadPacketAsync(stream)).Header.Opcode);
            Assert.Equal(0x2010, (await ReadPacketAsync(stream)).Header.Opcode);
            Assert.Equal(0x2000, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes("Valid=TRUE\nTicket=pilot-token\n"));
            Assert.Equal(0x2002, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster());
            Assert.Equal(0x2004, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x2009, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 0xAABBCCDD));

            // No further account response is owned after avatar selection. The
            // client still closes/unpublishes its local account connection on the
            // 0x3004 branch; the fixture need not manufacture a waiter.
        }

        private static UdpClient BindGameplayPort()
        {
            for (int port = 3501; port <= 4000; port++)
            {
                try { return new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); }
                catch (SocketException) { }
            }
            throw new InvalidOperationException("No loopback gameplay port available in 3501..4000.");
        }

        public async ValueTask DisposeAsync()
        {
            _accountListener.Stop();
            MasterUdp.Dispose();
            HandoffUdp.Dispose();
            GameplayUdp.Dispose();
            try { await AccountServer; } catch { }
        }

        private static async Task<Packet> ReadPacketAsync(NetworkStream stream)
        {
            byte[] headerBytes = new byte[PacketHeader.WireSize];
            await ReadExactAsync(stream, headerBytes);
            PacketHeader header = PacketHeader.Read(headerBytes);
            byte[] payload = new byte[header.PayloadLength];
            await ReadExactAsync(stream, payload);
            return new Packet(header, payload);
        }

        private static Task WritePacketAsync(NetworkStream stream, ushort opcode, ReadOnlyMemory<byte> payload)
            => stream.WriteAsync(Packet.ForOpcode(opcode, payload).ToWireBytes()).AsTask();

        private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> destination)
        {
            int offset = 0;
            while (offset < destination.Length)
            {
                int read = await stream.ReadAsync(destination[offset..]);
                if (read == 0)
                    throw new EndOfStreamException("Loopback account client disconnected unexpectedly.");
                offset += read;
            }
        }
    }
}
