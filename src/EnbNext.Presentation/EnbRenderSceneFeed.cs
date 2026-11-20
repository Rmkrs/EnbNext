using System.Collections.Concurrent;
using System.Collections.Immutable;
using EnbNext.Net7.World;

namespace EnbNext.Presentation;

public enum EnbRenderObjectChangeKind { Reset, Add, Definition, Remove }
public readonly record struct EnbRenderObjectChange(EnbRenderObjectChangeKind Kind,
    ulong Identity, ulong? After, EnbSceneObjectSnapshot? Definition)
{
    public int Slot {get;init;}=-1;
    public EnbRenderPoseOwner? PoseOwner {get;init;}
}
public sealed record EnbRenderSceneChangeBatch(ulong Sequence, ImmutableArray<EnbRenderObjectChange> Changes);

/// <summary>One renderer's ordered detached mutation stream. Frame coalescing
/// does not discard mutations; later publications stay queued until their frame.</summary>
public sealed class EnbRenderSceneSubscription : IDisposable
{
    private readonly ConcurrentQueue<EnbRenderSceneChangeBatch> _pending=new();
    private int _disposed;
    internal bool IsDisposed=>Volatile.Read(ref _disposed)!=0;
    internal void Publish(EnbRenderSceneChangeBatch batch)
    {
        if(IsDisposed)return;
        _pending.Enqueue(batch);
        if(IsDisposed)_pending.Clear();
    }
    public bool TryReadThrough(ulong sequence,out EnbRenderSceneChangeBatch batch)
    {
        if(_pending.TryPeek(out var next)&&next.Sequence<=sequence&&_pending.TryDequeue(out batch!))return true;
        batch=null!;return false;
    }
    public void Dispose(){Interlocked.Exchange(ref _disposed,1);_pending.Clear();}
}

/// <summary>Publication-side adapter. World registry mutations establish
/// membership; immutable definitions cross threads. This is not a render-frame
/// snapshot reconciliation service. Subscribe before sampling the first frame.</summary>
public sealed class EnbRenderSceneFeed
{
    private readonly object _gate=new();
    private readonly List<WeakReference<EnbRenderSceneSubscription>> _subscribers=[];
    private sealed class Entry(Net7WorldObject source,EnbSceneObjectSnapshot definition)
    {
        public readonly Net7WorldObject Source=source;
        public readonly EnbRenderPoseOwner Pose=new();
        public EnbSceneObjectSnapshot Definition=definition;
        public LinkedListNode<Entry> Link=null!;
        public ulong AddedSequence;
        public int Slot;
        public Net7WorldObjectBinding Binding=null!;
    }
    private readonly Dictionary<Net7WorldObject,Entry> _entries=new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Entry> _ordered=new();
    private readonly HashSet<Net7WorldObject> _dirty=new(ReferenceEqualityComparer.Instance);
    private readonly List<Net7WorldObject> _dirtyOrder=[];
    private Net7WorldRegistry? _registry;
    private Net7WorldRenderSubscription? _mutations;
    private Net7WorldObject? _localShip,_target;
    private object? _assets,_itemTemplates;
    private ulong _sequence;

    private readonly Stack<int> _freeSlots=[];
    private int _slotCount;
    private Net7WorldState? _samplingWorld;
    private Net7PresentationSamplingFrame? _sampling;
    internal int SlotFor(Net7WorldObject source)=>_entries[source].Slot;
    private EnbRenderPoseOwner?[] _poseOwners=[];
    private ulong _poseMembership,_sampledPoseMembership=ulong.MaxValue;
    internal EnbRenderPoseSet SamplePoses(Net7WorldState world,uint time)
    {
        lock(_gate)
        {
            if(!ReferenceEquals(_samplingWorld,world)){_samplingWorld=world;_sampling=new(world);}
            _sampling!.Begin(time);
            if(_sampledPoseMembership!=_poseMembership)
            {
                if(_poseOwners.Length!=_slotCount)_poseOwners=new EnbRenderPoseOwner?[_slotCount];
                else Array.Clear(_poseOwners);
                foreach(var member in _ordered)_poseOwners[member.Slot]=member.Pose;
                _sampledPoseMembership=_poseMembership;
            }
            foreach(var entry in _ordered)
            {
                entry.Pose.Value=entry.Definition.Spatial is not null&&_sampling.TrySample(entry.Binding,out var sample)
                    ?new(true,sample):default;
            }
            return new(_poseOwners);
        }
    }
    private void ReleaseEntries()
    {
        foreach(var entry in _ordered)entry.Binding.Dispose();
        _entries.Clear();_ordered.Clear();_freeSlots.Clear();_slotCount=0;_poseOwners=[];_poseMembership++;
    }
    public long DefinitionProjections {get;private set;}
    public long SourceChanges {get;private set;}
    public EnbRenderSceneSubscription Subscribe()
    {
        lock(_gate)
        {
            var result=new EnbRenderSceneSubscription();
            var changes=ImmutableArray.CreateBuilder<EnbRenderObjectChange>(_ordered.Count+1);
            changes.Add(new(EnbRenderObjectChangeKind.Reset,0,null,null));
            ulong? after=null;
            foreach(var entry in _ordered)
            {
                var obj=entry.Definition;
                changes.Add(new(EnbRenderObjectChangeKind.Add,obj.RenderIdentity,after,obj){Slot=entry.Slot,PoseOwner=entry.Pose});
                after=obj.RenderIdentity;
            }
            result.Publish(new(_sequence,changes.MoveToImmutable()));
            _subscribers.Add(new(result));return result;
        }
    }

