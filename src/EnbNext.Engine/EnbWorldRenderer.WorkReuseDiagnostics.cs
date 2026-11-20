using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private static object ReuseRootKey(LiveRootInstance r) => (r.Object.RenderIdentity, r.InstancePath);
    private static object?[] ReuseRootStructure(LiveRootInstance r) =>
    [r.Object.VisualGeneration, r.Object.RenderObjectGeneration, r.Root.Resource.Key,
     EnbWorkReuseDiagnostics.Reference(r.Root.PrimitiveFactoryIdentity),
     r.Parent is null ? null : ReuseRootKey(r.Parent), r.ParentStorageOrdinal,
     r.IsShipLegoController, r.IsOrdinaryHlodChild, r.OrdinaryHlodLevelOrdinal,
     r.IsExternalRslModel, r.IsExternalLegoChild, r.IsAggregateHlodChild];

    private static void ObserveReuseRoots(string phase, RootTaskList roots)
    {
        if (!EnbWorkReuseDiagnostics.Active) return;
        using var inspection=EnbWorkReuseDiagnostics.Inspect();
        EnbWorkReuseDiagnostics.Add(phase+".occurrences",roots.Count);
        foreach(var r in roots)
        {
            object key=ReuseRootKey(r);
            EnbWorkReuseDiagnostics.Observe(phase+".structure",key,ReuseRootStructure(r));
            EnbWorkReuseDiagnostics.Observe(phase+".wrapper",key,EnbWorkReuseDiagnostics.Reference(r));
            EnbWorkReuseDiagnostics.Observe(phase+".pose",key,r.WorldTransformOverride,
                r.PrimitiveFactoryWorld,r.PrimitiveFactoryScale,r.CurrentRetailBoundsWorld);
            EnbWorkReuseDiagnostics.Observe(phase+".selection",key,r.HlodSelection,r.ShouldDraw,
                r.EngineChildHidden,r.PivotVisible,r.MovieHidden);
        }
    }

    private Dictionary<object,object?[]>? CaptureReuseStructure(RootTaskList roots, (ulong Identity,ulong Generation) owner)
    {
        if(!EnbWorkReuseDiagnostics.Active)return null;
        using var inspection=EnbWorkReuseDiagnostics.Inspect();
        ulong outer=owner.Identity;
        foreach(var pair in _legoRslOwners)
            if(pair.Value.Object.RenderIdentity==owner.Identity){outer=pair.Key.Identity;break;}
        var identities=_legoRslOwners.Where(p=>p.Key.Identity==outer).Select(p=>p.Value.Object.RenderIdentity).Append(outer).ToHashSet();
        var result=new Dictionary<object,object?[]>();
        foreach(var root in roots.Where(r=>identities.Contains(r.Object.RenderIdentity)))
            if(!result.TryAdd(ReuseRootKey(root),ReuseRootStructure(root)))
                EnbWorkReuseDiagnostics.Add("callback.structureKeyCollision");
        return result;
    }

    private bool ObserveCallbackMutation(Dictionary<object,object?[]>? before,RootTaskList roots, (ulong Identity,ulong Generation) owner)
    {
        if(before is null)return false;
        using var inspection=EnbWorkReuseDiagnostics.Inspect();
        var after=CaptureReuseStructure(roots,owner)!;
        int changed=after.Count(p=>!before.TryGetValue(p.Key,out var previous)||!previous.AsSpan().SequenceEqual(p.Value));
        int removed=before.Keys.Count(k=>!after.ContainsKey(k));
        EnbWorkReuseDiagnostics.Add("callback.structureChangedOccurrences",changed);
        EnbWorkReuseDiagnostics.Add("callback.structureRemovedOccurrences",removed);
        EnbWorkReuseDiagnostics.Add(changed+removed==0?"callback.noObservedStructureChange":"callback.observedStructureChange");
        return changed+removed==0;
    }
}
