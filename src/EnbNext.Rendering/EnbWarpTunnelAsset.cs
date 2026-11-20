using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbWarpTunnelAsset(EnbW3dRenderResource Resource,bool OverbrightOff,IReadOnlyList<EnbWarpTunnelParticle> Particles)
{
    //0098C7A0 converts the HMODEL;0098E4B0 retains aggregate emitter
    //occurrences on that hierarchy. Emitters have zero local bounds/cost.
    public EnbW3dRenderResource RetainedSceneResource(IReadOnlySet<int> removed)
    {
        var hlod=EnbW3dHmodelConversion.Create(Resource.HierarchyModels.Single(),_=>true);
        var children=Particles.Select((particle,index)=>(particle,index)).Where(p=>!removed.Contains(p.index)).ToArray();
        string Name(int index)=>$"<warp-emitter:{index}>";
        hlod=hlod with {Aggregate=new(0,children.Select(p=>new EnbW3dRenderHlodMember((uint)p.particle.Pivot,Name(p.index))).ToArray())};
        return Resource.Copy().SetHierarchicalLods([hlod]).SetCompositeRenderChildren(Resource.CompositeRenderChildren.Concat(
            children.Select(p=>new EnbW3dRenderCompositeChild(Name(p.index),0x0f,default,0))).ToArray());
    }

    public static EnbWarpTunnelAsset Load(EnbRetailAssetBundle assets,int baseAssetId)
        =>Load(assets,assets.BaseAssetResolver!.Resolve(baseAssetId));

    internal static EnbWarpTunnelAsset Load(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved)
    {
        if(resolved.W3d is not {} source || source.CurrentRetailAggregate is not {} aggregate ||
            !EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out _))throw new InvalidDataException("Warp tunnel aggregate unavailable");
        var model=resource.HierarchyModels.Single();var tree=resource.Hierarchies.Single();
        //009C5210 starts with no active HAnim; file registration alone
        //does not perform009C5980's explicit animation bind.
        if(resource.HierarchicalLods.Count!=0)
            throw new InvalidDataException("Warp tunnel requires a static hierarchy model");
        var children=new List<EnbWarpTunnelParticle>();
        foreach(var a in aggregate.RenderDefinition.Attachments)
        {
            int pivot=EnbCurrentRetailAggregateBoneLookup.Resolve(tree,a.BoneName);
            if(!assets.RetailMixResources!.TryReadResource(a.SubobjectName+".w3d",out var bytes,out var location) ||
                !EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var d,out _,assets.Textures) || !d.Name.Equals(a.SubobjectName,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Warp tunnel emitter unavailable: "+a.SubobjectName);
            if(d.MaximumEmissions==0)EnbCurrentRetailShipParticleRuntime.Validate(d,true);
            else EnbCurrentRetailRObjParticleAsset.ValidateAnimated(d);
            children.Add(new(pivot,new EnbCurrentRetailRObjParticleAsset(EnbW3dRenderResourceKey.From(location),null,d).PrepareTexture(assets)));
        }
        resource=EnbCurrentRetailMeshTexturePreparation.Prepare(resource,assets);
        return new(resource,resolved.Definition.OverbrightOff,children);
    }
}
public sealed record EnbWarpTunnelParticle(int Pivot,EnbCurrentRetailRObjParticleAsset Asset);

/// <summary>006F2690: one brightness step per client update, independently of
/// render-view redraws. Mesh emissive RGB is replaced, not multiplied.</summary>
public sealed class EnbWarpTunnelFade
{
    private uint? _last;
    public float Brightness{get;private set;}
    public bool Ended{get;private set;}
    public void Update(uint tick,bool sourceWarping)
    {
        if(Ended || _last==tick)return;
        _last=tick;
        Brightness=Math.Clamp(Brightness+(sourceWarping ? .05f : -.05f),0,1);
        if(!sourceWarping && Brightness<.1f)Ended=true;
    }
}