    // The caller owns the source mutation boundary. Only changed source nodes
    // are projected here; the diagnostic snapshot is not an input to this feed.
    internal ulong Publish(Net7WorldState world,object? assets,object? itemTemplates,
        Func<Net7WorldObject,EnbSceneObjectSnapshot> project)
    {
        lock(_gate)
        {
            var registry=world.Objects;
            var changes=ImmutableArray.CreateBuilder<EnbRenderObjectChange>();
            if(!ReferenceEquals(_registry,registry))
            {_mutations?.Dispose();_registry=registry;_mutations=registry.SubscribeRenderChanges();}
            void Dirty(Net7WorldObject? source)
            {if(source is not null&&_dirty.Add(source))_dirtyOrder.Add(source);}
            EnbSceneObjectSnapshot Project(Net7WorldObject source)
            {DefinitionProjections++;return project(source);}
            while(_mutations!.TryRead(out var change))
            {
                SourceChanges++;
                switch(change.Kind)
                {
                    case Net7WorldRenderChangeKind.Reset:
                        ReleaseEntries();_dirty.Clear();_dirtyOrder.Clear();
                        changes.Add(new(EnbRenderObjectChangeKind.Reset,0,null,null));break;
                    case Net7WorldRenderChangeKind.Add:
                        var definition=Project(change.Object!);
                        var added=new Entry(change.Object!,definition){AddedSequence=_sequence+1,
                            Slot=_freeSlots.TryPop(out int available)?available:_slotCount++,
                            Binding=registry.BindFirst(change.Object!.GameId)};
                        ulong? after=null;
                        if(change.After is {} predecessor)
                        {
                            var preceding=_entries[predecessor];
                            added.Link=_ordered.AddAfter(preceding.Link,added);
                            after=preceding.Definition.RenderIdentity;
                        }
                        else added.Link=_ordered.AddFirst(added);
                        _entries.Add(added.Source,added);_poseMembership++;
                        changes.Add(new(EnbRenderObjectChangeKind.Add,definition.RenderIdentity,after,definition){Slot=added.Slot,PoseOwner=added.Pose});
                        break;
                    case Net7WorldRenderChangeKind.Definition:Dirty(change.Object);break;
                    case Net7WorldRenderChangeKind.Remove:
                        if(_entries.Remove(change.Object!,out var removed))
                        {
                            _poseMembership++;_ordered.Remove(removed.Link);removed.Binding.Dispose();_freeSlots.Push(removed.Slot);
                            changes.Add(new(EnbRenderObjectChangeKind.Remove,removed.Definition.RenderIdentity,null,null));
                        }
                        break;
                }
            }
            var local=world.LocalShipObjectId is uint localId?registry.FindFirst(localId):null;
            var target=world.CurrentTargetId is uint targetId?registry.FindFirst(targetId):null;
            if(!ReferenceEquals(local,_localShip)){Dirty(_localShip);Dirty(local);_localShip=local;}
            if(!ReferenceEquals(target,_target)){Dirty(_target);Dirty(target);_target=target;}
            // These are explicit catalog replacement invalidations, not a frame sweep.
            if(!ReferenceEquals(assets,_assets)||!ReferenceEquals(itemTemplates,_itemTemplates))
            {foreach(var entry in _ordered)Dirty(entry.Source);_assets=assets;_itemTemplates=itemTemplates;}
            foreach(var source in _dirtyOrder)
                if(_entries.TryGetValue(source,out var entry)&&entry.AddedSequence!=_sequence+1)
                {
                    var definition=Project(source);entry.Definition=definition;
                    changes.Add(new(EnbRenderObjectChangeKind.Definition,definition.RenderIdentity,null,definition));
                }
            _dirty.Clear();_dirtyOrder.Clear();
            ulong sequence=++_sequence;
            if(changes.Count!=0)Deliver(new(sequence,changes.ToImmutable()));
            return sequence;
        }
    }

    internal void Clear()
    {
        lock(_gate)
        {
            _mutations?.Dispose();_mutations=null;_registry=null;
            ReleaseEntries();_dirty.Clear();_dirtyOrder.Clear();
            _localShip=null;_target=null;_assets=null;_itemTemplates=null;_sampling=null;_samplingWorld=null;
            Deliver(new(++_sequence,[new(EnbRenderObjectChangeKind.Reset,0,null,null)]));
        }
    }
    private void Deliver(EnbRenderSceneChangeBatch batch)
    {
        for(int i=_subscribers.Count-1;i>=0;i--)
            if(_subscribers[i].TryGetTarget(out var subscriber)&&!subscriber.IsDisposed)subscriber.Publish(batch);
            else _subscribers.RemoveAt(i);
    }
}
