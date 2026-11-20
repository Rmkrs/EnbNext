using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Ordinary texture ownership:0091DE00 retains the registry entry on lookup;
/// 0048F1A0 collects entries whose only remaining reference is the registry.
/// Keys and creation semantics are resolved by the caller. Transformed-name
/// aliases are not registered here and must be removed by their owning layer.
/// </summary>
public sealed class EnbCurrentRetailTextureRegistry
{
    internal sealed class Entry(EnbCurrentRetailTextureObjectState state)
    {
        public readonly EnbCurrentRetailTextureObjectState State = state;
        public int References;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbCurrentRetailTextureObjectState,Entry> _owners = new();
    public int Count => _entries.Count;

    public Lease Acquire(string resolvedKey, EnbW3dTextureSamplingSemantics creation, EnbW3dTextureInfo? info)
        => Acquire(resolvedKey, new EnbW3dTextureCreationRequest(creation, 0), info);

    public Lease Acquire(string resolvedKey, EnbW3dTextureCreationRequest creation, EnbW3dTextureInfo? info)
        => Acquire(resolvedKey,creation,info,out _);

    public Lease Acquire(string resolvedKey, EnbW3dTextureCreationRequest creation, EnbW3dTextureInfo? info,out bool created)
    {
        created=false;
        if (!_entries.TryGetValue(resolvedKey, out var entry))
        {
            created=true;
            entry = new(new(creation));
            _entries.Add(resolvedKey, entry);
            _owners.Add(entry.State,entry);
        }
        entry.State.ApplyW3dInfo(info);
        return new(entry);
    }

    public int CollectUnused()
    {
        var unused = _entries.Where(pair => Volatile.Read(ref pair.Value.References) == 0).Select(pair => pair.Key).ToArray();
        foreach (var key in unused) _entries.Remove(key);
        return unused.Length;
    }

    //0091E000 drops registry references; live descriptor/model references still
    // own their objects. A subsequent acquisition creates a new registry entry.
    public void Clear() => _entries.Clear();

    // Retain the exact object, including after its named entry was removed.
    // Generated private textures have no entry in this registry.
    public bool TryRetain(EnbCurrentRetailTextureObjectState state,out Lease lease)
    {
        if(_owners.TryGetValue(state,out var entry)) { lease=new(entry);return true; }
        lease=null!;return false;
    }

    public sealed class Lease : IDisposable
    {
        private Entry? _entry;
        internal Lease(Entry entry)
        {
            _entry = entry;
            Interlocked.Increment(ref entry.References);
        }
        private Entry Current => _entry ?? throw new ObjectDisposedException(nameof(Lease));
        public EnbCurrentRetailTextureObjectState State => Current.State;
        public Lease Retain() => new(Current);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _entry,null) is not {} entry) return;
            Interlocked.Decrement(ref entry.References);
        }
    }
}
