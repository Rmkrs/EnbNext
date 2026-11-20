using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z34CurrentRetailDurationLinkedEffectTimingTests
{
    [Fact]
    public void TerminalSelectorSuppliesAdditionalStartOffsetAndRootDuration()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-100]
            EffectClass=PassEffect
            StartDelay=0.200
            Duration=0.500
            """);

        EnbCurrentRetailDurationLinkedEffectTimingResolution resolved =
            EnbCurrentRetailDurationLinkedEffectTimingRuntime.Resolve(catalog, 100, packetSpeedup: 1f);

        Assert.True(resolved.Success);
        Assert.Equal(100, resolved.TerminalSelector);
        Assert.Equal(200, resolved.AdditionalStartOffsetMilliseconds);
        Assert.Equal(500, resolved.DurationMilliseconds);
        Assert.Equal([100], resolved.TraversedDefinitionIds);
    }

    [Fact]
    public void EnabledStartLinkHasPriorityOverEnabledNextLinkAndAddsStartLinkDelay()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-100]
            EffectClass=PassEffect
            StartDelay=0.100
            Duration=9.0
            DurationLinkedEffectUseStartLink=TRUE
            StartLinkID=101
            StartLinkDelay=0.200
            DurationLinkedEffectUseNextLink=TRUE
            NextLinkID=999
            [EFFECT-101]
            EffectClass=PassEffect
            StartDelay=0.300
            Duration=0.400
            """);

        EnbCurrentRetailDurationLinkedEffectTimingResolution resolved =
            EnbCurrentRetailDurationLinkedEffectTimingRuntime.Resolve(catalog, 100, packetSpeedup: 1f);

        Assert.True(resolved.Success);
        Assert.Equal([100, 101], resolved.TraversedDefinitionIds);
        Assert.Equal(101, resolved.TerminalSelector);
        Assert.Equal(600, resolved.AdditionalStartOffsetMilliseconds);
        Assert.Equal(400, resolved.DurationMilliseconds);
    }

    [Fact]
    public void NextLinkAddsCurrentDurationAndLineEffectUsesReciprocalPacketSpeedup()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-100]
            EffectClass=LineEffect
            StartDelay=0.100
            Duration=0.250
            DurationLinkedEffectUseNextLink=TRUE
            NextLinkID=101
            [EFFECT-101]
            EffectClass=PassEffect
            StartDelay=0.200
            Duration=0.500
            """);

        EnbCurrentRetailDurationLinkedEffectTimingResolution resolved =
            EnbCurrentRetailDurationLinkedEffectTimingRuntime.Resolve(catalog, 100, packetSpeedup: 2f);

        Assert.True(resolved.Success);
        Assert.Equal(0.5f, resolved.ReciprocalSpeedup);
        Assert.Equal([100, 101], resolved.TraversedDefinitionIds);
        Assert.Equal(425, resolved.AdditionalStartOffsetMilliseconds);
        Assert.Equal(500, resolved.DurationMilliseconds);
    }

    [Fact]
    public void TerminalLineEffectTruncatesMillisecondsBeforeApplyingReciprocalSpeedup()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-100]
            EffectClass=LineEffect
            Duration=0.0039
            """);

        EnbCurrentRetailDurationLinkedEffectTimingResolution resolved =
            EnbCurrentRetailDurationLinkedEffectTimingRuntime.Resolve(catalog, 100, packetSpeedup: 5f / 3f);

        Assert.True(resolved.Success);
        // Retail: trunc(3.9 ms) = 3, then trunc(3 * ~0.6) = 1.
        // A single truncation after scaling seconds would instead produce 2 ms.
        Assert.Equal(1, resolved.DurationMilliseconds);
    }

    [Fact]
    public void NonPositiveSpeedupUsesRetailFallbackOfOne()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-100]
            EffectClass=LineEffect
            Duration=0.250
            """);

        EnbCurrentRetailDurationLinkedEffectTimingResolution resolved =
            EnbCurrentRetailDurationLinkedEffectTimingRuntime.Resolve(catalog, 100, packetSpeedup: 0f);

        Assert.True(resolved.Success);
        Assert.Equal(1f, resolved.ReciprocalSpeedup);
        Assert.Equal(250, resolved.DurationMilliseconds);
    }

    [Fact]
    public void MissingOrNonPositiveTerminalDurationUsesRetailOneSecondFallbackWhileCyclesFailClosed()
    {
        EnbRetailPresentationCatalog missingCatalog = Catalog("""
            [EFFECT-100]
            EffectClass=PassEffect
            DurationLinkedEffectUseStartLink=TRUE
            StartLinkID=101
            Duration=1.0
            """);
        EnbCurrentRetailDurationLinkedEffectTimingResolution missing =
            EnbCurrentRetailDurationLinkedEffectTimingRuntime.Resolve(missingCatalog, 100, 1f);
        Assert.True(missing.Success);
        Assert.Equal(101, missing.TerminalSelector);
        Assert.Equal(1000, missing.DurationMilliseconds);
        Assert.True(missing.UsedRetailFallbackDuration);

        EnbRetailPresentationCatalog cycleCatalog = Catalog("""
            [EFFECT-100]
            EffectClass=PassEffect
            DurationLinkedEffectUseStartLink=TRUE
            StartLinkID=101
            Duration=1.0
            [EFFECT-101]
            EffectClass=PassEffect
            DurationLinkedEffectUseNextLink=TRUE
            NextLinkID=100
            Duration=0.100
            """);
        EnbCurrentRetailDurationLinkedEffectTimingResolution cycle =
            EnbCurrentRetailDurationLinkedEffectTimingRuntime.Resolve(cycleCatalog, 100, 1f);
        Assert.False(cycle.Success);
        Assert.Equal(EnbCurrentRetailDurationLinkedEffectTimingFailure.CycleDetected, cycle.Failure);

        EnbRetailPresentationCatalog zeroCatalog = Catalog("""
            [EFFECT-100]
            EffectClass=PassEffect
            Duration=0
            """);
        EnbCurrentRetailDurationLinkedEffectTimingResolution zero =
            EnbCurrentRetailDurationLinkedEffectTimingRuntime.Resolve(zeroCatalog, 100, 1f);
        Assert.True(zero.Success);
        Assert.Equal(1000, zero.DurationMilliseconds);
        Assert.True(zero.UsedRetailFallbackDuration);
    }

    [Fact]
    public void SchedulerAppliesSelectorTimingBeforeActualRootStartDelayAndUsesTerminalDuration()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=RenderStateEffect
            RenderStateID=7
            ActionType=Activate
            StartDelay=0.100
            Duration=9.0
            NextLinkID=2
            [EFFECT-2]
            EffectClass=RenderStateEffect
            RenderStateID=7
            ActionType=Deactivate
            Duration=1.0
            [EFFECT-100]
            EffectClass=PassEffect
            StartDelay=0.200
            Duration=0.500
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(
            effectId: 77,
            generation: 1,
            definitionId: 1,
            timestamp: 1000,
            source: 5,
            target: 6,
            durationLink: 100);

        EnbCurrentRetailEffectRslSchedulerUpdate at1000 = scheduler.Update([root], catalog, 1000);
        Assert.Empty(at1000.RenderStateEvents);
        Assert.Equal(1, at1000.Receipt.ResolvedDurationLinkedRoots);
        Assert.Equal(1, at1000.Receipt.TrackedDurationLinkedRoots);
        Assert.Equal(0, at1000.Receipt.RejectedDurationLinkedRoots);
        Assert.Contains("100", at1000.Receipt.DurationLinkedExample);

        Assert.Empty(scheduler.Update([root], catalog, 1299).RenderStateEvents);
        EnbCurrentRetailRenderStateEffectEvent activate = Assert.Single(
            scheduler.Update([root], catalog, 1300).RenderStateEvents);
        Assert.Equal(EnbRenderStateEffectActionKind.Activate, activate.ActionKind);
        Assert.Equal(1300u, activate.ActualStartClientTick);

        Assert.Empty(scheduler.Update([root], catalog, 1799).RenderStateEvents);
        EnbCurrentRetailRenderStateEffectEvent deactivate = Assert.Single(
            scheduler.Update([root], catalog, 1800).RenderStateEvents);
        Assert.Equal(EnbRenderStateEffectActionKind.Deactivate, deactivate.ActionKind);
        Assert.Equal(EnbCurrentRetailEffectLinkKind.NextLink, deactivate.LinkKind);
    }

    [Fact]
    public void SchedulerRejectsBadSelectorProgramWithoutPartialRootMutation()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=RenderStateEffect
            RenderStateID=7
            ActionType=Activate
            Duration=1.0
            [EFFECT-100]
            EffectClass=PassEffect
            DurationLinkedEffectUseNextLink=TRUE
            NextLinkID=101
            Duration=0.100
            [EFFECT-101]
            EffectClass=PassEffect
            DurationLinkedEffectUseNextLink=TRUE
            NextLinkID=100
            Duration=0.100
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(77, 1, 1, 0, 5, 6, durationLink: 100);

        EnbCurrentRetailEffectRslSchedulerUpdate update = scheduler.Update([root], catalog, 1000);

        Assert.Empty(update.RenderStateEvents);
        Assert.Equal(0, update.Receipt.CreatedRoots);
        Assert.Equal(1, update.Receipt.RejectedRoots);
        Assert.Equal(1, update.Receipt.RejectedDurationLinkedRoots);
        Assert.Equal(0, update.Receipt.TrackedDurationLinkedRoots);
    }

    [Fact]
    public void ActualLineEffectCycleTimeUsesReciprocalSpeedupForRemovalRounding()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=LineEffect
            Duration=9.0
            CycleTime=0.200
            [EFFECT-100]
            EffectClass=PassEffect
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(
            77, 1, 1, timestamp: 0, source: 5, target: 6, durationLink: 100, effectScalar: 2f);

        EnbCurrentRetailEffectRslSchedulerUpdate initial = scheduler.Update([root], catalog, 0);
        Assert.Equal(1, initial.Receipt.TrackedDurationLinkedRoots);
        Assert.Equal(1, initial.Receipt.ActiveNodes);

        EnbCurrentRetailEffectRslSchedulerUpdate removed = scheduler.Update([], catalog, 250);
        Assert.Equal(1, removed.Receipt.RemovalMarks);
        Assert.Equal(1, scheduler.Update([], catalog, 299).Receipt.TrackedRoots);
        Assert.Equal(0, scheduler.Update([], catalog, 300).Receipt.TrackedRoots);
    }

    private static EnbRetailPresentationCatalog Catalog(string effects)
    {
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText(effects);
        return catalog;
    }

    private static EnbSceneEffectSnapshot Snapshot(
        uint effectId,
        ulong generation,
        ushort definitionId,
        uint timestamp,
        uint source,
        uint target,
        short durationLink,
        float effectScalar = 1f)
        => new(
            EffectId: effectId,
            InstanceGeneration: generation,
            SourceGameId: source,
            TargetGameId: target,
            DefinitionId: definitionId,
            Timestamp: timestamp,
            DurationMilliseconds: 0,
            BoneName: string.Empty,
            Mask: 0,
            IsObjectToObject: true,
            Scale: 1f,
            SpatialOffset: default,
            EndpointShortA: 0,
            EndpointShortB: 0,
            DescriptorFlag: false,
            EffectOffset: default,
            EffectScalar: effectScalar,
            GeometryKind: EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked,
            WorldPoint: default,
            DurationLink: durationLink,
            HsvShift: default,
            AuxiliaryEffectFieldA: 0,
            AuxiliaryEffectFieldB: 0,
            Presentation: null);
}
