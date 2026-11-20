using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Timing;
using EnbNext.Net7.Transport;
using EnbNext.Protocol.Net;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class Net7OrdinaryEffectLifetimeTests
{
    [Theory]
    [InlineData(32100,0)]
    [InlineData(32110,2500)]
    [InlineData(32200,25000)]
    public void StaticSentinelReplaysWithoutWireDuration(int duration,int delay)
    {
        byte[] p=Convert.FromHexString("070A4200404201D10F1200EDA9D4256E7D");
        BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(15),(short)duration);
        var replay=Net7Session.StaticEffectReplay(Packet.ForOpcode(9,p))!.Value;
        Assert.Equal(delay,replay.Delay);
        Assert.Equal(15,replay.Packet.Payload.Length);
        Assert.Equal(3,replay.Packet.Payload.Span[0]);
        Assert.Equal(p.AsSpan(1,10).ToArray(),replay.Packet.Payload.Span.Slice(1,10).ToArray());
        Assert.Equal(unchecked(0x25D4A9EDu+(uint)delay),BinaryPrimitives.ReadUInt32LittleEndian(replay.Packet.Payload.Span[11..]));
        var game=new Net7GameplayState();Assert.True(game.Apply(replay.Packet));
        Assert.Equal(0,game.Effects[1183697].DurationMilliseconds);
    }

    [Fact]
    public async Task StaticSentinelSuppressesUntilDueAndWaitsForClientTarget()
    {
        var clock=new HeldClock();await using var session=new Net7Session(new(),null,clock);
        await Dispatch(session,Packet.ForOpcode(9,Convert.FromHexString("070A4200404201D10F1200EDA9D4256E7D")),"decoded-inner");
        Assert.Empty(session.Gameplay.Effects);
        Assert.Equal(TimeSpan.FromMilliseconds(2501),Assert.Single(clock.Delays).Delay);
        clock.Delays[0].Signal.SetResult();
        var owned=(System.Collections.IEnumerable)typeof(Net7Session).GetField("_ownedOneShotTasks",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
        var task=Assert.Single(owned.Cast<Task>());
        Assert.False(task.IsCompleted);
        Enable(session);await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,session.Gameplay.Effects[1183697].DurationMilliseconds);
        Assert.Single(clock.Delays); // replay must not schedule an ordinary removal.
    }

    [Fact]
    public void StaticReplayQuantizesOnlyAuthoredOffsetShorts()
    {
        byte[] p=new byte[29];Convert.FromHexString("070A4200404201D10F1200EDA9D4256E7D").CopyTo(p,0);p[0]=0x3F;
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(17),BitConverter.SingleToInt32Bits(9));
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(21),BitConverter.SingleToInt32Bits(5.9f));
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(25),BitConverter.SingleToInt32Bits(-2.9f));
        var replay=Net7Session.StaticEffectReplay(Packet.ForOpcode(9,p))!.Value;
        var game=new Net7GameplayState();game.Apply(replay.Packet);var effect=game.Effects[1183697];
        Assert.Equal(1,effect.Scale);Assert.Equal(new System.Numerics.Vector3(.625f,-.25f,0),effect.EffectOffset);
        Assert.Null(Net7Session.StaticEffectReplay(Beam()));
        BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(15),32099);
        Assert.Null(Net7Session.StaticEffectReplay(Packet.ForOpcode(9,p)));
    }

    [Fact]
    public async Task StaticReplayCannotCrossSectorGeneration()
    {
        var clock=new HeldClock();await using var session=new Net7Session(new(),null,clock);Enable(session);
        await Dispatch(session,Packet.ForOpcode(9,Convert.FromHexString("070A4200404201D10F1200EDA9D4256E7D")),"decoded-inner");
        var tasks=((System.Collections.IEnumerable)typeof(Net7Session).GetField("_ownedOneShotTasks",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!).Cast<Task>().ToArray();
        typeof(Net7Session).GetField("_gameplayGeneration",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(session,1);
        clock.Delays[0].Signal.SetResult();await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(session.Gameplay.Effects);
    }

    [Fact]
    public async Task TargetWaitRearmsAfterSessionIdentityClears()
    {
        var transport=new Net7TransportState();var first=transport.WaitForGameplayForwardingAsync(default);
        Assert.False(first.IsCompleted);transport.CommitValidatedFixedSessionId(42);transport.CommitGameplayPort(3501);transport.SetGameplayForwardingActive(true);await first;
        transport.ClearSessionIdentity();var second=transport.WaitForGameplayForwardingAsync(default);Assert.False(second.IsCompleted);
        transport.CommitValidatedFixedSessionId(42);transport.SetGameplayForwardingActive(true);await second;
    }

    [Fact]
    public void InstalledJumpstartRemovalRetiresAllSoundAndVisualDescendants()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not { } paths) return;
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText(File.ReadAllText(Path.Combine(paths.ClientIniDirectory, "effect.ini")));
        var root = new EnbSceneEffectSnapshot(123, 1, 42, 43, 414, 0, 4000, "", 7, true, 1, default, 0, 0, false, default, 1,
            EnbSceneEffectGeometryKind.ObjectToObject, default, -1, default, 0, 0, null);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root], catalog, 0);
        Assert.Contains(scheduler.Update([root], catalog, 1000).SoundNodes, n=>n.Active && n.Definition.DefinitionId==418);
        // Proxy removal may reach the renderer on the same frame as phase expiry.
        scheduler.Update([], catalog, 4000);
        for (uint tick=4100; tick<=7000; tick+=100) scheduler.Update([], catalog, tick);
        var final = scheduler.Update([], catalog, 7100);
        Assert.Empty(final.SoundNodes);
        Assert.Empty(final.VisualNodes);
    }

    [Theory]
    [InlineData("decoded-inner")]
    [InlineData("reassembled decoded-inner")]
    [InlineData("direct outer UDP")]
    public async Task OrdinaryJumpstartHasProxyRemovalWithoutServerRemove(string ingress)
    {
        var clock = new HeldClock();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new Net7Session(new(), text => { if (text.Contains("proxy lifetime removed")) removed.TrySetResult(); }, clock);
        Enable(session);
        await Dispatch(session, Beam(), ingress);
        Assert.Single(session.Gameplay.Effects);
        Assert.Equal(TimeSpan.FromMilliseconds(4000), Assert.Single(clock.Delays).Delay);
        clock.Delays[0].Signal.SetResult();
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        session.ReadPresentationFrame((_, _) => { Assert.Empty(session.Gameplay.Effects); return 0; });
    }

    [Fact]
    public async Task OldRemovalCannotDeleteReusedEffectIdentity()
    {
        var clock = new HeldClock();
        await using var session = new Net7Session(new(), clock: clock);
        Enable(session);
        await Dispatch(session, Beam(), "decoded-inner");
        var old = session.Gameplay.Effects[123];
        await Dispatch(session, Beam(), "decoded-inner");
        Assert.NotEqual(old.InstanceGeneration, session.Gameplay.Effects[123].InstanceGeneration);
        // Exercise the same asynchronous removal body with an already completed delay.
        clock.Immediate = true;
        var method = typeof(Net7Session).GetMethod("RemoveOrdinaryEffectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(session, [old, 4000, 0, CancellationToken.None])!;
        Assert.Single(session.Gameplay.Effects);
    }

    [Fact]
    public void EligibilityPreservesProxyExclusionsAndSeparateStaticPadding()
    {
        var beam = new Net7EffectState(123, 42, 43, 414, 0, 4000, "", 7, true);
        Assert.Equal(4000, Net7Session.OrdinaryEffectRemovalDelay(beam));
        Assert.Equal(4250, Net7Session.OrdinaryEffectRemovalDelay(beam with { IsObjectToObject = false }));
        Assert.Null(Net7Session.OrdinaryEffectRemovalDelay(beam with { DurationMilliseconds = -1 }));
        Assert.Null(Net7Session.OrdinaryEffectRemovalDelay(beam with { DurationMilliseconds = 0 }));
        Assert.Null(Net7Session.OrdinaryEffectRemovalDelay(beam with { BoneName = "TRACTOR" }));
        Assert.Null(Net7Session.OrdinaryEffectRemovalDelay(beam with { EffectOffset = new(0, 0, -1) }));
        Assert.Null(Net7Session.OrdinaryEffectRemovalDelay(beam with { EffectOffset = new(0, 0, 250) }));
        Assert.Null(Net7Session.OrdinaryEffectRemovalDelay(beam with { Mask = 0x807, EffectScalar = 0 }));
        Assert.Equal(4000, Net7Session.OrdinaryEffectRemovalDelay(beam with { EffectScalar = 0 }));
        Assert.Null(Net7Session.OrdinaryEffectRemovalDelay(beam with { IsObjectToObject = false, DurationMilliseconds = 32101 }));
    }

    private static Packet Beam()
    {
        byte[] p = new byte[23];
        p[0] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(6), 43);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(10), 414);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(13), 123);
        BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(21), 4000);
        return Packet.ForOpcode(0x000B, p);
    }
    private static void Enable(Net7Session session)
    {
        var transport = (Net7TransportState)typeof(Net7Session).GetField("_transportState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        transport.CommitValidatedFixedSessionId(42);
        transport.CommitGameplayPort(3501);
        transport.SetGameplayForwardingActive(true);
        typeof(Net7Session).GetField("_gameplayClientTargetActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, true);
    }
    private static Task Dispatch(Net7Session session, Packet packet, string ingress)
        => (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [packet, ingress, CancellationToken.None])!;
    private sealed class HeldClock : INet7RuntimeClock
    {
        public List<(TimeSpan Delay, TaskCompletionSource Signal)> Delays { get; } = [];
        public bool Immediate { get; set; }
        public uint GetMilliseconds() => 0;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            if (Immediate) return Task.CompletedTask;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Delays.Add((delay, signal));
            return signal.Task.WaitAsync(cancellationToken);
        }
    }
}
