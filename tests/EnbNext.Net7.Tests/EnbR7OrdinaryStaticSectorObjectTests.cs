using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR7OrdinaryStaticSectorObjectTests
{
    [Theory]
    [InlineData(1.0f, 0)]
    [InlineData(0.75f, 0)]
    [InlineData(0.7499f, 1)]
    [InlineData(0.30f, 1)]
    [InlineData(0.2999f, 2)]
    [InlineData(0.0001f, 2)]
    [InlineData(0.000099f, 3)]
    [InlineData(0.0f, 3)]
    [InlineData(-1.0f, 3)]
    public void HarvestablePercentFullUsesExactCurrentRetailStateBuckets(float percentFull, int expectedState)
        => Assert.Equal(expectedState, EnbCurrentRetailStaticSectorObjectRuntime.ResolveHarvestableRenderState(percentFull));

    [Fact]
    public void HarvestablePercentFullRejectsNonFiniteValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnbCurrentRetailStaticSectorObjectRuntime.ResolveHarvestableRenderState(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnbCurrentRetailStaticSectorObjectRuntime.ResolveHarvestableRenderState(float.PositiveInfinity));
    }

    [Fact]
    public void FirstFullHarvestableSelectionDoesNotReplayCurrentObjectsLinkChain()
    {
        EnbResolvedRslReference rsl = Rsl("""
            [0\STATE0]
            ID=0
            TYPE=object
            OBJECT=FULL
            LINK=7
            [0\STATE1]
            ID=1
            TYPE=object
            OBJECT=PARTIAL
            LINK=7
            [0\STATE2]
            ID=7
            TYPE=sound
            SOUND=SHIFT
            """, initialState: 0);
        EnbSceneObjectSnapshot obj = Obj(1f);
        var runtime = new EnbCurrentRetailEffectRslObjectRuntime();
        // Initial object construction follows its authored LINK once. The
        // direct same-state selector must preserve that history without replay.
        var initial = runtime.Update(obj, rsl, [], 99);
        var initialActions = initial.Actions.ToArray();
        Assert.Single(initialActions, static action => action.StateId == 7 && action.Opcode == 0x0030);

        EnbCurrentRetailRslDirectStateSelection selection = Assert.IsType<EnbCurrentRetailRslDirectStateSelection>(
            EnbCurrentRetailStaticSectorObjectRuntime.ResolveDirectRslSelection(obj, rsl));
        EnbCurrentRetailEffectRslObjectUpdate update = runtime.Update(obj, rsl, [], 100, selection);

        Assert.Equal(0, update.CurrentObjectStateId);
        Assert.Equal(1, update.Receipt.DirectStateSelectionsApplied);
        Assert.Equal(0, update.Receipt.DirectStateSelectionsRejected);
        Assert.Equal(initialActions, update.Actions);
        // This projection lists render states, not the retained Sound state.
        Assert.DoesNotContain(7, update.ActiveStateIds);
    }

    [Fact]
    public void HarvestableBucketChangeActivatesSelectedStateAndItsAuthoredLinkOnce()
    {
        EnbResolvedRslReference rsl = Rsl("""
            [0\STATE0]
            ID=0
            TYPE=object
            OBJECT=FULL
            LINK=7
            [0\STATE1]
            ID=1
            TYPE=object
            OBJECT=PARTIAL
            LINK=7
            [0\STATE2]
            ID=7
            TYPE=sound
            SOUND=SHIFT
            """, initialState: 0);
        var runtime = new EnbCurrentRetailEffectRslObjectRuntime();

        EnbSceneObjectSnapshot full = Obj(1f);
        EnbCurrentRetailRslDirectStateSelection fullSelection = Assert.IsType<EnbCurrentRetailRslDirectStateSelection>(
            EnbCurrentRetailStaticSectorObjectRuntime.ResolveDirectRslSelection(full, rsl));
        _ = runtime.Update(full, rsl, [], 100, fullSelection);

        EnbSceneObjectSnapshot partial = full with { HarvestablePercentFull = 0.5f };
        EnbCurrentRetailRslDirectStateSelection partialSelection = Assert.IsType<EnbCurrentRetailRslDirectStateSelection>(
            EnbCurrentRetailStaticSectorObjectRuntime.ResolveDirectRslSelection(partial, rsl));
        EnbCurrentRetailEffectRslObjectUpdate changed = runtime.Update(partial, rsl, [], 200, partialSelection);

        Assert.Equal(1, changed.CurrentObjectStateId);
        Assert.Equal(1, changed.Receipt.DirectStateSelectionsApplied);
        Assert.Contains(changed.Actions, static action => action.EffectAuthored && action.Opcode == 0x0030 && action.StateId == 1);
        Assert.Contains(changed.Actions, static action => action.EffectAuthored && action.Opcode == 0x0030 && action.StateId == 7);

        EnbCurrentRetailEffectRslObjectUpdate sameBucket = runtime.Update(partial, rsl, [], 300, partialSelection);
        Assert.Equal(0, sameBucket.Receipt.DirectStateSelectionsApplied);
        Assert.Equal(2, sameBucket.Actions.Count(static action => action.EffectAuthored && action.Opcode == 0x0030));
    }

    [Fact]
    public void BlockedDirectStateSelectionCachesBucketInsteadOfRejectingEveryFrame()
    {
        EnbResolvedRslReference rsl = Rsl("""
            [0\STATE0]
            ID=0
            TYPE=object
            OBJECT=FULL
            [0\STATE1]
            ID=1
            TYPE=object
            OBJECT=PARTIAL
            RS_ENABLED=true
            """, initialState: 0);
        EnbSceneObjectSnapshot obj = Obj(0.5f);
        var runtime = new EnbCurrentRetailEffectRslObjectRuntime();
        EnbCurrentRetailRslDirectStateSelection selection = Assert.IsType<EnbCurrentRetailRslDirectStateSelection>(
            EnbCurrentRetailStaticSectorObjectRuntime.ResolveDirectRslSelection(obj, rsl));
        Assert.False(selection.IsResolvable);

        EnbCurrentRetailEffectRslObjectUpdate first = runtime.Update(obj, rsl, [], 100, selection);
        EnbCurrentRetailEffectRslObjectUpdate second = runtime.Update(obj, rsl, [], 200, selection);

        Assert.Equal(1, first.Receipt.DirectStateSelectionsRejected);
        Assert.Equal(1, first.Receipt.FailClosedEvents);
        Assert.Equal(0, second.Receipt.DirectStateSelectionsRejected);
        Assert.Equal(0, second.Receipt.FailClosedEvents);
        Assert.Equal(0, second.CurrentObjectStateId);
    }

    [Fact]
    public void PresentationSnapshotPublishesMaterializedHarvestablePercentFull()
    {
        var world = new Net7WorldState();
        Net7WorldObject resource = world.MaterializeCreate(new Net7CreateUpdate(42, 1f, 1105, 0x26, 0f, 0f, 0f));
        Net7AuxDataProperty percent = Assert.Single(
            resource.AuxData!.Properties,
            static property => property.Path == "Harvestable.PercentFull");
        percent.State = 2;
        percent.Value = 0.2999f;

        EnbSceneObjectSnapshot snapshot = Assert.Single(new EnbPresentationSnapshotBuilder().Build(world, null, 0).Objects);

        Assert.Equal(0.2999f, snapshot.HarvestablePercentFull);
    }

    [Fact]
    public void Type29KeepsFirstPresentationTransformWhileOrdinaryStaticObjectTracksLaterFixedUpdates()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(41, 1f, 51, 0x29, 0f, 0f, 0f));
        world.MaterializeCreate(new Net7CreateUpdate(42, 1f, 51, 0x25, 0f, 0f, 0f));
        world.Apply(Packet.ForOpcode(0x0040, FixedPayload(41, new Vector3(1, 2, 3))), 0);
        world.Apply(Packet.ForOpcode(0x0040, FixedPayload(42, new Vector3(4, 5, 6))), 0);
        var builder = new EnbPresentationSnapshotBuilder();

        EnbSceneSnapshot first = builder.Build(world, null, 0);
        Assert.Equal(new Vector3(1, 2, 3), first.Objects.Single(static obj => obj.GameId == 41).Spatial!.Transform.Position);
        Assert.Equal(new Vector3(4, 5, 6), first.Objects.Single(static obj => obj.GameId == 42).Spatial!.Transform.Position);

        world.Apply(Packet.ForOpcode(0x0040, FixedPayload(41, new Vector3(10, 20, 30))), 0);
        world.Apply(Packet.ForOpcode(0x0040, FixedPayload(42, new Vector3(40, 50, 60))), 0);
        EnbSceneSnapshot second = builder.Build(world, null, 0);

        Assert.Equal(new Vector3(1, 2, 3), second.Objects.Single(static obj => obj.GameId == 41).Spatial!.Transform.Position);
        Assert.Equal(new Vector3(40, 50, 60), second.Objects.Single(static obj => obj.GameId == 42).Spatial!.Transform.Position);
    }

    private static EnbResolvedRslReference Rsl(string text, int initialState)
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(text);
        Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));
        EnbResolvedRslLibraryResourceGraph graph = new EnbRetailRslResourceResolver(resources: null).Resolve(library);
        return new EnbResolvedRslReference(
            0,
            null,
            initialState,
            library,
            EnbRslRenderStateSemantics.BuildInitialCommands(null, initialState),
            graph);
    }

    private static EnbSceneObjectSnapshot Obj(float percentFull)
        => new(
            RenderIdentity: 1,
            VisualGeneration: 1,
            GameId: 500,
            IsLocalShip: false,
            IsCurrentTarget: false,
            ObjectType: 0x26,
            BaseAsset: 1105,
            Scale: 1f,
            HsvH: 0f,
            HsvS: 0f,
            HsvV: 0f,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: "resource",
            Radar: new EnbSceneRadarSnapshot(false, 0f, null),
            Spatial: null,
            BaseAssetReference: null,
            ClientBaseAssetReference: null,
            Decals: ImmutableArray<EnbSceneDecalSnapshot>.Empty,
            ColorizationEntries: ImmutableArray<EnbSceneColorizationSnapshot>.Empty,
            RenderStateLinks: ImmutableArray<EnbSceneRenderStateLinkSnapshot>.Empty,
            ActiveRenderStates: ImmutableArray<uint>.Empty,
            RenderStateActions: ImmutableArray<EnbSceneRenderStateActionSnapshot>.Empty,
            Attachments: ImmutableArray<EnbSceneAttachmentSnapshot>.Empty,
            NameDecal: null,
            SubParts: ImmutableArray<EnbSceneSubPartSnapshot>.Empty,
            Avatar: null,
            SpecialTargetEndpointId: 0,
            SpecialTargetTransition: 0f)
        {
            HarvestablePercentFull = percentFull,
        };

    private static byte[] FixedPayload(uint gameId, Vector3 position)
    {
        byte[] payload = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        WriteSingle(payload, 4, position.X);
        WriteSingle(payload, 8, position.Y);
        WriteSingle(payload, 12, position.Z);
        // Net7 fixed-position wire carries a 16-byte orientation after position.
        WriteSingle(payload, 16, 0f);
        WriteSingle(payload, 20, 0f);
        WriteSingle(payload, 24, 0f);
        WriteSingle(payload, 28, 1f);
        return payload;
    }

    private static void WriteSingle(byte[] payload, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
}
