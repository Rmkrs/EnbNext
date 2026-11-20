using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbCapturedMissileTimingTests
{
    [Theory]
    [InlineData(10000u)]
    [InlineData(4294964295u)]
    public void CapturedSuccessiveMissilesKeepIndependentOverlappingImpactTails(uint start)
    {
        // Preserve the two captured server timestamp differences, but publish
        // at those normalized times synthetically. Capture arrival timing and
        // materialized endpoint motion are not asserted by this replay.
        const uint serverOrigin=0x3BE321B7;
        uint At(uint elapsed)=>unchecked(start+elapsed);
        var game=new Net7GameplayState(convertServerTimestamp:t=>unchecked(start+t-serverOrigin));
        var publisher=new EnbPresentationSnapshotPublisher();
        var world=new Net7WorldState();
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.True(game.Apply(Packet.ForOpcode(0x000B,Convert.FromHexString(
            "0708B48701002D1E014052030060931400B721E33BCC0800000000"))));
        var first=Assert.Single(publisher.Publish(world,game.Effects,null,At(0)).Effects);
        Assert.Equal(0x149360u,first.EffectId);Assert.Equal(2252,first.DurationMilliseconds);
        Assert.Equal(start,first.Timestamp);
        scheduler.Update([first],catalog,At(0));
        var firstImpact=Assert.Single(scheduler.Update([first],catalog,At(2252)).VisualNodes);
        Assert.Equal(65,firstImpact.Definition.DefinitionId);
        Assert.Equal(At(2252),firstImpact.ActualStartTick);

        Assert.True(game.Apply(Packet.ForOpcode(0x000B,Convert.FromHexString(
            "0708B48701002D1E014052030072931400CE32E33B310500000000"))));
        var roots=publisher.Publish(world,game.Effects,null,At(4375)).Effects;
        Assert.Equal(2,roots.Length);
        var second=Assert.Single(roots,r=>r.EffectId==0x149372);
        Assert.Equal(1329,second.DurationMilliseconds);Assert.Equal(At(4375),second.Timestamp);
        Assert.All(roots,r=>{Assert.Equal((ushort)850,r.DefinitionId);Assert.Equal(0x187B4u,r.SourceGameId);Assert.Equal(0x40011E2Du,r.TargetGameId);});
        var launched=scheduler.Update(roots,catalog,At(4375)).VisualNodes;
        Assert.Contains(launched,n=>n.Root.EffectId==second.EffectId && n.Definition.DefinitionId==850 && n.Active);
        Assert.Equal(firstImpact.Sequence,Assert.Single(launched,n=>n.Root.EffectId==first.EffectId).Sequence);
        var beforeHit=scheduler.Update(roots,catalog,At(5703)).VisualNodes;
        Assert.Contains(beforeHit,n=>n.Root.EffectId==second.EffectId && n.Definition.DefinitionId==850 && n.Active);
        var both=scheduler.Update(roots,catalog,At(5704)).VisualNodes;
        Assert.Equal(2,both.Count);Assert.All(both,n=>Assert.Equal(65,n.Definition.DefinitionId));
        Assert.Equal(firstImpact.Sequence,Assert.Single(both,n=>n.Root.EffectId==first.EffectId).Sequence);
        var secondImpact=Assert.Single(both,n=>n.Root.EffectId==second.EffectId);
        Assert.Equal(At(5704),secondImpact.ActualStartTick);
        Assert.Equal(both.Select(n=>(n.Root.EffectId,n.Sequence)),scheduler.Update(roots,catalog,At(5704)).VisualNodes.Select(n=>(n.Root.EffectId,n.Sequence)));
        Assert.Equal(2,scheduler.Update(roots,catalog,At(7251)).VisualNodes.Count);
        var survivor=Assert.Single(scheduler.Update(roots,catalog,At(7252)).VisualNodes);
        Assert.Equal(second.EffectId,survivor.Root.EffectId);Assert.Equal(secondImpact.Sequence,survivor.Sequence);
        Assert.Single(scheduler.Update(roots,catalog,At(10703)).VisualNodes);
        Assert.Empty(scheduler.Update(roots,catalog,At(10704)).VisualNodes);
        Assert.Empty(scheduler.Update(roots,catalog,At(10705)).VisualNodes);
        scheduler.Update([],catalog,At(10706));Assert.Equal(0,scheduler.TrackedRootCount);
    }

    // Exact 000B payloads from the passive September 11 combat audits.
    // Endpoints are not materialized; this checks packet/state/scheduler timing,
    // not captured movement, GPU rendering, or the server clock conversion.
    [Theory]
    [InlineData("0708B38701002D1E014052030098A81400941AA13BE00A00000000", 2784, 0x187B3u)]
    [InlineData("0708B48701002D1E014052030060931400B721E33BCC0800000000", 2252, 0x187B4u)]
    [InlineData("0708B48701002D1E014052030072931400CE32E33B310500000000", 1329, 0x187B4u)]
    public void CapturedDurationControlsImpactAndAuthoredTail(string payload, int duration, uint source)
    {
        const uint start = 10000; // Explicit offline clock normalization.
        var game = new Net7GameplayState(convertServerTimestamp: _ => start);
        Assert.True(game.Apply(Packet.ForOpcode(0x000B, Convert.FromHexString(payload))));
        var root = Assert.Single(new EnbPresentationSnapshotPublisher()
            .Publish(new Net7WorldState(), game.Effects, null, start).Effects);
        Assert.Equal((ushort)850, root.DefinitionId);
        Assert.Equal(source, root.SourceGameId);
        Assert.Equal(0x40011E2Du, root.TargetGameId);
        Assert.Equal(start, root.Timestamp);
        Assert.Equal(duration, root.DurationMilliseconds);
        Assert.Equal(EnbSceneEffectGeometryKind.ObjectToObject, root.GeometryKind);

        var paths = EnbInstalledAssetLocator.TryLocate();
        Assert.NotNull(paths);
        var catalog = EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var launch = scheduler.Update([root], catalog, start);
        Assert.True(Assert.Single(launch.VisualNodes, n => n.Definition.DefinitionId == 850).Active);
        Assert.Equal(source, Assert.Single(launch.VisualNodes, n => n.Definition.DefinitionId == 851).BoundGameId);
        uint impact = start + (uint)duration;
        var before = scheduler.Update([root], catalog, impact - 1);
        Assert.True(Assert.Single(before.VisualNodes, n => n.Definition.DefinitionId == 850).Active);
        Assert.DoesNotContain(before.VisualNodes, n => n.Definition.DefinitionId == 65);
        var hit = Assert.Single(scheduler.Update([root], catalog, impact).VisualNodes);
        Assert.Equal(65, hit.Definition.DefinitionId);
        Assert.Equal(EnbCurrentRetailEffectLinkKind.NextLink, hit.LinkKind);
        Assert.Equal(root.TargetGameId, hit.BoundGameId);
        Assert.Equal(impact, hit.ActualStartTick);
        Assert.Equal(5000u, hit.DurationMilliseconds);
        Assert.Equal(hit.Sequence, Assert.Single(scheduler.Update([root], catalog, impact).VisualNodes).Sequence);
        Assert.Single(scheduler.Update([root], catalog, impact + 4999).VisualNodes);
        Assert.Empty(scheduler.Update([root], catalog, impact + 5000).VisualNodes);
        Assert.Empty(scheduler.Update([root], catalog, impact + 5001).VisualNodes);
        scheduler.Update([], catalog, impact + 5002);
        Assert.Equal(0, scheduler.TrackedRootCount);
    }
}
