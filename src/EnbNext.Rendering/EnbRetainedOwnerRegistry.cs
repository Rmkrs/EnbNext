using System.Collections;

namespace EnbNext.Rendering;

/// <summary>Retained membership with owner-local traversal. A group's order is
/// compiled only when its membership changes, from the authoritative whole
/// collection order. Retired entries survive only in selections already held
/// by a consumer; the registry drops them immediately.</summary>
public sealed class EnbRetainedOwnerRegistry<TKey,TValue>(Func<TKey,ulong> ownerOf) : IReadOnlyDictionary<TKey,TValue>, IDictionary where TKey:notnull
{
    private sealed class Group
    {
        public int Count;
        public KeyValuePair<TKey,TValue>[]? Entries;
    }
    private readonly Dictionary<TKey,TValue> _items=[];
    private readonly Dictionary<ulong,Group> _owners=[];
    public long GroupPreparations {get;private set;}
    public int OwnerCount=>_owners.Count;
    public int Count=>_items.Count;
    public TValue this[TKey key]=>_items[key];
    public Dictionary<TKey,TValue>.KeyCollection Keys=>_items.Keys;
    public Dictionary<TKey,TValue>.ValueCollection Values=>_items.Values;
    IEnumerable<TKey> IReadOnlyDictionary<TKey,TValue>.Keys=>Keys;
    IEnumerable<TValue> IReadOnlyDictionary<TKey,TValue>.Values=>Values;
    public bool ContainsKey(TKey key)=>_items.ContainsKey(key);
    public bool TryGetValue(TKey key,out TValue value)=>_items.TryGetValue(key,out value!);
    public void Add(TKey key,TValue value)
    {
        _items.Add(key,value);
        ulong owner=ownerOf(key);
        if(!_owners.TryGetValue(owner,out var group))_owners.Add(owner,group=new());
        group.Count++;group.Entries=null;
    }
    public bool Remove(TKey key)
    {
        if(!_items.Remove(key))return false;
        ulong owner=ownerOf(key);var group=_owners[owner];
        group.Entries=null;
        if(--group.Count==0)_owners.Remove(owner);
        return true;
    }
    public void Clear(){_items.Clear();_owners.Clear();}
    private void Set(TKey key,TValue value)
    {
        if(!_items.ContainsKey(key)){Add(key,value);return;}
        _items[key]=value;_owners[ownerOf(key)].Entries=null;
    }
    object? IDictionary.this[object key] {get=>((IDictionary)_items)[key];set=>Set((TKey)key,(TValue)value!);}
    ICollection IDictionary.Keys=>_items.Keys;
    ICollection IDictionary.Values=>_items.Values;
    bool IDictionary.IsReadOnly=>false;
    bool IDictionary.IsFixedSize=>false;
    bool ICollection.IsSynchronized=>false;
    object ICollection.SyncRoot=>((ICollection)_items).SyncRoot;
    void IDictionary.Add(object key,object? value)=>Add((TKey)key,(TValue)value!);
    bool IDictionary.Contains(object key)=>((IDictionary)_items).Contains(key);
    void IDictionary.Remove(object key){if(key is TKey typed)Remove(typed);}
    IDictionaryEnumerator IDictionary.GetEnumerator()=>((IDictionary)_items).GetEnumerator();
    void ICollection.CopyTo(Array array,int index)=>((ICollection)_items).CopyTo(array,index);
    public Selection SelectCandidates(IReadOnlySet<ulong>? owners)
    {
        if(owners?.Count==1)
            foreach(ulong owner in owners)return ForOwner(owner);
        return new(_items,null);
    }
    public Selection ForOwner(ulong owner)
    {
        if(!_owners.TryGetValue(owner,out var group))return new(_items,[]);
        if(group.Entries is null)
        {
            var entries=new KeyValuePair<TKey,TValue>[group.Count];int index=0;
            foreach(var pair in _items)if(ownerOf(pair.Key)==owner)entries[index++]=pair;
            group.Entries=entries;GroupPreparations++;
        }
        return new(_items,group.Entries);
    }
    public Dictionary<TKey,TValue>.Enumerator GetEnumerator()=>_items.GetEnumerator();
    IEnumerator<KeyValuePair<TKey,TValue>> IEnumerable<KeyValuePair<TKey,TValue>>.GetEnumerator()=>GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator()=>((IDictionary)_items).GetEnumerator();
    public readonly struct Selection
    {
        private readonly Dictionary<TKey,TValue> _items;
        private readonly KeyValuePair<TKey,TValue>[]? _entries;
        internal Selection(Dictionary<TKey,TValue> items,KeyValuePair<TKey,TValue>[]? entries){_items=items;_entries=entries;}
        public Enumerator GetEnumerator()=>new(_items,_entries);
    }
    public struct Enumerator : IDisposable
    {
        private Dictionary<TKey,TValue>.Enumerator _all;
        private readonly KeyValuePair<TKey,TValue>[]? _entries;
        private int _index;
        internal Enumerator(Dictionary<TKey,TValue> items,KeyValuePair<TKey,TValue>[]? entries)
        {_all=entries is null?items.GetEnumerator():default;_entries=entries;_index=-1;}
        public KeyValuePair<TKey,TValue> Current=>_entries is null?_all.Current:_entries[_index];
        public bool MoveNext()=>_entries is null?_all.MoveNext():++_index<_entries.Length;
        public void Dispose(){if(_entries is null)_all.Dispose();}
    }
}
