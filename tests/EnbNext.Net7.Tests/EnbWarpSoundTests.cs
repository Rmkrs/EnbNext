using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbWarpSoundTests
{
    [Fact]
    public void AuxUpdatesJournalDetachedTransitionsAndIgnoreUnrelatedChanges()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(23, 1, 10, 1, 0, 0, 0));
        void Set(uint id, int value)
        {
            using var delta = new MemoryStream();
            using var d = new BinaryWriter(delta);
            d.Write((byte)0); d.Write(1u); d.Write(id); d.Write(value); d.Write(0u); d.Write(0u);
            using var payload = new MemoryStream();
            using var p = new BinaryWriter(payload);
            p.Write(23u); p.Write((ushort)delta.Length); p.Write(delta.ToArray());
            world.Apply(Packet.ForOpcode(0x001B, payload.ToArray()), 0);
        }
        Set(1910, 4); Set(1922, 14); Set(39, 1);
        var builder = new EnbPresentationSnapshotBuilder();
        var before = builder.Build(world, null, 0).Objects[0].ShipPresentation!;
        Set(38, 6); Set(39, 2); Set(39, 2); Set(39, 8);
        var after = builder.Build(world, null, 0).Objects[0].ShipPresentation!;
        Assert.Single(before.WarpChanges);
        Assert.Equal(new[] { 1, 2, 8 }, after.WarpChanges.Select(c => c.State));
        Assert.All(after.WarpChanges, c => { Assert.Equal(4, c.PowerLevel); Assert.Equal(14, c.EngineTrailType); });
        Assert.Empty(before.PrivateWarpChanges);
        Assert.Equal(new[] { 6 }, after.PrivateWarpChanges.Select(c => c.State));
        Set(38, 2); Set(38, 2); Set(38, 3);
        var privateAfter = builder.Build(world, null, 0).Objects[0].ShipPresentation!;
        Assert.Equal(new[] { 6, 2, 3 }, privateAfter.PrivateWarpChanges.Select(c => c.State));
        Assert.Equal(after.WarpChanges.ToArray(), privateAfter.WarpChanges.ToArray());
        Assert.Single(after.PrivateWarpChanges); // Detached snapshot remains unchanged.
        for (int i = 0; i < 100; i++) Set(39, i % 2);
        Assert.Equal(32, builder.Build(world, null, 0).Objects[0].ShipPresentation!.WarpChanges.Length);
    }

    private static EnbSceneObjectSnapshot Ship()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(23, 1, 10, 1, 0, 0, 0));
        return new EnbPresentationSnapshotBuilder().Build(world, null, 0).Objects[0] with
        { IsLocalShip = true, ShipPresentation = new(false, 0, 0, 0, false, null, 0, [], []) };
    }

    [Theory]
    [InlineData(3)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(11)]
    public void ReportedLifecyclePreloadsThenPlaysThenStopsLoops(int exit)
    {
        var runtime = new EnbWarpSoundRuntime();
        var ship = Ship();
        var changes = ImmutableArray<EnbSceneWarpPresentationChange>.Empty;
        IReadOnlyList<EnbCurrentRetailEffectSoundNode> State(int state)
        {
            changes = changes.Add(new((ulong)changes.Length + 1, state, 3, 14));
            ship = ship with { ShipPresentation = ship.ShipPresentation! with { WarpChanges = changes } };
            return runtime.Update([ship]);
        }
        Assert.Empty(State(0));
        var charge = State(1);
        Assert.Equal("warp_start_3", charge[0].Definition.Sfx);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.Is2D(charge[0].Definition));
        Assert.All(charge.Skip(1), n => Assert.False(n.Active));
        Assert.Equal("engine_loop_36", charge[1].Definition.Sfx);
        Assert.Equal(2.5f, charge[1].RadiusMultiplier);
        var active = State(2);
        Assert.All(active, n => Assert.True(n.Active));
        Assert.Equal(active, runtime.Update([ship]));
        Assert.Equal(charge[1].Sequence, active[^2].Sequence);
        var ended = State(exit);
        Assert.DoesNotContain(ended, n => n.Definition.Sfx!.Contains("loop") || n.Definition.Sfx == "Warp_Secondary");
        Assert.Equal($"warp_exit_3", ended[^1].Definition.Sfx);
        Assert.Empty(runtime.Update([]));
    }

    [Fact]
    public void BatchedTransitionsSurviveButPrivateStateAndRepeatedFramesCreateNothing()
    {
        var runtime = new EnbWarpSoundRuntime();
        var ship = Ship();
        runtime.Update([ship]);
        ship = ship with { ShipPresentation = ship.ShipPresentation! with
            { PrivateWarpState = 1, WarpChanges = [new(1, 1, 99, 0), new(2, 2, 99, 0), new(3, 8, 99, 0)] } };
        var nodes = runtime.Update([ship]);
        Assert.Equal(new[] { "warp_start_6", "warp_enter_6", "warp_exit_6" }, nodes.Select(n => n.Definition.Sfx));
        Assert.Equal(nodes, runtime.Update([ship]));
        runtime.Clear();
        Assert.Single(runtime.Update([ship])); // No replay of old trips after context reset.
        Assert.Empty(runtime.Update([ship with { RenderIdentity = 99, ShipPresentation = ship.ShipPresentation with { WarpChanges = [] } }]));
    }

    [Fact]
    public void SoundUnitUsesAllTwelveCrtBitsIncludingBothEndpoints()
    {
        var random = new EnbRetailCrtRandomSource();
        uint state = 1;
        var seen = new HashSet<int>();
        for (int i = 0; i < 100000; i++)
        {
            state = unchecked(state * 214013u + 2531011u);
            int bits = (int)((state >> 16) & 4095);
            seen.Add(bits);
            float expected = (float)(bits * (double)BitConverter.UInt32BitsToSingle(0x39800801));
            Assert.Equal(expected, random.NextUnit());
            Assert.Equal(state, random.State);
        }
        Assert.Equal(4096, seen.Count);
    }

    [Fact]
    public void FactorySamplesSymmetricAuthoredRangesOncePerVoice()
    {
        var d = EnbRetailSoundCatalog.ParseText("[warp]\nSoundFilename=a.wav\nSoundVolume=.55\nSoundMinVolumeShift=.25\nSoundMaxVolumeShift=.25\nSoundMinPitchShift=.25\nSoundMaxPitchShift=.25").GetDefinitionOrFile("warp");
        Assert.Null(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(d));
        Assert.Equal((.3f, .75f), EnbCurrentRetailEffectSoundRuntime.SampleModifiers(d, 0, 0));
        Assert.Equal((BitConverter.UInt32BitsToSingle(0x3f4ccccc), 1.25f), EnbCurrentRetailEffectSoundRuntime.SampleModifiers(d, 1, 1));
        Assert.Equal((.55f, 1f), EnbCurrentRetailEffectSoundRuntime.SampleModifiers(d, .5f, .5f));
    }

    [Fact]
    public void RemoteCuesFollowSourceAndUnrelatedGlobalStatesDoNotInventExit()
    {
        var runtime = new EnbWarpSoundRuntime();
        var ship = Ship() with { IsLocalShip = false };
        ship = ship with { ShipPresentation = ship.ShipPresentation! with { WarpChanges = [new(1, 1, 0, 7)] } };
        var charge = runtime.Update([ship]);
        Assert.Equal("warp_start_1", charge[0].Definition.Sfx);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.Is2D(charge[0].Definition));
        Assert.Equal(1, charge[1].RadiusMultiplier);
        var position = new System.Numerics.Vector3(12, 34, 56);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(charge[0], id => id == ship.GameId ? position : null,
            out var resolved, out bool follows, out _));
        Assert.Equal(position, resolved); Assert.True(follows);
        ship = ship with { ShipPresentation = ship.ShipPresentation with
            { WarpChanges = [new(1, 1, 0, 7), new(2, 2, 0, 7), new(3, 6, 0, 7)] } };
        Assert.Equal(2, runtime.Update([ship]).Count(n => n.Definition.Sfx is "engine_loop_29" or "Warp_Secondary" && n.Active));
    }
}
