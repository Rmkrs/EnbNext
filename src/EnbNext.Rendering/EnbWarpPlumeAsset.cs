using EnbNext.Assets;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>A static AttachEffect prototype: optional mesh HLOD and
/// continuous textured particle attachments, kept as independent render objects.</summary>
public sealed record EnbWarpPlumeAsset(EnbW3dRenderResource Resource,EnbW3dHlodSelection Selection,
    string BonePrefix,bool HideAsOtherObjects,bool OverbrightOff,IReadOnlyList<EnbWarpPlumeParticle> Particles)
{
    public static EnbWarpPlumeAsset Load(EnbRetailAssetBundle assets)
        => LoadAttachEffect(assets,710);

    public static EnbWarpPlumeAsset LoadAttachEffect(EnbRetailAssetBundle assets,int effectId,Vector3 hsvShift=default)
        =>LoadAttachEffect(assets,effectId,hsvShift,null);

    internal static EnbWarpPlumeAsset LoadAttachEffect(EnbRetailAssetBundle assets,int effectId,Vector3 hsvShift,EnbBaseAssetResolution? resolution)
    {
        var effect=assets.PresentationCatalog!.FindEffect(effectId)??throw new InvalidDataException($"E{effectId} unavailable");
        if(effect.EffectClassKind!=EnbEffectClassKind.AttachEffect || effect.BaseAssetId is not int id ||
            !effect.Properties.TryGetValue("BoneNamePrefix",out var prefix) || string.IsNullOrEmpty(prefix) ||
            EnbRetailIniDocument.ReadBool(effect.Properties,"ResetMappers"))throw new InvalidDataException("Unsupported static attachment definition");
        var resolved=resolution??assets.BaseAssetResolver!.Resolve(id);
        if(resolved.W3d is not {} source || source.CurrentRetailAggregate is not {} aggregate ||
            !EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out _))throw new InvalidDataException("Attachment resource unavailable");
        var h=resource.HierarchicalLods.Single();var tree=resource.Hierarchies.Single();
        //0098C420 ->009C5210 starts unanimated even when the file also
        //publishes HAnim clips.006EC570 does not bind one to this object.
        if(h.Levels.Count!=1 || h.HasAggregateExtension ||
            !h.HierarchyName.Equals(tree.Name,StringComparison.OrdinalIgnoreCase) ||
            h.Levels[0].Members.Any(m=>!resource.Meshes.Any(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,m.Name))))
            throw new InvalidDataException("Attachment requires a static locally bound single-level HLOD");
        var children=new List<EnbWarpPlumeParticle>();
        foreach(var attachment in aggregate.RenderDefinition.Attachments)
        {
            int pivot=EnbCurrentRetailAggregateBoneLookup.Resolve(tree,attachment.BoneName);
            var child=EnbCurrentRetailShipExhaustAsset.Load(assets,attachment.SubobjectName);
            if(!child.IsReady)throw new InvalidDataException(child.Error??child.DeferredReason);
            // 006EC570 uses the common prototype factory; 0048D710 recursively
            // shifts emitter children through 0048D850 before attachment cloning.
            if(hsvShift!=Vector3.Zero)
            {
                var texture=child.RetainedTexture is {} retained
                    ? EnbCurrentRetailEffectMeshHsvRuntime.ResolveRetainedTexture(assets.Textures,retained,hsvShift):null;
                child=child with {RetainedTexture=texture,RuntimeTextureName=texture?.LogicalName??child.RuntimeTextureName,Definition=child.Definition.Copy().SetTextureFileName(texture?.LogicalName??child.Definition.TextureFileName).SetColor(EnbCurrentRetailHsvRuntime.ShiftParticleColor(child.Definition.Color,hsvShift))};
            }
            children.Add(new(pivot,child));
        }
        resource=EnbCurrentRetailMeshTexturePreparation.Prepare(resource,assets);
        if(hsvShift!=Vector3.Zero)resource=EnbCurrentRetailEffectMeshHsvRuntime.Apply(resource,assets.Textures,hsvShift);
        return new(resource,new(h.Name,0),prefix,EnbRetailIniDocument.ReadBool(effect.Properties,"HideAsOtherObjects"),resolved.Definition.OverbrightOff,children);
    }
}
public sealed record EnbWarpPlumeParticle(int Pivot,EnbCurrentRetailShipExhaustAsset Asset);
