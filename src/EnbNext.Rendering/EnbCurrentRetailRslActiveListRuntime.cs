namespace EnbNext.Rendering;

/// <summary>Embedded RSL list0045BE40, enable00457E10, disable004582E0,
/// and post-callback next-pointer traversal0045A870.</summary>
public sealed class EnbCurrentRetailRslActiveListRuntime
{
    private sealed class Node(int? stateId)
    {
        public int? StateId { get; } = stateId;
        public Node? Next { get; set; }
        public Node? Previous { get; set; }
    }

    private readonly Node _start = new(null);
    private readonly Node _end = new(null);
    private readonly Dictionary<int, Node> _nodes = [];

    private readonly Action? _changed;
    public EnbCurrentRetailRslActiveListRuntime(Action? changed=null)
    {
        _changed=changed;
        _start.Next = _end;
        _end.Previous = _start;
    }

    private IReadOnlyList<int>? _snapshot;

    // Publish membership only after a list mutation. Visit remains the live
    // post-callback next-pointer walk; a snapshot must never replace it there.
    public IReadOnlyList<int> Snapshot()
    {
        if (_snapshot is not null) return _snapshot;
        var values = new List<int>();
        Visit(values.Add);
        return _snapshot = values.AsReadOnly();
    }

    public void Enable(int stateId)
    {
        if (!_nodes.TryGetValue(stateId, out var node)) _nodes.Add(stateId, node = new(stateId));
        _snapshot = null;_changed?.Invoke();
        Unlink(node);
        node.Previous = _end.Previous;
        node.Next = _end;
        node.Previous!.Next = node;
        _end.Previous = node;
    }

    public void Disable(int stateId)
    {
        if (_nodes.TryGetValue(stateId, out var node) && node.Next is not null)
        {
            _snapshot = null;_changed?.Invoke();
            Unlink(node);
        }
    }

    public void Visit(Action<int> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var node = _start.Next;
        while (node?.Next is not null)
        {
            callback(node.StateId!.Value);
            node = node.Next;
        }
    }

    public interface IVisitor { void Visit(int stateId); }

    // The visitor is invocation-local. Read Next after the callback exactly as
    //0045A870 does, including self-unlink and newly activated states.
    public void Visit<TVisitor>(ref TVisitor visitor) where TVisitor : struct,IVisitor
    {
        var node=_start.Next;
        while(node?.Next is not null)
        {
            visitor.Visit(node.StateId!.Value);
            node=node.Next;
        }
    }

    private static void Unlink(Node node)
    {
        if (node.Next is null || node.Previous is null) return;
        node.Previous.Next = node.Next;
        node.Next.Previous = node.Previous;
        node.Previous = null;
        node.Next = null;
    }
}
