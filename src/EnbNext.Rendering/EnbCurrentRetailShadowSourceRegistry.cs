namespace EnbNext.Rendering;

/// <summary>
/// Retained projector membership (00495430, 004926C0, 00492840, 00495160).
/// Sources are owned by their RSL wrappers: leaving the scene removes membership,
/// without discarding the wrapper's projection or changing its dirty state.
/// </summary>
public sealed class EnbCurrentRetailShadowSourceRegistry<T> where T : class
{
    private readonly List<T> _dynamic = [];
    private readonly List<T> _stationary = [];

    public int Count => _dynamic.Count + _stationary.Count;

    /// <summary>Register an already-created source projector. The caller applies
    /// the effective ShadowSource flag and owns creation/allocation failure.</summary>
    public bool Register(T source, bool hintStationary)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Contains(_dynamic, source) || Contains(_stationary, source)) return false;
        // Both native lists use head insertion (009D1040).
        (hintStationary ? _stationary : _dynamic).Insert(0, source);
        return true;
    }

    public bool Remove(T source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var list in new[] { _dynamic, _stationary })
        {
            int index = list.FindIndex(candidate => ReferenceEquals(candidate, source));
            if (index < 0) continue;
            list.RemoveAt(index);
            return true;
        }
        return false;
    }

    /// <summary>0075F8B0 updates and submits every dynamic source, then every
    /// stationary source. HintStationary does not select the static capture cache.
    /// The snapshot remains valid while the receiver owns its frame references.</summary>
    public IReadOnlyList<T> SnapshotTerrainSources() => [.. _dynamic, .. _stationary];

    public void Clear()
    {
        _dynamic.Clear();
        _stationary.Clear();
    }

    private static bool Contains(List<T> list, T source)
        => list.Exists(candidate => ReferenceEquals(candidate, source));
}
