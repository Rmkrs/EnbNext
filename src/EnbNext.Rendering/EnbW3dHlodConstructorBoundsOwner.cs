using System.Numerics;
using System.Runtime.InteropServices;
namespace EnbNext.Rendering;

/// <summary>Render-thread-owned 0098F1D0 local bounds and 0098D110 world sphere.
/// Ordinary animation does not invalidate local bounds. 0098CF90 BOUNDINGBOX
/// and explicit pivot overrides still evaluate their required live state.</summary>
public sealed class EnbW3dHlodConstructorBoundsOwner
{
    private readonly EnbW3dHlodBoundsOwner state=new();
    private EnbW3dRenderResource? resource;
    private EnbW3dRenderHlod? hlod;
    private bool retained,hasPost;
    private float modelScale,hierarchyScale;
    public long PreparationCount {get;private set;}
    public bool TryCompute(EnbW3dRenderResource source,EnbW3dRenderHlod model,Matrix4x4 world,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,EnbW3dHierarchyState? post,
        out EnbW3dHlodBoundsOwner bounds,out string? diagnostic)
    {
        float nextModelScale=post?.RenderObjectScale??1f,nextHierarchyScale=post?.HierarchyTranslationScale??1f;
        if(retained&&(post is null||post.Transforms.Count==0)&&ReferenceEquals(resource,source)&&ReferenceEquals(hlod,model)&&
            hasPost==(post is not null)&&Same(modelScale,nextModelScale)&&Same(hierarchyScale,nextHierarchyScale)&&
            (post is null||string.Equals(post.HierarchyName,model.HierarchyName,StringComparison.OrdinalIgnoreCase))&&
            Finite(world)&&(post is null||Finite(post.RigidWorldTransform)))
        {
            bool valid=true;
            for(int i=0;i<animations.Count;i++)
                if(!string.Equals(animations[i].Animation.HierarchyName,model.HierarchyName,StringComparison.OrdinalIgnoreCase))
                {valid=false;break;}
            if(valid)
            {
                var sphere=EnbCurrentRetailRenderBoundsStorage.Calculate(post?.RigidWorldTransform??world,
                    new(state.LocalSphere.Center,state.LocalSphere.Radius),state.LocalBox.Center,state.LocalBox.Extents).Sphere;
                var transformed=new EnbW3dWorldBounds(new(sphere.X,sphere.Y,sphere.Z),state.LocalSphere.Radius);
                if(transformed.IsValid)
                {bounds=state.Set(state.LocalBox,state.LocalSphere,transformed);diagnostic=null;return true;}
            }
        }
        retained=false;
        bool ready=post is null
            ?EnbW3dHlodCompositeBoundsCompiler.TryCompute(source,model,world,animations,out bounds,out diagnostic,state)
            :EnbW3dHlodCompositeBoundsCompiler.TryCompute(source,model,world,animations,post,out bounds,out diagnostic,state);
        if(ready&&(post is null||post.Transforms.Count==0)&&state.BoundingBoxMember is null)
        {
            resource=source;hlod=model;hasPost=post is not null;modelScale=nextModelScale;hierarchyScale=nextHierarchyScale;
            retained=true;PreparationCount++;
        }
        return ready;
    }
    private static bool Same(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
    private static bool Finite(Matrix4x4 value)
    {
        foreach(float word in MemoryMarshal.Cast<Matrix4x4,float>(MemoryMarshal.CreateReadOnlySpan(ref value,1)))
            if(!float.IsFinite(word))return false;
        return true;
    }
}
