using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed record ConfiguredEffectAssembly(EnbCurrentRetailRObjEffectAsset Asset,
        IReadOnlyList<EnbCurrentRetailRObjMeshMappers> Models,float Scale,Vector3 Hsv);
    private readonly Dictionary<string,ConfiguredEffectAssembly> _configuredEffectAssemblies=new(StringComparer.OrdinalIgnoreCase);

    //98C420/9A3A50 retain these aggregate children;48BE60 copies their
    //models in order before98EF60 scales and91ECA0 publishes the parent.
    private static bool CanProjectConfiguredEffectAssembly(LiveCpuRoot root)
    {
        bool ModelTree(LiveCpuRoot child)=>(child.Hlod is not null || child.IsDirectMeshPrototype || IsDirectDazzle(child) || IsNoOpConfiguredLeaf(child)) && child.Collection is null &&
            child.AggregateAttachments.Count==0 && child.RetainedEmitter is null &&
            child.Resource.ExternalRenderObjects.All(e=>e.Kind is EnbW3dExternalRenderObjectKind.NullObject or EnbW3dExternalRenderObjectKind.Box or EnbW3dExternalRenderObjectKind.Dazzle or EnbW3dExternalRenderObjectKind.ParticleEmitter or EnbW3dExternalRenderObjectKind.SpecialMaterial) &&
            child.HmodelChildren.All(c=>c.Root.RetainedEmitter is not null || ModelTree(c.Root));
        return root.Hlod is not null && root.AggregateAttachments.Count==0 && root.HmodelChildren.Count>0 &&
            root.Resource.ExternalRenderObjects.All(e=>e.Kind is EnbW3dExternalRenderObjectKind.NullObject or EnbW3dExternalRenderObjectKind.Box or EnbW3dExternalRenderObjectKind.Dazzle or EnbW3dExternalRenderObjectKind.ParticleEmitter or EnbW3dExternalRenderObjectKind.SpecialMaterial) &&
            root.HmodelChildren.Any(child=>child.Root.Hlod is not null && ModelTree(child.Root)) &&
            root.HmodelChildren.All(child=>child.Root.RetainedEmitter is not null || ModelTree(child.Root));
    }

    private void RetainConfiguredEffectAssembly(string name,LiveCpuRoot original,LiveCpuRoot configured,
        EnbCurrentRetailRObjMeshMappers models,float scale,Vector3 hsv,bool particlesConfigured=false)
    {
        if(!CanProjectConfiguredEffectAssembly(original) || _configuredEffectAssemblies.ContainsKey(name) ||
            HasParticleConstruction(original)&&!particlesConfigured)return;
        var asset=DescribeEffectAssembly(name,original,configured);
        var owners=new List<EnbCurrentRetailRObjMeshMappers>();
        try
        {
            owners.Add(models.RetainRenderObjects(configured.Resource));
            void RetainChildren(LiveCpuRoot node)
            {
                foreach(var child in node.HmodelChildren.Where(c=>c.Root.RetainedEmitter is null))
                {owners.Add(models.RetainRenderObjects(child.Root.Resource));RetainChildren(child.Root);}
            }
            RetainChildren(configured);
            _configuredEffectAssemblies.Add(name,new(asset,owners,scale,hsv));
        }
        catch {foreach(var owner in owners)owner.Dispose();throw;}
    }

    private static EnbCurrentRetailRObjEffectAsset DescribeEffectAssembly(string name,LiveCpuRoot original,LiveCpuRoot configured)
    {
        EnbCurrentRetailRObjEffectAsset Describe(LiveCpuRoot node,LiveCpuRoot instance)
        {
            var members=node.Hlod?.Levels.SelectMany(level=>level.Members).Concat(node.Hlod.Aggregate?.Members??[]).ToArray()??[];
            var children=node.HmodelChildren.Where(c=>c.Root.RetainedEmitter is null).Select(c=>new EnbRObjModelAttachment(
                c.Root.RootRenderObjectName,checked((int)c.Pivot),Describe(c.Root,instance.HmodelChildren.Single(other=>other.StorageOrdinal==c.StorageOrdinal).Root))
                {BoundsMemberName=members[c.StorageOrdinal].Name,LevelOrdinal=c.LevelOrdinal,
                 SceneMemberOrdinal=c.StorageOrdinal-(c.LevelOrdinal is int level
                    ?node.Hlod!.Levels.TakeWhile(l=>l.Ordinal!=level).Sum(l=>l.Members.Count)
                    :node.Hlod!.Levels.Sum(l=>l.Members.Count))}).ToArray();
            var emitters=new List<EnbRObjCompositeEmitter>();
            int levelCount=instance.Hlod?.Levels.Sum(level=>level.Members.Count)??0;
            foreach(var child in instance.HmodelChildren.Where(c=>c.Root.RetainedEmitter is not null))
            {
                var emitter=child.Root.RetainedEmitter!;
                var particle=new EnbCurrentRetailRObjParticleAsset(emitter.Key,emitter.Texture,emitter.Definition,checked((int)child.Pivot))
                    {RuntimeTextureName=emitter.RuntimeTextureName,RetainedTexture=emitter.RetainedTexture};
                int level=child.LevelOrdinal??-1;
                int firstMember=level<0?levelCount:instance.Hlod!.Levels.TakeWhile(l=>l.Ordinal!=level).Sum(l=>l.Members.Count);
                emitters.Add(new(particle,level,child.StorageOrdinal-firstMember){FactoryRandomOwner=child.Root.FactoryParticleRandom});
            }
            return new(node.Resource,node.Hlod is {Levels.Count:1} hlod?new(hlod.Name,0):null,node.OverbrightOff,null,null)
                {FactoryObjectName=node.RootRenderObjectName,ForceSubObjectLod=node.ForceSubObjectLod,IsNullObject=IsNamedNullLeaf(node),SpecialMaterial=node.SpecialMaterial?.Definition,
                    ModelAttachments=EnbRObjModelAttachment.Flatten(children),CompositeEmitters=emitters};
        }
        //Keep unconfigured query descriptors, as in the existing composite
        //effect consumer. Its radius supplies factory scale; draw owners hold
        //the already configured geometry/materials and must not copy again.
        return Describe(original,configured) with {FactoryObjectName=name,IsPreconfiguredFactoryObject=true};
    }

    private bool TryAcquireConfiguredEffectAssembly(string name,out EnbCurrentRetailRObjEffectAsset asset,
        out EffectObjectModelAcquisition owner)
    {
        asset=null!;owner=null!;
        if(!_configuredEffectAssemblies.TryGetValue(name,out var retained))return false;
        var copies=new List<EnbCurrentRetailRObjMeshMappers>();
        try
        {
            foreach(var model in retained.Models)copies.Add(model.RetainRenderObjects(model.Resource));
            asset=retained.Asset;
            owner=new(copies[0],name){FactoryInputs=(retained.Scale,retained.Hsv),Children=copies.Skip(1).ToArray()};
            return true;
        }
        catch {foreach(var copy in copies)copy.Dispose();throw;}
    }

    private bool RetainUnconfiguredEffectAssembly(string name,LiveCpuRoot root,out EnbCurrentRetailRObjEffectAsset asset,
        out EffectObjectModelAcquisition owner)
    {
        //48B220's neutral branch returns the concrete factory tree without
        //copying models or publishing a variant.9A4990 has already converted
        //nested0400 objects before9A3A50 inserts them into this tree.
        asset=DescribeEffectAssembly(name,root,root);owner=null!;
        var retained=new List<EnbCurrentRetailRObjMeshMappers>();
        try
        {
            void Retain(LiveCpuRoot node)
            {
                retained.Add(new(node.Resource,ReadMapperClock(),CameraMeshBatches,_prototypeResources,
                    node.Resource.Meshes.Cast<EnbW3dRenderMesh?>().ToArray(),_configuredDirectMeshModels));
                foreach(var child in node.HmodelChildren.Where(c=>c.Root.RetainedEmitter is null))Retain(child.Root);
            }
            Retain(root);
            owner=new(retained[0],name){FactoryInputs=(1,Vector3.Zero),Children=retained.Skip(1).ToArray()};
            _effectFactoryRoots.Add(asset,root);
            return true;
        }
        catch {foreach(var model in retained)model.Dispose();throw;}
    }
}
