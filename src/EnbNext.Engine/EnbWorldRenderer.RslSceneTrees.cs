using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private long _rslSceneTreeRegistrations,_rslSceneTreeFrameReuses;
    private int AddRslSceneRoots(SceneOwner? source,IEnbRenderObject obj,EnbResolvedRslReference rsl,
        EnbRetailAssetBundle assets,out RootTaskList roots,List<RslAnimationDiagnostic> receipts,
        List<LiveRslSoundRequest> sounds,uint time,EnbRslObjectState? update,
        out string? diagnostic,out bool pending)
    {
        if(source?.RslCallbackTree is not {Persistent:true,PersistentValid:true} tree ||
            source.RootTasks.Collection is not null)
        {
            roots=new();
            return AddRslRoots(obj,rsl,assets,roots,receipts,sounds,time,update,out diagnostic,out pending);
        }
        // 00460FF0 delegates to the current retained model. Animation/NEXT/
        // sound evaluation remains exactly once; only occurrence construction
        // moves to the model's lifetime. A model change consumes this result
        // locally instead of replaying animation evaluation.
        tree.Evaluated.Clear();tree.Replacements.Clear();
        int added=AddRslRoots(obj,rsl,assets,tree.Replacements,receipts,sounds,time,update,
            out diagnostic,out pending,evaluatedRoots:tree.Evaluated);
        bool same=added>0 && tree.Evaluated.Count==tree.Tops.Count;
        for(int i=0;same && i<tree.Tops.Count;i++)same=ReferenceEquals(tree.Tops[i].Root,tree.Evaluated[i].Root);
        if(same && RefreshRetainedRslSceneTree(tree,obj,out var refreshError))
        {roots=tree.Instances;tree.Frame=_rigidFrame;_rslSceneTreeFrameReuses++;return added;}
        source.RslCallbackTree=null;roots=new();int materialized=0;
        foreach(var selected in tree.Evaluated)
            if(TryAppendRootInstanceTree(obj,selected.Root,true,roots,selected.Blend,out var error))materialized++;
            else diagnostic??=error;
        return materialized;
    }
    private bool RefreshRetainedRslSceneTree(RetainedRslCallbackTree tree,IEnbRenderObject obj,out string? diagnostic)
    {
        diagnostic=null;
        for(int i=0;i<tree.Instances.Count;i++)
        {
            var instance=tree.Instances[i];var current=instance.Parent?.Object??obj;
            var appearance=instance.Parent?.Appearance??FactoryAppearance.From(obj);
            if(instance.Parent is null)
            {
                instance.HierarchySourceScale=1;
                if(instance.Root.UnconfiguredFactoryObject)appearance=FactoryAppearance.Neutral;
                if(instance.Root.ConfiguredFactoryInputs is {} inputs)
                {
                    float remaining=(_shipExhaustOwner??this)._effectRslRootOwners.TryGetValue(obj.RenderIdentity,out var owner)?owner.SourceRadiusScale:1;
                    instance.HierarchySourceScale=remaining;
                    appearance=new(inputs.Scale*remaining,inputs.Hsv.X,inputs.Hsv.Y,inputs.Hsv.Z);
                }
            }
            if(instance.Root.IndependentRslFactory && instance.Root.ConfiguredFactoryInputs is {} childInputs)
                appearance=new(childInputs.Scale,childInputs.Hsv.X,childInputs.Hsv.Y,childInputs.Hsv.Z);
            instance.RefreshRetainedSceneFrame(current);instance.Appearance=appearance;
        }
        for(int i=0;i<tree.Tops.Count;i++)
        {
            var instance=tree.Tops[i];
            instance.UpdateCallbackAnimation(tree.Evaluated[i].Blend??ResolveShipWarpAnimation(instance.Object,
                instance.Root,instance.InstancePath,instance.Object.Spatial?.Transform.Position??Vector3.Zero));
            if(TryBuildObjectWorld(instance.Object,out var world,1))instance.PrimitiveFactoryWorld=world;
            else {diagnostic="Retained RSL owner transform is unavailable.";return false;}
            instance.PrimitiveFactoryScale=EnbCurrentRetailPrototypeVariantName.ChangesScale(instance.RenderScale)?instance.RenderScale:1;
            var shift=instance.Appearance.Hsv;
            instance.PrimitiveFactoryHsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(shift)?shift:Vector3.Zero;
        }
        foreach(var instance in tree.Instances)
            if(!TryRefreshOrdinaryHlodChildCore(instance,false,out diagnostic))return false;
        return true;
    }
}
