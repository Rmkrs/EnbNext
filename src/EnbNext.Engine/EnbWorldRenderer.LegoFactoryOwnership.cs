using EnbNext.Presentation;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private readonly HashSet<SceneOwner> _dirtyLegoFactoryOwners=[];
    private readonly HashSet<LegoFactoryBinding> _compatLegoFactoryBindings=[];
    private void RemoveLegoFactoryBinding(LegoFactoryBinding binding)
    {
        if(!binding.Valid)return;
        DetachLegoPhysicalLifetime(binding);
        binding.Valid=false;
        _legoFactoryBindings.Remove(binding.Key);
        _legoParticleFactories.Remove(binding.Key);_legoBaseFactories.Remove(binding.Key);
        if((binding.Factory?.Root??binding.Particle)?.PhysicalFactoryOwner is {} physical)_legoSourceInstances.Remove(physical);
        _compatLegoFactoryBindings.Remove(binding);
    }
    private void DetachLegoFactories(SceneOwner source)
    {
        _dirtyLegoFactoryOwners.Remove(source);
        foreach(var binding in source.LegoFactories)RemoveLegoFactoryBinding(binding);
        source.LegoFactories.Clear();
    }
    private void PruneLegoFactoryOwner(IEnbRenderObject obj,List<LegoFactoryBinding> bindings)
    {
        if(bindings.Count==0)return;
        var live=new HashSet<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path)>();
        var retained=RetainedLegoSpecs(obj,all:true);
        if(retained is not null)
            foreach(var spec in retained)live.Add((obj.RenderIdentity,spec.Source,spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,spec.CanonicalPath));
        else
        {
            foreach(var child in obj.ShipLegos?.Attachments??[])
                if(TryParseShipLegoPath(child.BoneName,out var path,out _))live.Add((obj.RenderIdentity,"aux",child.Ordinal,child.MaterializationRevision,child.BaseAsset,string.Join('/',path)));
            for(int i=0;i<obj.SubParts.Length;i++)
            {
                var child=obj.SubParts[i];
                if(TryParseShipLegoPath(child.Name,out var path,out _))live.Add((obj.RenderIdentity,"0x00B4",i,child.MaterializationRevision,unchecked((int)child.PartValue),string.Join('/',path)));
            }
        }
        if(_legoPhysicalOwners.TryGetValue(obj.RenderIdentity,out var physical))
        {
            var visited=new HashSet<LegoPhysicalObject>();
            void Keep(LegoPhysicalObject model)
            {
                if(!visited.Add(model))return;
                if(model.Spec is {} spec)live.Add((obj.RenderIdentity,spec.Source,spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,spec.CanonicalPath));
                if(model is LegoPhysicalModel concrete)foreach(var child in concrete.Children)Keep(child);
                else if(model is LegoRslOwner wrapper)foreach(var child in wrapper.Models.Values)Keep(child);
            }
            foreach(var model in physical.RootModels.Values)Keep(model);
            foreach(var pair in LegoRslOwnersFor(obj.RenderIdentity))if(live.Contains(pair.Key))Keep(pair.Value);
            foreach(var model in physical.ChildModels.Values)
                if(model.Spec is {} spec&&live.Contains((obj.RenderIdentity,spec.Source,spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,spec.CanonicalPath)))Keep(model);
            foreach(var key in physical.ChildModels.Keys.Where(key=>!visited.Contains(physical.ChildModels[key])).ToArray())physical.ChildModels.Remove(key);
        }
        for(int i=bindings.Count-1;i>=0;i--)
            if(!live.Contains(bindings[i].Key)){RemoveLegoFactoryBinding(bindings[i]);bindings.RemoveAt(i);}
    }
}
