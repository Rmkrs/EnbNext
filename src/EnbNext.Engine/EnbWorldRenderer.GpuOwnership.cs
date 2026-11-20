namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<LiveGpuKey,LiveGpuResource> _compatGpuResources=[];
    private readonly Dictionary<(ulong Identity,ulong Generation),int> _gpuOwnerResourceCounts=[];
    private void AttachGpuResidentOwner(LiveGpuKey key)
    {
        var owner=(key.RenderIdentity,key.VisualGeneration);
        _gpuOwnerResourceCounts.TryGetValue(owner,out int count);_gpuOwnerResourceCounts[owner]=count+1;
        if(count==0){_residentGpuOwners.Add(owner);_indexedSubmissions.SetOwnerResident(owner,true);}
    }
    private void DetachGpuResidentOwner(LiveGpuKey key)
    {
        var owner=(key.RenderIdentity,key.VisualGeneration);
        int count=_gpuOwnerResourceCounts[owner];
        if(count>1)_gpuOwnerResourceCounts[owner]=count-1;
        else {_gpuOwnerResourceCounts.Remove(owner);_residentGpuOwners.Remove(owner);_indexedSubmissions.SetOwnerResident(owner,false);}
    }
    private void ClearGpuResidentOwners()
    {_gpuOwnerResourceCounts.Clear();_residentGpuOwners.Clear();_indexedSubmissions.ClearResidentOwners();}
    private readonly List<(LiveGpuKey Key,LiveGpuResource Resource)> _retiredOrdinaryGpuResources=[];
    private void RegisterGpuResourceLifetime(LiveGpuKey key,LiveGpuResource resource,bool ordinaryEligible)
    {
        AttachGpuResidentOwner(key);
        // Resource registration is a mutation boundary. Bind once here; draws
        // use the prepared GPU binding and never resolve the scene node again.
        var source=ordinaryEligible&&_ownedSceneFrame&&_ownedScene!.TryFind(key.RenderIdentity,out var node)?node.State:null;
        if(source is not null&&source.Definition.RenderIdentity==key.RenderIdentity&&
            source.Definition.VisualGeneration==key.VisualGeneration&&
            !key.RenderInstancePath.StartsWith("effect-object/",StringComparison.Ordinal)&&
            !key.RenderInstancePath.Contains("/effect/",StringComparison.Ordinal))
        {
            resource.LifetimeOwner=source;source.GpuResources.Add((key,resource));
            _compatGpuResources.Remove(key);
        }
        else _compatGpuResources[key]=resource;
    }
    private void RemoveGpuResourceBinding(LiveGpuKey key)
    {
        if(_gpuResources.Remove(key,out var resource))
        {
            DetachGpuResidentOwner(key);
            if(resource.LifetimeOwner is {} source)
            {source.GpuResources.RemoveAll(entry=>ReferenceEquals(entry.Resource,resource));resource.LifetimeOwner=null;}
        }
        _compatGpuResources.Remove(key);
    }
    private void RetireOrdinaryGpuResources(SceneOwner source)
    {
        _retiredOrdinaryGpuResources.AddRange(source.GpuResources);
        foreach(var entry in source.GpuResources)entry.Resource.LifetimeOwner=null;
        source.GpuResources.Clear();
    }
    private void DrainRetiredOrdinaryGpuResources(EnbGlApi? gl)
    {
        foreach(var (key,resource) in _retiredOrdinaryGpuResources)
        {
            if(_gpuResources.TryGetValue(key,out var current)&&ReferenceEquals(current,resource))
            {RemoveGpuResourceBinding(key);_lanePlans.Remove(key);_gpuRecoveryState.Remove(key);}
            resource.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
        }
        _retiredOrdinaryGpuResources.Clear();
    }
}
