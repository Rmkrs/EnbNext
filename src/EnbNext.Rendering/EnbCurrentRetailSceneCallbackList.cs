namespace EnbNext.Rendering;

//965E00(type0)/9D1040 prepend unique owners;9661C0 reads Next after each
//callback. Model LIFO node reuse within this registry. Native9D13A0 uses a
//process-wide pool; unrelated lists' allocator reuse is not modeled here.
public sealed class EnbCurrentRetailSceneCallbackRegistry
{
    internal sealed class Entry(object family,object value,Action invoke)
    {
        public readonly object Family=family;
        public object Value=value;
        public Action Invoke=invoke;
        public object? Reservation;
        public bool Ready=true;
    }
    private sealed class Node
    {
        public Entry Value=null!;
        public Node? Previous,Next;
    }
    private readonly Node _head=new();
    private readonly Dictionary<Entry,Node> _members=[];
    private readonly Stack<Node> _free=[];
    private int _walkDepth;

    internal void Register(Entry owner)
    {
        if(_members.ContainsKey(owner))return;
        var node=_free.Count==0?new Node():_free.Pop();
        node.Value=owner;node.Previous=_head;node.Next=_head.Next;
        if(node.Next is not null)node.Next.Previous=node;
        _head.Next=node;_members.Add(owner,node);
    }
    internal void Remove(Entry owner)
    {
        if(!_members.Remove(owner,out var node))return;
        node.Previous!.Next=node.Next;
        if(node.Next is not null)node.Next.Previous=node.Previous;
        //The native free operation overwrites the free-list word, not Next.
        _free.Push(node);
        if(_walkDepth==0)node.Value=null!;
    }
    internal void Visit(Action<Entry> callback)
    {
        _walkDepth++;
        try{for(var node=_head.Next;node is not null;node=node.Next)callback(node.Value);}
        finally
        {
            //Keep owner references while a mutable walk can still reach a
            //removed node, but do not retain retired assets in the node pool.
            if(--_walkDepth==0)foreach(var node in _free)node.Value=null!;
        }
    }
    public void Dispatch()=>Visit(entry=>entry.Invoke());
    internal void Clear(){_head.Next=null;_members.Clear();_free.Clear();}
}

/// <summary>A typed membership view sharing the native scene walk and node pool.</summary>
public sealed class EnbCurrentRetailSceneCallbackList<T> where T:notnull
{
    private readonly EnbCurrentRetailSceneCallbackRegistry _scene;
    private readonly bool _ownsScene;
    private readonly Dictionary<T,EnbCurrentRetailSceneCallbackRegistry.Entry> _members=[];
    private readonly Dictionary<object,EnbCurrentRetailSceneCallbackRegistry.Entry> _reservations=new(ReferenceEqualityComparer.Instance);
    private Action<T>? _callback;
    public EnbCurrentRetailSceneCallbackList(EnbCurrentRetailSceneCallbackRegistry? scene=null)
    {_ownsScene=scene is null;_scene=scene??new();}
    public void Register(T owner)
    {
        if(_members.ContainsKey(owner))return;
        var entry=new EnbCurrentRetailSceneCallbackRegistry.Entry(this,owner,()=>_callback?.Invoke(owner));
        _members.Add(owner,entry);_scene.Register(entry);
    }
    /// <summary>Bind dispatch at the actual scene attachment. The callback
    /// reads its owner's current frame inputs without an identity lookup.</summary>
    public void RegisterBound(T owner,Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if(_members.ContainsKey(owner))return;
        var entry=new EnbCurrentRetailSceneCallbackRegistry.Entry(this,owner,callback);
        _members.Add(owner,entry);_scene.Register(entry);
    }
    // A physical scene attachment can precede preparation of its callback's
    // managed payload. Allocate its native list position at the attachment,
    // then bind the payload without moving the node or changing pool reuse.
    public void Reserve(object identity)
    {
        if(_reservations.ContainsKey(identity))return;
        var entry=new EnbCurrentRetailSceneCallbackRegistry.Entry(this,identity,()=>{})
            {Reservation=identity,Ready=false};
        _reservations.Add(identity,entry);_scene.Register(entry);
    }
    public void Register(T owner,object reservation)
    {
        Reserve(reservation);
        var entry=_reservations[reservation];
        if(_members.TryGetValue(owner,out var previous) && !ReferenceEquals(previous,entry))
            throw new InvalidOperationException("Callback payload already has another scene registration");
        entry.Value=owner;entry.Invoke=()=>_callback?.Invoke(owner);entry.Ready=true;
        _members[owner]=entry;
    }
    public void RemoveReservation(object identity)
    {
        if(!_reservations.Remove(identity,out var entry))return;
        if(entry.Ready)_members.Remove((T)entry.Value);
        _scene.Remove(entry);
    }
    public void Remove(T owner)
    {
        if(!_members.Remove(owner,out var entry))return;
        if(entry.Reservation is {} reservation)_reservations.Remove(reservation);
        _scene.Remove(entry);
    }
    public void Visit(Action<T> callback)
        =>_scene.Visit(entry=>{if(entry.Ready && ReferenceEquals(entry.Family,this))callback((T)entry.Value);});
    public void SetCallback(Action<T> callback)=>_callback=callback;
    public void Clear()
    {
        foreach(var entry in _members.Values)_scene.Remove(entry);
        foreach(var entry in _reservations.Values)_scene.Remove(entry);
        _reservations.Clear();
        _members.Clear();_callback=null;
        if(_ownsScene)_scene.Clear();
    }
}
