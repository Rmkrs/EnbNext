using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbTractorSoundLifetimeTests
{
    [Fact]
    public void FiniteNativeTractorStopsWithPickupEvenWhenRenderingStartsLate()
    {
        var catalog = EnbR10EffectSoundTests.Catalog("[EFFECT-1]\nEffectClass=LineEffect\nDuration=INFINITE\nSFX=sn_tractor_beam_loop_2.wav");
        var root = Root() with { DurationMilliseconds = 5000, EndWithTarget = true };
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.Single(scheduler.Update([root], catalog, 2000, new HashSet<uint> { 42, 9 }).SoundNodes);
        Assert.Empty(scheduler.Update([root], catalog, 5000, new HashSet<uint> { 42 }).SoundNodes);
        Assert.Empty(scheduler.Update([root], catalog, 5100, new HashSet<uint> { 42, 9 }).SoundNodes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InfiniteBeamStopsWhenEitherEndpointIsRemovedAndDoesNotRestart(bool removeTarget)
    {
        var catalog = EnbR10EffectSoundTests.Catalog("""
            [EFFECT-1]
            EffectClass=LineEffect
            Duration=INFINITE
            SFX=sn_tractor_beam_loop_2.wav
            StartLinkID=2
            UnaryStartLinkToTarget=TRUE
            [EFFECT-2]
            EffectClass=SoundEffect
            Duration=INFINITE
            SFX=child.wav
            """);
        var root = Root();
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.Equal(2, scheduler.Update([root], catalog, 0, new HashSet<uint> { 42, 9 }).SoundNodes.Count);
        var remaining = new HashSet<uint> { removeTarget ? 42u : 9u };
        var stopped = scheduler.Update([root], catalog, 100, remaining);
        Assert.Empty(stopped.SoundNodes);
        Assert.Empty(stopped.VisualNodes);
        // Root and target-bound child can receive separate observer callbacks.
        // The contract is retirement without restart, not one callback per tree.
        Assert.True(stopped.Receipt.RemovalMarks>0);
        // A stale effect snapshot or reused object ID is not a new effect generation.
        Assert.Empty(scheduler.Update([root], catalog, 200, new HashSet<uint> { 42, 9 }).SoundNodes);
        Assert.Equal(2, scheduler.Update([root with { InstanceGeneration = 2 }], catalog, 300,
            new HashSet<uint> { 42, 9 }).SoundNodes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiniteBeamUsesAuthoredEndpointLossPolicy(bool optedIn)
    {
        var catalog = EnbR10EffectSoundTests.Catalog($"[EFFECT-1]\nEffectClass=LineEffect\nDuration=2\nSFX=x.wav\nGoAwayWhenTargetDoes={optedIn}");
        var root = Root();
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.Single(scheduler.Update([root], catalog, 0, new HashSet<uint> { 42, 9 }).SoundNodes);
        var afterLoss = scheduler.Update([root], catalog, 100, new HashSet<uint> { 42 });
        Assert.Equal(optedIn ? 0 : 1, afterLoss.SoundNodes.Count);
        Assert.Empty(scheduler.Update([root], catalog, 2000, new HashSet<uint> { 42 }).SoundNodes);
    }

    [Fact]
    public void LateEndpointBindingDoesNotMasqueradeAsRemoval()
    {
        var catalog = EnbR10EffectSoundTests.Catalog("[EFFECT-1]\nEffectClass=LineEffect\nDuration=INFINITE\nSFX=x.wav");
        var root = Root();
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.Single(scheduler.Update([root], catalog, 0, new HashSet<uint> { 42 }).SoundNodes);
        Assert.Single(scheduler.Update([root], catalog, 100, new HashSet<uint> { 42, 9 }).SoundNodes);
        Assert.Single(scheduler.Update([root], catalog, 200, new HashSet<uint> { 42, 9 }).SoundNodes);
        Assert.Empty(scheduler.Update([root], catalog, 300, new HashSet<uint> { 42 }).SoundNodes);
    }

    private static EnbSceneEffectSnapshot Root() => EnbR10EffectSoundTests.Root() with
    {
        TargetGameId = 9, IsObjectToObject = true,
        GeometryKind = EnbSceneEffectGeometryKind.ObjectToObject,
    };
}
