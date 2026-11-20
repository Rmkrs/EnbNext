namespace EnbNext.Rendering;

/// <summary>Visible invocation tasks only. A scope retains packets until flush;
/// it neither discovers scene membership nor retains retired scene owners.</summary>
public sealed class EnbCurrentRetailMeshInvocationTasks
{
    private readonly object _token = new();
    private ulong _epoch = 1;
    private readonly List<List<EnbW3dDrawTask>> _groups = [];
    private readonly List<int> _firstOrdinals = [];
    public int Count => _firstOrdinals.Count;
    public void Add(EnbCurrentRetailMeshInvocation invocation, EnbW3dDrawTask packet, int ordinal)
    {
        if (!ReferenceEquals(invocation.TaskStorageToken, _token) || invocation.TaskStorageEpoch != _epoch)
        {
            invocation.TaskStorageToken = _token;
            invocation.TaskStorageEpoch = _epoch;
            invocation.TaskStorageSlot = Count;
            _firstOrdinals.Add(-1);
            if (_groups.Count == invocation.TaskStorageSlot) _groups.Add([]);
        }
        if (_firstOrdinals[invocation.TaskStorageSlot] < 0 && packet.CurrentRetailProjectedDecalOwner is null && packet.Mesh.CurrentRetailUsesMeshRenderer)
            _firstOrdinals[invocation.TaskStorageSlot] = ordinal;
        _groups[invocation.TaskStorageSlot].Add(packet);
    }
    public bool IsFirst(EnbCurrentRetailMeshInvocation invocation, int ordinal)
        => _firstOrdinals[Slot(invocation)] == ordinal;
    public List<EnbW3dDrawTask> Values(EnbCurrentRetailMeshInvocation invocation)
        => _groups[Slot(invocation)];
    private int Slot(EnbCurrentRetailMeshInvocation invocation)
    {
        if (!ReferenceEquals(invocation.TaskStorageToken, _token) || invocation.TaskStorageEpoch != _epoch)
            throw new InvalidOperationException("Invocation is outside its visible-task scope.");
        return invocation.TaskStorageSlot;
    }
    public void Clear()
    {
        for (int i = 0; i < Count; i++) _groups[i].Clear();
        _firstOrdinals.Clear();
        _epoch++;
    }
}

/// <summary>Signed-byte static-sort levels, visited in first-admission order.
/// Only active task lists are cleared after a flush.</summary>
public sealed class EnbCurrentRetailStaticSortTasks
{
    private readonly List<EnbW3dDrawTask>?[] _levels = new List<EnbW3dDrawTask>?[256];
    private readonly List<sbyte> _active = [];
    public int Count => _active.Count;
    public int Key(int index) => _active[index];
    public List<EnbW3dDrawTask> Values(int index) => _levels[_active[index] + 128]!;
    public void Add(sbyte level, EnbW3dDrawTask packet)
    {
        var tasks = _levels[level + 128] ??= [];
        if (tasks.Count == 0) _active.Add(level);
        tasks.Add(packet);
    }
    public void Clear()
    {
        foreach (sbyte level in _active) _levels[level + 128]!.Clear();
        _active.Clear();
    }
}
