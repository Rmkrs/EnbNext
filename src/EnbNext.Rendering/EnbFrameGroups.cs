namespace EnbNext.Rendering;

/// <summary>Insertion-ordered scratch grouping with reusable backing storage.
/// Clear releases keys and values, including retired renderer owners.</summary>
public sealed class EnbFrameGroups<TKey, TValue>(IEqualityComparer<TKey>? comparer = null) where TKey : notnull
{
    private readonly Dictionary<TKey, int> _indices = new(comparer);
    private readonly List<TKey> _keys = [];
    private readonly List<List<TValue>> _values = [];
    public int Count => _keys.Count;
    public TKey Key(int index) => _keys[index];
    public List<TValue> Values(int index) => _values[index];
    public List<TValue> this[TKey key] => _values[_indices[key]];
    public bool TryGetValues(TKey key, out List<TValue> values)
    {
        if (_indices.TryGetValue(key, out int index)) { values=_values[index]; return true; }
        values=null!; return false;
    }
    public void Add(TKey key, TValue value)
    {
        if (!_indices.TryGetValue(key, out int index))
        {
            index = _keys.Count;
            _indices.Add(key, index); _keys.Add(key);
            if (index == _values.Count) _values.Add([]);
        }
        _values[index].Add(value);
    }
    public void Clear()
    {
        for (int i = 0; i < _keys.Count; i++) _values[i].Clear();
        _indices.Clear(); _keys.Clear();
    }
}
