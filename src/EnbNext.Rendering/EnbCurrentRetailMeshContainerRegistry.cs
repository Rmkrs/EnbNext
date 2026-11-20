namespace EnbNext.Rendering;

/// <summary>
/// Retained mesh-container admission and traversal (0097AC10/0097AE50),
/// separate from descriptor batching. Inputs are the resolved native FVF,
/// sorting branch and post-expansion buffer costs, not guessed from draw packets.
/// </summary>
public sealed class EnbCurrentRetailMeshContainerRegistry
{
    public sealed class Container
    {
        internal Container(uint fvf, bool skin, bool sorting, uint vertices, uint indices)
        {
            Fvf = skin ? 0x112u : fvf;
            IsSkin = skin;
            Sorting = sorting;
            // The native buffer constructors take unsigned 16-bit capacities.
            VertexCapacity = skin ? 0u : unchecked((ushort)Math.Max(4000u, vertices));
            IndexCapacity = unchecked((ushort)Math.Max(12000u, indices));
        }

        public int FlushOrdinal { get; internal set; }
        public uint Fvf { get; }
        public bool IsSkin { get; }
        public bool Sorting { get; }
        public uint VertexCapacity { get; }
        public uint IndexCapacity { get; }
        public uint UsedVertices { get; internal set; }
        public uint UsedIndices { get; internal set; }
        internal HashSet<object> Models { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private readonly List<List<Container>> _fvfLists = [];
    private readonly List<Container> _skins = [];
    private readonly Dictionary<object, Container> _models = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Container> _pendingRetirement = [];

    /// <summary>
    /// Register a model when its native batch-link list first materializes.
    /// Index cost includes every pass and any already-proven added triangles.
    /// Repeated instances of the same retained model share its registration.
    /// </summary>
    public Container Register(object model, uint fvf, bool skin, bool sorting,
        uint vertexCount, uint indexCount)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_models.TryGetValue(model, out var existing)) return existing;
        List<Container>? list = skin ? _skins :
            _fvfLists.FirstOrDefault(group => group.Count == 0 || group[0].Fvf == fvf);
        if (list is null)
        {
            list = [];
            _fvfLists.Add(list);
        }
        var container = list.FirstOrDefault(candidate => candidate.Sorting == sorting &&
            (skin || vertexCount <= unchecked(candidate.VertexCapacity - candidate.UsedVertices)) &&
            indexCount <= unchecked(candidate.IndexCapacity - candidate.UsedIndices));
        if (container is null)
        {
            container = new(fvf, skin, sorting, vertexCount, indexCount);
            list.Add(container);
            ReindexFlushOrder();
        }
        container.UsedVertices = unchecked(container.UsedVertices + (skin ? 0 : vertexCount));
        container.UsedIndices = unchecked(container.UsedIndices + indexCount);
        container.Models.Add(model);
        _models.Add(model, container);
        return container;
    }

    /// <summary>
    /// Called after the model's descriptor links are removed (0097ABA0/00977830).
    /// A surviving buffer is append-only; deletion does not recover its space.
    /// </summary>
    public void Remove(object model)
    {
        if (!_models.Remove(model, out var container)) return;
        container.Models.Remove(model);
        if (container.Models.Count == 0) _pendingRetirement.Add(container);
    }

    private void ReindexFlushOrder()
    {
        int index = 0;
        foreach (var group in _fvfLists)
            foreach (var container in group) container.FlushOrdinal = index++;
        foreach (var container in _skins) container.FlushOrdinal = index++;
    }

    public IReadOnlyList<Container> SnapshotFlushOrder()
        => _fvfLists.SelectMany(group => group).Concat(_skins).ToArray();

    /// <summary>
    /// 0097AB50 deletes queued empty containers after rendering. A late
    /// registration does not cancel that queued destruction; its model links
    /// are invalidated by the container/batch destructors too.
    /// </summary>
    public void EndFrame(Action<Container>? retire = null)
    {
        foreach (var container in _pendingRetirement)
        {
            retire?.Invoke(container);
            foreach (var model in container.Models) _models.Remove(model);
            container.Models.Clear();
            _skins.Remove(container);
            foreach (var group in _fvfLists) group.Remove(container);
        }
        if (_pendingRetirement.Count != 0) ReindexFlushOrder();
        _pendingRetirement.Clear();
    }

    public void Clear()
    {
        foreach (var container in SnapshotFlushOrder()) container.Models.Clear();
        _models.Clear();
        _pendingRetirement.Clear();
        _fvfLists.Clear();
        _skins.Clear();
    }
}
