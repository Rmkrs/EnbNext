using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    // 00965F40 prepares visibility/LOD before 009661C0 runs callbacks.
    // 00460FF0 then delegates to the retained current model. A clock update
    // does not replace that model or its concrete children.
    private sealed class RetainedRslCallbackTree
    {
        public long Frame=-1;
        public bool Persistent,PersistentValid;
        public readonly List<(bool Override,float Scale,float H,float S,float V)> Inputs=[];
        public readonly RootTaskList Instances=new();
        public readonly List<LiveRootInstance> Tops=[];
        public readonly List<(LiveCpuRoot Root,LiveRslAnimationBlendBinding? Blend)> Evaluated=[];
        public readonly RootTaskList Replacements=new();
    }
    private long _rslCallbackMutationAttempts,_rslCallbackMutations,_rslCallbackMutatedRoots,_rslCallbackRebuilds,_rslCallbackUnownedRebuilds;
    private readonly bool _mutateRslCallbackRoots=Environment.GetEnvironmentVariable("ENB_LEGACY_RSL_CALLBACK_REBUILD")!="1";
    private void CaptureRslCallbackTree(SceneOwner source,IEnbRenderObject obj,RootTaskList instances)
    {
        if(source.RslCallbackTree is {Persistent:true,PersistentValid:true} existing)
        {
            // A nested/query invocation must not replace a queued scene tree.
            if(ReferenceEquals(instances,existing.Instances))existing.Frame=_rigidFrame;
            return;
        }
        // Aggregate bones have conditional membership and observer callbacks;
        // Lego/fixed attachments have separate ownership notifications. Their
        // existing mutation paths remain responsible for those semantics.
        if(!_mutateRslCallbackRoots || instances.Count==0 || UsesPlanetExhaust(obj) ||
            HasCurrentRetailShipLegoComposition(obj) || EnbCurrentRetailShipFixedVisualRuntime.IsEligible(obj))
        {source.RslCallbackTree=null;return;}
        // Authored HMODEL/HLOD emitter children keep their physical buffers.
        // 0098F090 changes their mount pose; the existing emitter preparation
        // still advances births and handles scene removal after this mutation.
        // Dynamic bones and independent emitter roots remain excluded.
        foreach(var instance in instances)
            if(instance.Root.AggregateAttachments.Count!=0 || instance.Root.Sphere is not null ||
                instance.Root.Ring is not null || instance.RslChild is not null ||
                instance.Root.RetainedEmitter is not null && (!(instance.IsOrdinaryHlodChild || instance.IsAggregateHlodChild) || instance.Parent is null))
            {source.RslCallbackTree=null;return;}
        var tree=source.RslCallbackTree is {Persistent:false} scratch?scratch:new RetainedRslCallbackTree();
        source.RslCallbackTree=tree;
        tree.Instances.Clear();tree.Tops.Clear();
        foreach(var instance in instances)
        {tree.Instances.Add(instance);if(instance.Parent is null)tree.Tops.Add(instance);}
        tree.Frame=_rigidFrame;
        if(source.RootTasks.Collection is null && tree.Tops.All(t=>!t.Root.IndependentRslFactory))
        {
            foreach(var instance in tree.Instances)
            {
                // Scene lifetime is independent of the query/extra-view slot.
                // Leave an unused fresh invocation in that slot rather than
                // allowing a later query to reset the retained scene object.
                if(instance.InvocationStorage is {} slots &&
                    ReferenceEquals(slots.Slots[instance.InvocationSlot],instance))
                    slots.Slots[instance.InvocationSlot]=new(instance.Object,instance.Root,instance.IsRsl,instance.InstancePath)
                    {IsOrdinaryHlodChild=instance.IsOrdinaryHlodChild,IsShipLegoController=instance.IsShipLegoController,LegoControllerBaseAsset=instance.LegoControllerBaseAsset};
                instance.InvocationStorage=null;
            }
            tree.Persistent=tree.PersistentValid=true;_rslSceneTreeRegistrations++;
        }
    }
    private bool MutateRslCallbackTree(SceneOwner source,RetainedRslCallbackTree tree,out string? diagnostic)
    {
        diagnostic=null;
        if(!ReferenceEquals(source.RslCallbackTree,tree) || tree.Tops.Count!=tree.Evaluated.Count)return false;
        for(int i=0;i<tree.Tops.Count;i++)
            if(!ReferenceEquals(tree.Tops[i].Root,tree.Evaluated[i].Root))return false;
        for(int i=0;i<tree.Tops.Count;i++)
        {
            var instance=tree.Tops[i];
            instance.UpdateCallbackAnimation(tree.Evaluated[i].Blend ?? ResolveShipWarpAnimation(instance.Object,
                instance.Root,instance.InstancePath,instance.Object.Spatial?.Transform.Position??System.Numerics.Vector3.Zero));
        }
        foreach(var instance in tree.Instances)
        {
            bool admitted=instance.ShouldDraw;
            bool refreshed=TryRefreshOrdinaryHlodChildCore(instance,false,out diagnostic);
            // Callback-time pose mutation must not repeat the preceding scene
            // cull/detail traversal or revive a rejected wrapper/child.
            instance.ShouldDraw=admitted;
            if(!refreshed)return false;
        }
        return true;
    }
}
