using System.Numerics;

namespace EnbNext.Rendering;

//0098E0F0 removes an occurrence then calls +100/0098F1D0. A nonempty
//recomputation calls parent+100 recursively; an empty object zeros only itself.
public sealed class EnbEffectCompositeBoundsHistory
{
    private readonly IReadOnlyList<EnbRObjModelAttachment> models;
    private readonly EnbW3dRenderResource[] bounds;
    public EnbEffectCompositeBoundsHistory(EnbCurrentRetailRObjEffectAsset asset)
    {
        models=asset.ModelAttachments;
        bounds=[asset.Resource!,..models.Select(m=>m.Asset.Resource!)];
    }
    public EnbW3dRenderResource Bounds(int model)=>bounds[model+1];
    public void Remove(int model,IReadOnlyList<EnbW3dRenderResource> current)
    {
        int index=model+1;
        while(true)
        {
            bounds[index]=Rebind(index,current[index],bounds);
            if(index==0 || !bounds[index].HierarchicalLods.Any(h=>h.Levels.Any(l=>l.Members.Count!=0)||h.Aggregate?.Members.Count>0))return;
            index=models[index-1].ParentModelIndex+1;
        }
    }
    public EnbW3dRenderResource[] Project(IReadOnlyList<EnbW3dRenderResource> current)
        =>current.Select((resource,index)=>Rebind(index,resource,bounds)).ToArray();

    private EnbW3dRenderResource Rebind(int index,EnbW3dRenderResource resource,IReadOnlyList<EnbW3dRenderResource> children)
    {
        if(resource.HierarchicalLods.SingleOrDefault() is not {} parent)return resource;
        var descriptors=resource.CompositeRenderChildren.ToArray();
        for(int i=0;i<models.Count;i++)
        {
            var model=models[i];
            if(model.ParentModelIndex+1!=index || children[i+1].HierarchicalLods.SingleOrDefault() is not {} hlod)continue;
            var child=children[i+1];
            string name=model.BoundsMemberName??$"<0600:{parent.Name}:{(model.BoundsOrdinal<0?i:model.BoundsOrdinal)}:{model.Name}>";
            int descriptor=Array.FindIndex(descriptors,value=>value.Name==name);
            if(descriptor<0)continue;
            if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(child,hlod,Matrix4x4.Identity,null,0,out var computed,out var error))throw new InvalidDataException(error);
            //0098F1D0 updates bounds without rebuilding 0098EA70's constructor cost table.
            descriptors[descriptor]=descriptors[descriptor] with {HlodResource=child,Hlod=hlod,
                ConstructorBounds=new(new(computed.LocalSphere.Center,computed.LocalSphere.Radius),computed.LocalBox.Center,computed.LocalBox.Extents)};
        }
        return resource.Copy().SetCompositeRenderChildren(descriptors);
    }
}
