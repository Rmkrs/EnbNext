using System.Runtime.CompilerServices;

namespace EnbNext.Rendering;

/// <summary>Reusable visible-task arena for one native mesh flush. Retail
/// 009776F0 prepends ordinary tasks; 00977940/00977B00 append extra tasks.
/// Only active categories are traversed. Draining releases all queued owners.</summary>
public sealed class EnbCurrentRetailMeshBatchQueue<T>
{
    private readonly record struct Key(int Container, int Pass, object? Identity, int Kind)
    {
        public bool Equals(Key other) => Container == other.Container && Pass == other.Pass &&
            Kind == other.Kind && ReferenceEquals(Identity, other.Identity);
        public override int GetHashCode() => HashCode.Combine(Container, Pass, Kind,
            Identity is null ? 0 : RuntimeHelpers.GetHashCode(Identity));
    }
    private struct Task(T value, int next)
    {
        public T Value = value;
        public int Next = next;
    }
    private readonly Dictionary<Key, EnbCurrentRetailMeshQueueCategory> _active = [];
    private readonly List<EnbCurrentRetailMeshQueueCategory> _categories = [];
    private readonly List<Task> _tasks = [];
    private readonly List<EnbCurrentRetailMeshQueueCategory> _scratchCategories=[];
    private int _scratchUsed;
    private static readonly Comparison<EnbCurrentRetailMeshQueueCategory> Order = static (a, b) =>
    {
        int c = (a.Kind == 2).CompareTo(b.Kind == 2);
        if (c == 0) c = a.Container.CompareTo(b.Container);
        if (c == 0) c = a.Kind.CompareTo(b.Kind);
        if (c == 0) c = a.Pass.CompareTo(b.Pass);
        return c != 0 ? c : b.Activation.CompareTo(a.Activation);
    };

    // Identity has no back-reference to this queue. A retained descriptor can
    // remember its binding without keeping a destroyed view alive.
    private readonly object _identity=new();
    private ulong _epoch=1;
    public long CategoryBindingBuildCount {get;private set;}
    public long CategoryLookupCount {get;private set;}
    public int TaskCount => _tasks.Count;
    public int ActiveCategoryCount => _categories.Count;
    public void Enqueue(int containerOrder, int pass, object batchIdentity, T instance)
    {
        ArgumentNullException.ThrowIfNull(batchIdentity);
        if(batchIdentity is EnbCurrentRetailMeshCategory retained)
        {
            var category=retained.Bind(_identity,containerOrder,pass,out bool created);
            if(created)CategoryBindingBuildCount++;
            if(category.Epoch!=_epoch)
            {
                category.Epoch=_epoch;category.Activation=_categories.Count;category.Identity=retained;
                category.Head=category.Tail=-1;_categories.Add(category);
            }
            Add(category,instance);
        }
        else Add(new Key(containerOrder, pass, batchIdentity, 0), instance);
    }
    public void EnqueueAdditionalPass(int containerOrder, T instance) => Add(new Key(containerOrder, 0, null, 1), instance);
    public void EnqueueDeferredAdditionalPass(int containerOrder, T instance) => Add(new Key(containerOrder, 0, null, 2), instance);
    public void EnqueueEffectPass(int containerOrder, bool skinContainer, uint contextFlags, T instance)
    {
        if (!skinContainer && (contextFlags & 4) != 0) EnqueueDeferredAdditionalPass(containerOrder, instance);
        else EnqueueAdditionalPass(containerOrder, instance);
    }
    private void Add(Key key, T instance)
    {
        CategoryLookupCount++;
        if (!_active.TryGetValue(key, out var category))
        {
            if(_scratchUsed==_scratchCategories.Count)_scratchCategories.Add(new(0,0,null,0));
            category=_scratchCategories[_scratchUsed++];
            category.Container=key.Container;category.Pass=key.Pass;category.Identity=key.Identity;category.Kind=key.Kind;
            category.Activation=_categories.Count;category.Head=category.Tail=-1;
            _active.Add(key,category);_categories.Add(category);
        }
        Add(category,instance);
    }
    private void Add(EnbCurrentRetailMeshQueueCategory category,T instance)
    {
        int task = _tasks.Count;
        _tasks.Add(new(instance, category.Kind == 0 ? category.Head : -1));
        if (category.Kind == 0 || category.Head < 0) category.Head = task;
        else
        {
            var tail = _tasks[category.Tail];
            tail.Next = task;
            _tasks[category.Tail] = tail;
        }
        category.Tail = task;
    }
    /// <summary>Append into caller-owned scratch. Safe after owner removal:
    /// queued references survive until this drain, including on failure.</summary>
    public void FlushInto(ICollection<T> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        try
        {
            _categories.Sort(Order);
            foreach (var category in _categories)
                for (int node = category.Head; node >= 0; node = _tasks[node].Next)
                    destination.Add(_tasks[node].Value);
        }
        finally { Clear(); }
    }
    public IReadOnlyList<T> Flush()
    {
        var result = new List<T>(_tasks.Count);
        FlushInto(result);
        return result;
    }
    public void Clear()
    {
        // A queued category owns its descriptor only until drain. Keeping that
        // reference in a descriptor-owned weak-table value would form a rooted
        // dependent-handle cycle while the queue identity remains alive.
        foreach(var category in _categories)category.Identity=null;
        _scratchUsed=0;
        _epoch++;
        _active.Clear();
        _categories.Clear();
        _tasks.Clear();
    }
}

// These bindings belong to the immutable descriptor identity, not to a frame
// snapshot. Only active bindings enter the queue's flush list. Multiple views
// retain independent heads/epochs even when their model categories are shared.
internal sealed class EnbCurrentRetailMeshQueueCategory(int container,int pass,object? identity,int kind)
{
    internal int Container=container,Pass=pass,Kind=kind;
    internal object? Identity=identity;
    internal ulong Epoch;
    internal int Activation,Head=-1,Tail=-1;
}
internal abstract class EnbCurrentRetailMeshCategory
{
    private sealed class QueueBindings(object identity)
    {
        internal readonly object Identity=identity;
        internal readonly List<EnbCurrentRetailMeshQueueCategory> Values=[];
        internal EnbCurrentRetailMeshQueueCategory? Last;
    }
    private readonly ConditionalWeakTable<object,QueueBindings> _bindings=new();
    private QueueBindings? _lastQueue;
    internal EnbCurrentRetailMeshQueueCategory Bind(object queue,int container,int pass,out bool created)
    {
        // Different views may consume a shared descriptor on different threads.
        // Publish the queue-specific cache as one reference, never separate key
        // and value fields. A queue itself retains its existing single-writer rule.
        var bindings=Volatile.Read(ref _lastQueue);
        if(bindings is null || !ReferenceEquals(bindings.Identity,queue))
        {
            bindings=_bindings.GetValue(queue,static identity=>new(identity));
            Volatile.Write(ref _lastQueue,bindings);
        }
        created=false;
        if(bindings.Last is {} last && last.Container==container && last.Pass==pass)return last;
        foreach(var binding in bindings.Values)
            if(binding.Container==container && binding.Pass==pass)return bindings.Last=binding;
        var result=new EnbCurrentRetailMeshQueueCategory(container,pass,null,0);
        bindings.Values.Add(result);bindings.Last=result;created=true;return result;
    }
}
