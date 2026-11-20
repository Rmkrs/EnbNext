using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private enum RadiusPublicationPhase { Query,Admission,Final }
    private readonly HashSet<SceneOwner> _ownedRadiusSources=[];
    private readonly ImmutableDictionary<ulong,float>[] _ownedRadiusPublications=
        [ImmutableDictionary<ulong,float>.Empty,ImmutableDictionary<ulong,float>.Empty,ImmutableDictionary<ulong,float>.Empty];
    private readonly RootTaskList _ownedRadiusQueryTasks=new();
    private long _ownedRadiusPublicationChanges,_ownedRadiusFallbackBuilds;
    private void DetachOwnedRadiusSource(SceneOwner source)
    {
        _ownedRadiusSources.Remove(source);
        Array.Clear(source.RadiusPublications);source.FrameDistanceRadius=null;source.QueryDistanceRadius=null;source.LastEffectRadius=null;
        for(int i=0;i<_ownedRadiusPublications.Length;i++)
            _ownedRadiusPublications[i]=_ownedRadiusPublications[i].Remove(source.Definition.RenderIdentity);
    }
    private void UpdateOwnedRadiusMembership(SceneOwner source)
    {
        if(HasCurrentRetailShipLegoComposition(source.Definition))_ownedRadiusSources.Add(source);
        else DetachOwnedRadiusSource(source);
    }
    private float? ResolveOwnedRadius(SceneOwner source,RootTaskList roots,EnbClientBaseAssetMountCatalog mounts)
    {
        LiveRootInstance? root=null;int count=0;
        static bool Matches(LiveRootInstance item,ulong identity)=>item.InstancePath=="root"&&item.Object.RenderIdentity==identity;
        if(ReferenceEquals(source.RootTasks.Collection,roots))
        {
            for(var cell=source.RootTasks.First;cell is not null;cell=cell.OwnerNext)
                if(Matches(cell.Value,source.Definition.RenderIdentity)){root=cell.Value;count++;}
        }
        else foreach(var item in roots)if(Matches(item,source.Definition.RenderIdentity)){root=item;count++;}
        foreach(var item in _hiddenCombatSourceRoots)if(Matches(item,source.Definition.RenderIdentity)){root=item;count++;}
        if(count>1)return null;
        if(count==1&&!TryGetLegoRadiusWorld(root!,out _))return null;
        if(count==0&&!(_preparingLegoRadiusQueries?source.QueryEmptyRadiusCandidate:source.EmptyRadiusCandidate))return null;
        if(source.LegoControllers?.RootRadius is {} retained)return retained.Radius;
        if(source.RadiusFallbackReady)return source.RadiusFallback;
        return PrepareOwnedRadiusFallback(source,roots,mounts);
    }
    private float? PrepareOwnedRadiusFallback(SceneOwner source,RootTaskList roots,EnbClientBaseAssetMountCatalog mounts)
    {
        // Only legacy authored composition without a controller mutation stream
        // needs this construction adapter. Its result has the source lifetime.
        source.RadiusFallbackReady=true;source.RadiusFallback=null;_ownedRadiusFallbackBuilds++;
        using var scope=new SceneOwnerScope(this,source);
        VisitCurrentRetailRadiusControllers(roots,mounts,(obj,controllers)=>
        {
            if(TryResolveLegoRadius(controllers,mounts,out float radius))source.RadiusFallback=radius;
        },obj=>obj.RenderIdentity==source.Definition.RenderIdentity);
        return source.RadiusFallback;
    }
    private ImmutableDictionary<ulong,float> BuildOwnedRadiusPublication(RootTaskList roots,EnbClientBaseAssetMountCatalog mounts,RadiusPublicationPhase phase)
    {
        int index=(int)phase;var result=_ownedRadiusPublications[index];
        foreach(var source in _ownedRadiusSources)
        {
            float? radius=ResolveOwnedRadius(source,roots,mounts);
            if(phase==RadiusPublicationPhase.Query)source.QueryDistanceRadius=radius;else source.FrameDistanceRadius=radius;
            if(Nullable.Equals(source.RadiusPublications[index],radius))continue;
            source.RadiusPublications[index]=radius;_ownedRadiusPublicationChanges++;
            result=radius is {} value?result.SetItem(source.Definition.RenderIdentity,value):result.Remove(source.Definition.RenderIdentity);
        }
        return _ownedRadiusPublications[index]=result;
    }
    private ImmutableDictionary<ulong,float> BuildRadiusAdmission(IReadOnlyCollection<LiveRootInstance> roots,EnbClientBaseAssetMountCatalog? mounts)
        =>_ownedSceneFrame&&mounts is not null&&roots is RootTaskList tasks&&tasks.IsOwnedBy(this)
            ?BuildOwnedRadiusPublication(tasks,mounts,RadiusPublicationPhase.Admission):BuildRetainedCurrentRetailDistanceRadii(roots,mounts);
    private ImmutableDictionary<ulong,float>? _mergedAdmissionRadii,_mergedFinalRadii,_mergedDistanceRadii;
    private ImmutableDictionary<ulong,float> MergeRadiusPublications(ImmutableDictionary<ulong,float> admission,ImmutableDictionary<ulong,float> final)
    {
        if(ReferenceEquals(admission,_mergedAdmissionRadii)&&ReferenceEquals(final,_mergedFinalRadii))return _mergedDistanceRadii!;
        _mergedAdmissionRadii=admission;_mergedFinalRadii=final;
        return _mergedDistanceRadii=ReferenceEquals(admission,final)?admission:admission.SetItems(final);
    }
    private bool ShouldAttachOwnedRadar(SceneOwner source,EnbNext.Presentation.IEnbRenderObject? observer,int? scanRange,bool query)
        =>EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer,source,scanRange,
            observerRadius:query?_localSceneOwner?.QueryDistanceRadius:_localSceneOwner?.FrameDistanceRadius,
            objectRadius:query?source.QueryDistanceRadius:source.FrameDistanceRadius);
    private void PublishOwnedEffectRadii(RootTaskList roots,EnbClientBaseAssetMountCatalog? mounts)
    {
        if(mounts is null)return;
        foreach(var source in _ownedRadiusSources)
        {
            if(ResolveOwnedRadius(source,roots,mounts) is not {} radius||Nullable.Equals(source.LastEffectRadius,radius))continue;
            source.LastEffectRadius=radius;_lastEffectObjectRadii[source.Definition.RenderIdentity]=radius;
        }
    }
}
