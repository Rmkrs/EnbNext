using System.Numerics;
using System.Collections.Immutable;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly HashSet<RetainedEmitterKey> _removedRetainedEmitters=[];
    private readonly Dictionary<RetainedEmitterKey,RetainedCompositeMembership> _retainedCompositeMembership=[];
    private readonly Dictionary<(ulong Owner,ulong Generation,string Path,object? PhysicalFactory,object? BoneFactory),CompositeFrameSlot> _retainedCompositeFrameRoots=[];

    //Membership belongs to the constructed occurrence, never its shared FILE
    //prototype. Keep original storage ordinals stable across later removals.
    private sealed class RetainedCompositeMembership(LiveCpuRoot prototype)
    {
        public readonly LiveCpuRoot Prototype=prototype;
        public Action? Changed;
        public readonly HashSet<int> Removed=[];
        public readonly Dictionary<int,LiveCpuRoot> Replacements=[];
        public readonly Dictionary<string,(uint Pivot,EnbW3dRenderCompositeChild Child)> Attachments=[];
        public LiveCpuRoot QueryRoot=prototype;
        public LiveCpuRoot DrawRoot=prototype;

        private LiveCpuRoot? _viewRoot;
        private EnbW3dRenderResource? _viewSource, _drawView;
        public EnbW3dRenderResource DrawResource(EnbW3dRenderResource source)
        {
            if(ReferenceEquals(_viewRoot,DrawRoot)&&ReferenceEquals(_viewSource,source))return _drawView!;
            _viewRoot=DrawRoot;_viewSource=source;
            return _drawView=source.Copy().SetHierarchicalLods(DrawRoot.Resource.HierarchicalLods);
        }

        public void Rebuild()
        {
            _viewRoot=null;_viewSource=null;_drawView=null;
            _viewRoot=null;_viewSource=null;_drawView=null;
            if(Prototype.Collection is not null)
            {
                QueryRoot=Prototype with {CollectionChildren=Prototype.CollectionChildren
                    .Select((child,index)=>(Child:Replacements.GetValueOrDefault(index,child),Index:index))
                    .Where(pair=>!Removed.Contains(pair.Index)).Select(pair=>pair.Child).ToArray()};
                Changed?.Invoke();return;
            }
            var hlod=Prototype.Hlod!;
            //Drawing retains the original names and concrete child owners.
            //Query descriptors below additionally carry dynamic attachments;
            //those owners are submitted separately and must not draw twice.
            int drawOrdinal=0;
            IReadOnlyList<EnbW3dRenderHlodMember> DrawMembers(IReadOnlyList<EnbW3dRenderHlodMember> members)
                =>members.Where(_=>!Removed.Contains(drawOrdinal++)).ToArray();
            var drawHlod=hlod with {
                Levels=hlod.Levels.Select(level=>level with {Members=DrawMembers(level.Members)}).ToArray(),
                Aggregate=hlod.Aggregate is {} drawAggregate?drawAggregate with {Members=DrawMembers(drawAggregate.Members)}:null};
            DrawRoot=Prototype with {Hlod=drawHlod,Resource=Prototype.Resource.Copy().SetHierarchicalLods([drawHlod])};
            //A named child's snapshot preserves its already configured mesh
            //and hierarchy words.98E4B0/98E0F0 still mutate its membership:
            //rebuild from those words instead of letting the old snapshot
            //short-circuit the resulting query in TryComputeRootLocalBounds.
            var queryPrototype=Prototype.NamedChildBoundsRoot??Prototype;
            var descriptors=queryPrototype.Resource.CompositeRenderChildren.ToList();
            int ordinal=0;
            IReadOnlyList<EnbW3dRenderHlodMember> Members(IReadOnlyList<EnbW3dRenderHlodMember> members)
            {
                var result=new List<EnbW3dRenderHlodMember>();
                foreach(var member in members)
                {
                    int slot=ordinal++;
                    if(Removed.Contains(slot))continue;
                    if(Replacements.TryGetValue(slot,out var child))
                    {
                        string name=$"<retained-bounds:{hlod.Name}:{slot}>";
                        if(!TryDescribeCompositeChild(child,name,out var descriptor,out var error))throw new InvalidDataException(error);
                        descriptors.Add(descriptor);result.Add(member with {Name=name});
                    }
                    else result.Add(member);
                }
                return result;
            }
            var levels=hlod.Levels.Select(level=>level with {Members=Members(level.Members)}).ToArray();
            var aggregateMembers=hlod.Aggregate is {} aggregate?Members(aggregate.Members).ToList():[];
            foreach(var (name,attachment) in Attachments)
            {
                descriptors.Add(attachment.Child with {Name=name});
                aggregateMembers.Add(new(attachment.Pivot,name));
            }
            var retained=hlod with {Levels=levels,
                Aggregate=aggregateMembers.Count!=0 || hlod.Aggregate is not null
                    ?(hlod.Aggregate??new(0,[])) with {Members=aggregateMembers}:null};
            QueryRoot=queryPrototype with {Hlod=retained,NamedChildBoundsRoot=null,Resource=queryPrototype.Resource.Copy().SetHierarchicalLods([retained]).SetCompositeRenderChildren(descriptors.ToArray())};
            Changed?.Invoke();
        }
        public bool HasChildren=>QueryRoot.Collection is not null?QueryRoot.CollectionChildren.Count!=0:
            QueryRoot.Hlod!.Levels.Any(level=>level.Members.Count!=0)||QueryRoot.Hlod.Aggregate?.Members.Count>0;
    }

    private static RetainedEmitterKey CompositeMembershipKey(LiveRootInstance instance)
        =>RetainedEmitterOwnerKey(instance,1,Vector3.Zero);

    private LiveCpuRoot RetainedDrawRoot(LiveRootInstance instance)
        =>CompositeMembership(instance) is {} membership
            && membership.Removed.Count!=0?membership.DrawRoot:instance.Root;

    private EnbW3dRenderResource RetainedDrawResource(LiveRootInstance instance)
    {
        var resource=instance.AnimationBlend?.DrawResource??instance.Root.Resource;
        return CompositeMembership(instance) is {} membership
            && membership.Removed.Count!=0 && !ReferenceEquals(membership.DrawRoot,instance.Root) ? membership.DrawResource(resource) : resource;
    }

    private bool TryResolveRetainedHlodCompiledCosts(LiveRootInstance instance,
        out ImmutableArray<int> levels,out float aggregate,out string? diagnostic)
    {
        return TryResolveOwnedHlodCosts(instance,out levels,out aggregate,out diagnostic);
    }

    private static bool IsDamageChildName(string name)
        =>name.StartsWith("d4m",StringComparison.OrdinalIgnoreCase)||name.StartsWith("mobdam",StringComparison.OrdinalIgnoreCase);

    private void RemoveAuthoredDamageChildren(LiveRootInstance parent,RootTaskList roots,long now,bool remove,uint? pivot=null,
        IEnumerable<LiveRootInstance>? queuedChildren=null)
    {
        if(parent.Root.Hlod is not {} hlod)return;
        //81189C/811C44 enumerate current native membership, including Bone
        //attachments and hidden children. Detach the owner, not the retained
        //RSL state: a later458B20 activation can attach that same child again.
        var detached=new HashSet<EnbCurrentRetailRslBoneRenderObjectRuntime>();
        if(remove && parent.Root.RslBoneNode is {} rsl)
            foreach(var member in rsl.Runtime.Aggregate.Reverse().ToArray())
                if((pivot is null || member.BoneIndex==pivot) &&
                    member.Child is EnbCurrentRetailRslBoneRenderObjectRuntime runtime &&
                    rsl.DynamicChildren.TryGetValue(runtime,out var child) && IsDamageChildName(child.Root.RuntimeRenderObjectName) &&
                    rsl.Runtime.RemoveChild(runtime))detached.Add(runtime);
        var occurrence=(_shipExhaustOwner??this).ResolveCompositeOccurrence(parent);
        var membership=occurrence.Membership;
        if(_shipExhaustOwner is null && parent.Root.RslBoneNode is not null &&
            (!_ownerLocalShipChildTasks || occurrence.DamageAttachmentsDirty))
        {
            //98E4B0/98E0F0 refresh native bounds immediately. Blood reads
            //those bounds before creating its replacement effects.
            if(membership is null)membership=CreateCompositeMembership(parent);
            foreach(string name in membership.Attachments.Keys.Where(name=>name.StartsWith("<rsl-bone:",StringComparison.Ordinal)).ToArray())
                membership.Attachments.Remove(name);
            foreach(var pair in RslBoneCompositeMembers(parent.Root))membership.Attachments.Add(pair.Key,pair.Value);
            InvalidateLegoSceneOwner(parent.Object.RenderIdentity);
            membership.Rebuild();PropagateRetainedCompositeBounds(parent,membership);
            // Runtime membership and exported-definition events invalidate this
            // binding. Repeated damage commands do not rebuild unchanged bounds.
            occurrence.DamageAttachmentsDirty=false;
        }
        if(remove)
        {
            int ordinal=0;bool changed=false;
            foreach(var member in hlod.Levels.SelectMany(level=>level.Members).Concat(hlod.Aggregate?.Members??[]))
            {
                int slot=ordinal++;
                if(pivot is {} selected&&member.ReferenceIndex!=selected)continue;
                //48B220's registry key is not RenderObj+10's runtime name.
                string name=parent.Root.HmodelChildren.FirstOrDefault(child=>child.StorageOrdinal==slot)?.Root.RuntimeRenderObjectName??member.Name;
                if(!IsDamageChildName(name))continue;
                if(membership is null)membership=CreateCompositeMembership(parent);
                changed|=membership.Removed.Add(slot);
            }
            if(changed)
            {
                InvalidateLegoSceneOwner(parent.Object.RenderIdentity);
                membership!.Rebuild();
                PropagateRetainedCompositeBounds(parent,membership);
            }
        }
        if(membership is not null)parent.RetainedQueryRoot=membership.QueryRoot;
        var removed=(queuedChildren??roots).Where(child=>ReferenceEquals(child.Parent,parent)&&
            (child.RslChild is {} runtime&&detached.Contains(runtime) ||
             child.ParentStorageOrdinal is {} slot&&membership?.Removed.Contains(slot)==true)).ToArray();
        foreach(var child in removed)
        {
            //98E0F0 removes the child owner, not its independent live buffer.
            //Destroy stops births and lets that buffer drain normally.
            if(_shipExhaustOwner is null)foreach(var owner in _retainedEmitters.Values)
                if(owner.LastInstance is {} retained && retained.Object.RenderIdentity==child.Object.RenderIdentity &&
                    retained.Object.VisualGeneration==child.Object.VisualGeneration &&
                    (retained.InstancePath==child.InstancePath||retained.InstancePath.StartsWith(child.InstancePath+"/",StringComparison.Ordinal)))
                    owner.State.Destroy(checked((uint)(now-owner.Start)),owner.World,owner.Scale);
            roots.RemoveAll(instance=>DescendsFrom(instance,child));
        }
    }

    private void PropagateRetainedCompositeBounds(LiveRootInstance child,RetainedCompositeMembership membership)
    {
        if(child.Parent is not {} parent||child.ParentStorageOrdinal is not {} slot||
            (parent.Root.Hlod is null&&parent.Root.Collection is null))return;
        var ancestor=CreateCompositeMembership(parent);
        InvalidateLegoSceneOwner(parent.Object.RenderIdentity);
        ancestor.Replacements[slot]=membership.QueryRoot;
        //98F1D0/9912B0's empty branch does not notify the ancestor.
        if(membership.HasChildren){ancestor.Rebuild();PropagateRetainedCompositeBounds(parent,ancestor);}
        parent.RetainedQueryRoot=ancestor.QueryRoot;
    }

    private readonly List<Action> _particleSceneRemovals=[];
    private readonly HashSet<(EnbRetailParticleSceneLifetime State,bool Buffer)> _particleSceneRemovalOwners=[];
    private void QueueParticleSceneRemoval(EnbRetailParticleSceneLifetime state,bool buffer,Action remove)
    {
        if(_particleSceneRemovalOwners.Add((state,buffer)))_particleSceneRemovals.Add(remove);
    }
    private void FinishParticleScene(long now)
    {
        if(_shipExhaustOwner is not null)return;
        //965460 calls scene+58 (9662E0) after 9661C0's traversal, before
        //returning to another view. A second view must see these removals
        //without requiring another primary frame or a clock advance.
        //965E00(type2) prepends;9662E0 visits newest requests first. Native
        //particleRemovalOrder also checks duplicate requests/refcounts.
        for(int i=_particleSceneRemovals.Count-1;i>=0;i--)_particleSceneRemovals[i]();
        _particleSceneRemovals.Clear();
        _particleSceneRemovalOwners.Clear();
        foreach(var occurrence in _dirtyCompositeOccurrences)
        {
            _compositePropagationVisits++;
            if(occurrence.Valid && occurrence.FrameSlot.Epoch==_compositeFrameEpoch &&
                occurrence.FrameSlot.Current is {} instance && ReferenceEquals(instance.PreparedCompositeOccurrence,occurrence))
                instance.RetainedQueryRoot=occurrence.Membership?.QueryRoot;
        }
        _dirtyCompositeOccurrences.Clear();
    }

    private void PruneRetainedChildMembership(IReadOnlySet<(ulong Identity,ulong Generation)> live)
    {
        _removedRetainedEmitters.RemoveWhere(key=>!RetainedParticleOwnerIsLive(key,live));
        foreach(var occurrence in _compatCompositeOccurrences.Where(value=>!RetainedParticleOwnerIsLive(value.Key,live)).ToArray())
            RetireCompositeOccurrence(occurrence);
    }

    private void RetireRetainedChild(RetainedEmitterKey key,RetainedEmitterOwner owner,long now)
    {
        if(owner.LastInstance?.Root.RslBoneNode is not null)
        {RetireBoneParticle(key,owner,now);return;}
        if(owner.State.Destroyed || owner.LastInstance is not {Parent:{} parent,ParentStorageOrdinal:{} slot})return;
        //9662E0 -> HLOD98E0F0 / Collection990E50. Direct RSL root
        //461EF0 forwards to emitter+6C (462A50), which returns false.
        _removedRetainedEmitters.Add(key);
        //990F36 restores the saved collection matrix through child+50.
        //For local emitters97DD90 invalidates buffer bounds even unchanged.
        if(parent.Root.Collection is not null)owner.State.UpdateTransform(owner.World);
        owner.State.Destroy(checked((uint)(now-owner.Start)),owner.World,owner.Scale);
        try {Update(parent,slot,null);}
        catch(InvalidDataException error)
        {
            //Retain the last valid bounds if an optional child's descriptor
            //cannot be recomputed; never abort the rest of the scene.
            _retainedEmitterErrors.Add($"Retained child bounds '{parent.Root.RootRenderObjectName}': {error.Message}");
        }
        void Update(LiveRootInstance parent,int slot,LiveCpuRoot? replacement)
        {
                var membership=CreateCompositeMembership(parent);
            if(replacement is null)membership.Removed.Add(slot);
            else membership.Replacements[slot]=replacement;
            InvalidateLegoSceneOwner(parent.Object.RenderIdentity);
            membership.Rebuild();
            //98F1D0/9912B0 empty branches zero their own local storage and
            //return. Only their nonempty branches forward parent+100.
            if(parent.Parent is {} ancestor && parent.ParentStorageOrdinal is {} ancestorSlot &&
                (ancestor.Root.Hlod is not null || ancestor.Root.Collection is not null))
            {
                if(membership.HasChildren)Update(ancestor,ancestorSlot,membership.QueryRoot);
                else
                {
                    //The child object has changed, although the parent's cached
                    //bounds do not refresh until a later nonempty update.
                    var retainedAncestor=CreateCompositeMembership(ancestor);
                    InvalidateLegoSceneOwner(ancestor.Object.RenderIdentity);
                    retainedAncestor.Replacements[ancestorSlot]=membership.QueryRoot;
                }
            }
        }
    }

    private void ApplyRetainedChildMembership(RootTaskList roots,HashSet<LiveRootInstance> removed,CallbackRootRefreshScratch? refresh=null)
    {
        // A scene callback replaces only its own occurrence references. Other
        // owners keep their prepared query roots and frame bindings intact.
        if(refresh is null)_compositeFrameEpoch++;
        else foreach(var previous in refresh.Previous)
            ResolveCompositeOccurrence(previous).FrameSlot.Current=null;
        foreach(var instance in refresh?.Current??roots)
        {
            var occurrence=ResolveCompositeOccurrence(instance);
            occurrence.FrameSlot.Epoch=_compositeFrameEpoch;
            occurrence.FrameSlot.Current=instance;
            instance.RetainedQueryRoot=occurrence.Membership?.QueryRoot;
        }
        if(removed.Count!=0)
        {roots.RemoveAll(removed.Contains);refresh?.Current.RemoveAll(removed.Contains);}
    }

    private void BorrowRetainedChildMembership(RootTaskList roots,EnbWorldRenderer source)
    {
        var detached=roots.Where(instance=>
        {
            if(instance.RslChild is null || instance.Parent is not {} parent)return false;
            return TryResolveScannerPrimitiveSource(parent,source,out _) &&
                !TryResolveScannerPrimitiveSource(instance,source,out _);
        }).ToArray();
        roots.RemoveAll(instance=>detached.Any(child=>DescendsFrom(instance,child)));
        roots.RemoveAll(instance=>
        {
            if(!TryResolveScannerPrimitiveSource(instance,source,out var retained))return false;
            instance.RetainedQueryRoot=retained.RetainedQueryRoot;
            return retained.Root.RetainedEmitter is not null &&
                TryResolvePrimitiveFactoryTransform(retained,out _,out float scale,out var hsv) &&
                source._removedRetainedEmitters.Contains(RetainedEmitterOwnerKey(retained,scale,hsv));
        });
    }
}
