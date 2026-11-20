using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Render-thread-owned ordered objects. Identity lookup is restricted
/// to mutation application and explicit external queries. Traversal follows
/// retained nodes; removals release ownership only after consuming flushes.</summary>
public sealed class EnbRetainedRenderScene<TState> where TState:class
{
    public sealed class Node
    {
        internal Node(EnbSceneObjectSnapshot definition,TState state)
        {Definition=definition;State=state;}
        public EnbSceneObjectSnapshot Definition {get;internal set;}
        public TState State {get;}
        public int Slot {get;internal set;}=-1;
        public EnbRenderPoseOwner? PoseOwner {get;internal set;}
        internal LinkedListNode<Node>? Link;
    }
    private readonly LinkedList<Node> _active=new();
    private readonly List<TState> _orderedStates=[];
    public IReadOnlyList<TState> OrderedStates=>_orderedStates;
    private int StateIndex(TState state)
    {for(int i=0;i<_orderedStates.Count;i++)if(ReferenceEquals(_orderedStates[i],state))return i;return -1;}
    private readonly Dictionary<ulong,Node> _identities=[];
    private readonly Dictionary<uint,Node> _firstGameIds=[];
    private readonly bool _indexGameIds;
    public long GameIdIndexMutations {get;private set;}
    private readonly List<Node> _retired=[];
    private readonly Func<EnbSceneObjectSnapshot,TState> _create;
    private readonly Action<TState,EnbSceneObjectSnapshot> _update;
    private readonly Action<TState> _release;
    private readonly Action<TState>? _detach;
    public EnbRetainedRenderScene(Func<EnbSceneObjectSnapshot,TState> create,
        Action<TState,EnbSceneObjectSnapshot> update,Action<TState> release,Action<TState>? detach=null,bool indexGameIds=false)
    {_create=create;_update=update;_release=release;_detach=detach;_indexGameIds=indexGameIds;}
    public LinkedList<Node> ActiveNodes=>_active;
    public int Count=>_active.Count;
    public long AppliedChanges {get;private set;}
    public long MutationLookups {get;private set;}
    public long ReleasedOwners {get;private set;}
    private readonly bool _traceLookups=Environment.GetEnvironmentVariable("ENB_TRACE_SCENE_LOOKUPS")=="1";
    public Dictionary<string,long> ExternalLookupSites {get;}=[];
    public Dictionary<string,string> ExternalLookupStacks {get;}=[];
    public long ExternalLookups {get;private set;}
    public long SuccessfulExternalLookups {get;private set;}

    public void ApplyThrough(EnbRenderSceneSubscription subscription,ulong sequence)
    {
        while(subscription.TryReadThrough(sequence,out var batch))
            foreach(var change in batch.Changes)Apply(change);
    }
    private void Apply(EnbRenderObjectChange change)
    {
        AppliedChanges++;
        switch(change.Kind)
        {
            case EnbRenderObjectChangeKind.Reset:
                foreach(var node in _active){_detach?.Invoke(node.State);node.Link=null;_retired.Add(node);}
                _active.Clear();_orderedStates.Clear();_identities.Clear();_firstGameIds.Clear();break;
            case EnbRenderObjectChangeKind.Add:
                var added=new Node(change.Definition!,_create(change.Definition!)){Slot=change.Slot,PoseOwner=change.PoseOwner};
                if(change.After is {} after)
                {
                    MutationLookups++;
                    var preceding=_identities[after];
                    added.Link=_active.AddAfter(preceding.Link!,added);
                    _orderedStates.Insert(StateIndex(preceding.State)+1,added.State);
                }
                else {added.Link=_active.AddFirst(added);_orderedStates.Insert(0,added.State);}
                _identities.Add(change.Identity,added);IndexGameId(added);break;
            case EnbRenderObjectChangeKind.Definition:
                MutationLookups++;
                var updated=_identities[change.Identity];
                bool changedGameId=updated.Definition.GameId!=change.Definition!.GameId;
                if(changedGameId)UnindexGameId(updated);
                _update(updated.State,change.Definition!);updated.Definition=change.Definition!;
                if(changedGameId)IndexGameId(updated);break;
            case EnbRenderObjectChangeKind.Remove:
                MutationLookups++;
                if(_identities.Remove(change.Identity,out var removed))
                {UnindexGameId(removed);_active.Remove(removed.Link!);_orderedStates.RemoveAt(StateIndex(removed.State));_detach?.Invoke(removed.State);removed.Link=null;_retired.Add(removed);}
                break;
            default:throw new ArgumentOutOfRangeException(nameof(change));
        }
    }
    // Explicit endpoint queries share a mutation-maintained index. Duplicate
    // game IDs preserve the ordered view's FirstOrDefault semantics; only a
    // membership/definition change can require an ordering scan.
    private void IndexGameId(Node node)
    {
        if(!_indexGameIds)return;
        GameIdIndexMutations++;
        if(!_firstGameIds.TryGetValue(node.Definition.GameId,out var first) ||
            StateIndex(node.State)<StateIndex(first.State))_firstGameIds[node.Definition.GameId]=node;
    }
    private void UnindexGameId(Node node)
    {
        if(!_indexGameIds)return;
        GameIdIndexMutations++;
        if(!_firstGameIds.TryGetValue(node.Definition.GameId,out var first) || !ReferenceEquals(first,node))return;
        _firstGameIds.Remove(node.Definition.GameId);
        foreach(var candidate in _active)
            if(!ReferenceEquals(candidate,node) && candidate.Definition.GameId==node.Definition.GameId)
            {_firstGameIds.Add(candidate.Definition.GameId,candidate);break;}
    }
    public bool TryFindByGameId(uint gameId,out Node node)
    {
        if(!_indexGameIds)throw new InvalidOperationException("Game ID queries require the mutation-maintained index.");
        ExternalLookups++;
        bool found=_firstGameIds.TryGetValue(gameId,out node!);
        if(found)SuccessfulExternalLookups++;
        return found;
    }
    /// <summary>For explicit owner queries such as target selection, not the frame walk.</summary>
    public bool TryFind(ulong identity,out Node node,[System.Runtime.CompilerServices.CallerMemberName] string caller="")
    {
        ExternalLookups++;
        bool found=_identities.TryGetValue(identity,out node!);
        if(found)SuccessfulExternalLookups++;
        if(_traceLookups)
        {
            string key=caller+":"+identity+(found?":base"+node.Definition.BaseAsset:":unowned");
            ExternalLookupSites[key]=ExternalLookupSites.GetValueOrDefault(key)+1;
            if(!ExternalLookupStacks.ContainsKey(key))ExternalLookupStacks.Add(key,Environment.StackTrace);
        }
        return found;
    }
    public void CompleteConsumption()
    {
        foreach(var node in _retired){_release(node.State);ReleasedOwners++;}
        _retired.Clear();
    }
}
