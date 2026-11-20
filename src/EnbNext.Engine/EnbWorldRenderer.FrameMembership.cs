using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // View-local membership worksets. These contain scalar generation keys,
    // never scene objects. Consumers finish in RenderFrame; no set is published
    // as a snapshot or borrowed by another view.
    private readonly EnbRetainedMembershipSet<ulong> _frameObjectIdentities=new();
    private readonly HashSet<ulong> _frameRenderedObjects=[];
    private readonly EnbRetainedMembershipSet<(ulong,ulong)> _frameObjectVisualOwners=new(),_frameActiveObjects=new(),
        _framePrimitiveOwners=new(),_frameMaterialOwners=new();
    private readonly HashSet<(ulong,ulong)> _residentGpuOwners=[];
    private readonly EnbRetainedMembershipSet<(ulong,ulong)> _frameRetainedGpuOwners=new();
    private readonly EnbRetainedMembershipSet<(ulong,ulong,ulong)> _frameFixedOwners=new();
    private void BeginFrameMembership(LiveFrame frame)
    {
        ClearFrameMembership();
        _frameObjectIdentities.BeginFrame(_ownedSceneFrame);_frameObjectVisualOwners.BeginFrame(_ownedSceneFrame);
        _frameActiveObjects.BeginFrame(_ownedSceneFrame);_framePrimitiveOwners.BeginFrame(_ownedSceneFrame);
        _frameMaterialOwners.BeginFrame(_ownedSceneFrame);_frameFixedOwners.BeginFrame(_ownedSceneFrame);
        _frameRetainedGpuOwners.BeginFrame(_ownedSceneFrame);
        if(_ownedSceneFrame)return;
        foreach(var obj in SnapshotObjects(frame.Snapshot))
        {
            _frameObjectIdentities.Add(obj.RenderIdentity);
            _frameObjectVisualOwners.Add((obj.RenderIdentity,obj.VisualGeneration));
            _frameActiveObjects.Add((obj.RenderIdentity,obj.RenderObjectGeneration));
            _framePrimitiveOwners.Add((obj.RenderIdentity,obj.VisualGeneration));
            _frameMaterialOwners.Add((obj.RenderIdentity,obj.VisualGeneration));
            _frameFixedOwners.Add((obj.RenderIdentity,obj.BaseAssetMaterializationRevision,obj.FixedShipVisualRevision));
        }
    }
    private void ClearFrameMembership()
    {
        _frameObjectIdentities.ClearFrame();_frameRenderedObjects.Clear();_frameObjectVisualOwners.ClearFrame();
        _frameActiveObjects.ClearFrame();_framePrimitiveOwners.ClearFrame();_frameMaterialOwners.ClearFrame();
        _frameRetainedGpuOwners.ClearFrame();_frameFixedOwners.ClearFrame();
    }
    private int _ownedNameDecalSources;
    private void AttachSceneMembership(IEnbRenderObject obj)
    {
        if(obj.NameDecal is not null)_ownedNameDecalSources++;
        _gpuLiveVisuals.Attach((obj.RenderIdentity,obj.VisualGeneration));
        _gpuLiveRenderObjects.Attach((obj.RenderIdentity,obj.RenderObjectGeneration));
        if(obj.IsLocalShip)_gpuLocalVisuals.Attach((obj.RenderIdentity,obj.VisualGeneration));
        _frameEmitterResidents.Attach((obj.RenderIdentity,obj.VisualGeneration));
        _frameObjectIdentities.Attach(obj.RenderIdentity);
        _frameObjectVisualOwners.Attach((obj.RenderIdentity,obj.VisualGeneration));
        _frameActiveObjects.Attach((obj.RenderIdentity,obj.RenderObjectGeneration));
        _framePrimitiveOwners.Attach((obj.RenderIdentity,obj.VisualGeneration));
        _frameMaterialOwners.Attach((obj.RenderIdentity,obj.VisualGeneration));
        _frameFixedOwners.Attach((obj.RenderIdentity,obj.BaseAssetMaterializationRevision,obj.FixedShipVisualRevision));
    }
    private void DetachSceneMembership(IEnbRenderObject obj)
    {
        if(obj.NameDecal is not null)_ownedNameDecalSources--;
        _gpuLiveVisuals.Detach((obj.RenderIdentity,obj.VisualGeneration));
        _gpuLiveRenderObjects.Detach((obj.RenderIdentity,obj.RenderObjectGeneration));
        if(obj.IsLocalShip)_gpuLocalVisuals.Detach((obj.RenderIdentity,obj.VisualGeneration));
        _frameEmitterResidents.Detach((obj.RenderIdentity,obj.VisualGeneration));
        _frameObjectIdentities.Detach(obj.RenderIdentity);
        _frameObjectVisualOwners.Detach((obj.RenderIdentity,obj.VisualGeneration));
        _frameActiveObjects.Detach((obj.RenderIdentity,obj.RenderObjectGeneration));
        _framePrimitiveOwners.Detach((obj.RenderIdentity,obj.VisualGeneration));
        _frameMaterialOwners.Detach((obj.RenderIdentity,obj.VisualGeneration));
        _frameFixedOwners.Detach((obj.RenderIdentity,obj.BaseAssetMaterializationRevision,obj.FixedShipVisualRevision));
    }
    private readonly RootStagingScratch _rootStagingScratch=new();
    private sealed class RootStagingScratch : IDisposable
    {
        private bool active;
        public readonly RootTaskList Roots=[];
        public readonly List<ShipAttachmentOwner> FixedSearchNodes=[];
        public RootStagingScratch Rent()
        {
            if(active)return new RootStagingScratch().Rent();
            active=true;return this;
        }
        public void Dispose(){Roots.Clear();FixedSearchNodes.Clear();active=false;}
    }
    // One callback's membership/replacement work. Queued draw state never
    // points into these containers; clear all owner references at scope exit.
    private readonly CallbackRootRefreshScratch _callbackRootRefreshScratch = new();
    private sealed class CallbackRootRefreshScratch : IDisposable
    {
        private bool active;
        private readonly HashSet<ulong> localIdentities=[];
        private HashSet<ulong>? ownedIdentities;
        internal HashSet<ulong> Identities=>ownedIdentities??localIdentities;
        internal void UseOwner(HashSet<ulong> identities)=>ownedIdentities=identities;
        internal bool PersistentTopology;
        internal readonly RootTaskList Previous = [], Current = [];
        internal readonly Dictionary<LiveRootInstance,LiveRootInstance> Replacements = new(ReferenceEqualityComparer.Instance);
        internal readonly Predicate<LiveRootInstance> BelongsPredicate;
        internal readonly Predicate<LiveRootInstance> CollectPreviousPredicate;
        private readonly List<ShipExhaustKey> exhaustRemovals=[];
        private readonly Predicate<WorldSphereKey> sphereBelongs;
        private readonly Predicate<WorldRingKey> ringBelongs;
        internal CallbackRootRefreshScratch()
        {BelongsPredicate=Belongs;CollectPreviousPredicate=CollectPrevious;sphereBelongs=SphereBelongs;ringBelongs=RingBelongs;}
        private bool SphereBelongs(WorldSphereKey key)=>Identities.Contains(key.Identity);
        private bool RingBelongs(WorldRingKey key)=>Identities.Contains(key.Identity);
        internal void ClearObservations(EnbWorldRenderer view)
        {
            exhaustRemovals.Clear();
            foreach(var key in view._frameShipExhaust.Keys)if(Identities.Contains(key.Owner))exhaustRemovals.Add(key);
            foreach(var key in exhaustRemovals)view._frameShipExhaust.Remove(key);
            exhaustRemovals.Clear();
            view._worldSphereObserved.RemoveWhere(sphereBelongs);
            view._worldRingObserved.RemoveWhere(ringBelongs);
        }
        internal LiveRootInstance? FindPrevious(LiveRootInstance instance)
        {
            foreach(var prior in Previous)
                if(prior.InstancePath==instance.InstancePath && ReferenceEquals(prior.Root.PrimitiveFactoryIdentity,instance.Root.PrimitiveFactoryIdentity))return prior;
            return null;
        }
        internal LiveRootInstance? FindWrapper(ulong identity)
        {foreach(var prior in Previous)if(prior.Object.RenderIdentity==identity && prior.InstancePath=="root")return prior;return null;}
        internal void PreserveAttachments(List<(LiveRootInstance Parent,LiveRootInstance Child,int Bone)> attachments,RootTaskList roots)
        {
            for(int i=0;i<attachments.Count;i++)
            {
                var attachment=attachments[i];
                if(!Replacements.TryGetValue(attachment.Parent,out var parent))continue;
                foreach(var child in Previous)
                {
                    bool descendant=false;
                    for(var cursor=child;cursor is not null;cursor=cursor.Parent)
                        if(ReferenceEquals(cursor,attachment.Child)){descendant=true;break;}
                    if(!descendant)continue;
                    if(ReferenceEquals(child,attachment.Child))child.Parent=parent;
                    if(!Current.Contains(child)){roots.Add(child);Current.Add(child);}
                }
                attachments[i]=(parent,attachment.Child,attachment.Bone);
            }
        }
        private bool CollectPrevious(LiveRootInstance instance)
        {if(!Belongs(instance))return false;Previous.Add(instance);return true;}
        internal bool Belongs(LiveRootInstance instance) => Identities.Contains(instance.Object.RenderIdentity);
        internal CallbackRootRefreshScratch Rent()
        {
            if(active)return new CallbackRootRefreshScratch().Rent();
            active=true;return this;
        }
        public void Dispose()
        {
            PersistentTopology=false;ownedIdentities=null;localIdentities.Clear();Previous.Clear();Current.Clear();Replacements.Clear();exhaustRemovals.Clear();active=false;
        }
    }
    private readonly SceneModelPathScratch _sceneModelPathScratch = new();
    private sealed class SceneModelPathScratch : IDisposable
    {
        private bool active;
        internal readonly Stack<LiveRootInstance> Path = new();
        internal SceneModelPathScratch Rent()
        {
            if(active)return new SceneModelPathScratch().Rent();
            active=true;return this;
        }
        public void Dispose() { Path.Clear();active=false; }
    }
}
